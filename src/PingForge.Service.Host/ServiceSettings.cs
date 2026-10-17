using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.Service.Host;

/// <summary>
/// Профиль потребления из того же файла настроек, который пишет окно программы.
///
/// Почему отдельный маленький класс, а не общая модель: служба не должна зависеть от WPF-проекта
/// (и от его библиотек), а файл — общий. Читаем его терпимо: нет файла или он битый — берём «обычный»
/// профиль и говорим об этом в журнале; чужие поля не трогаем, при записи меняем только свой.
/// </summary>
internal static class ServiceSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath(string dataDirectory) => Path.Combine(dataDirectory, "settings.json");

    /// <summary>Профиль из файла. Непрочитанный профиль — «обычно», а не «экономно»: не выдумываем.</summary>
    public static PerfProfile ReadProfile(string dataDirectory)
    {
        string path = FilePath(dataDirectory);

        try
        {
            if (!File.Exists(path))
            {
                return PerfProfile.Balanced;
            }

            string json = File.ReadAllText(path);
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
    /// Записать профиль, не затирая остального: мастер первого запуска и пути к играм лежат в том же файле.
    /// Запись через временный файл — как в окне программы: половины файла не бывает.
    /// </summary>
    public static bool TryWriteProfile(string dataDirectory, PerfProfile profile)
    {
        string path = FilePath(dataDirectory);

        try
        {
            Directory.CreateDirectory(dataDirectory);

            JsonObject root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            // Сохраняем чужой регистр ключа, если он уже есть: файл принадлежит не службе.
            string key = root.Select(pair => pair.Key)
                .FirstOrDefault(name => string.Equals(name, "PerfProfile", StringComparison.OrdinalIgnoreCase))
                ?? "PerfProfile";

            root[key] = EngineNames.ProfileWireName(profile);

            string temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
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
