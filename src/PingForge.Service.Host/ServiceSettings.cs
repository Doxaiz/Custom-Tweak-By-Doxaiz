using System.IO;
using System.Text.Json;
using PingForge.Core.Security;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.Service.Host;

/// <summary>
/// Профиль потребления из того же файла настроек, который пишет окно программы.
///
/// Почему отдельный маленький класс, а не общая модель: служба не должна зависеть от WPF-проекта
/// (и от его библиотек), а файл — общий. Читаем его терпимо: нет файла или он битый — берём «обычный»
/// профиль; чужие поля не трогаем, при записи меняем только свой.
///
/// Файл общий, и писателей двое (окно и служба), поэтому чтение и запись идут через
/// <see cref="SharedSettingsFileWriter"/> — под общим для обоих процессов мьютексом. Раньше служба
/// делала «прочитать весь файл → заменить свой ключ → записать», и при одновременном сохранении
/// в окне последний записавший затирал чужие поля: человек терял пути к играм или пройденный мастер
/// первого запуска, а программа об этом не сообщала.
/// </summary>
internal static class ServiceSettings
{
    public static string FilePath(string dataDirectory) => Path.Combine(dataDirectory, "settings.json");

    /// <summary>Писатель общего файла: тот же путь и то же имя блокировки, что у окна программы.</summary>
    private static SharedSettingsFileWriter Writer(string dataDirectory) => new(FilePath(dataDirectory));

    /// <summary>Профиль из файла. Непрочитанный профиль — «обычно», а не «экономно»: не выдумываем.</summary>
    public static PerfProfile ReadProfile(string dataDirectory)
    {
        try
        {
            if (!Writer(dataDirectory).TryRead(out string json, out _))
            {
                return PerfProfile.Balanced;
            }

            if (json.Length == 0)
            {
                // Файла ещё нет: первый запуск службы до первого сохранения в окне.
                return PerfProfile.Balanced;
            }

            string? wire = ReadStringIgnoreCase(json, "PerfProfile");

            return wire is not null && EngineNames.TryParseProfile(wire, out PerfProfile profile)
                ? profile
                : PerfProfile.Balanced;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PerfProfile.Balanced;
        }
    }

    /// <summary>
    /// Записать профиль, не затирая остального: мастер первого запуска, пути к играм и свои игры лежат
    /// в том же файле и принадлежат окну программы.
    /// </summary>
    public static bool TryWriteProfile(string dataDirectory, PerfProfile profile)
    {
        SharedFileWriteResult result = Writer(dataDirectory).Update(
            root =>
            {
                // Сохраняем чужой регистр ключа, если он уже есть: файл принадлежит не службе.
                string key = root.Select(pair => pair.Key)
                    .FirstOrDefault(name => string.Equals(name, "PerfProfile", StringComparison.OrdinalIgnoreCase))
                    ?? "PerfProfile";

                root[key] = EngineNames.ProfileWireName(profile);
                return root;
            },
            // Битый файл служба не затирает и не переименовывает: это файл окна, и решение о том,
            // что с ним делать, принимает окно. Служба просто честно не пишет.
            quarantineBroken: false);

        return result.Written;
    }

    private static string? ReadStringIgnoreCase(string json, string property)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty candidate in document.RootElement.EnumerateObject())
        {
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)
                && candidate.Value.ValueKind == JsonValueKind.String)
            {
                return candidate.Value.GetString();
            }
        }

        return null;
    }
}
