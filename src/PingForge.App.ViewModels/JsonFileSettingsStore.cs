using System.Text.Json;
using System.Text.Json.Nodes;
using PingForge.Core.Security;

namespace PingForge.App.ViewModels;

/// <summary>
/// Настройки на диске: обычный JSON рядом с базой службы. Лежит здесь, а не в слое Windows, по одной
/// причине — так их можно проверить тестами: и удачную запись, и битый файл, и отсутствие папки.
///
/// Этот файл общий с службой Windows: служба пишет в него свой профиль потребления. Поэтому чтение
/// и запись идут через <see cref="SharedSettingsFileWriter"/> — под общим мьютексом, с копией прежней
/// версии в <c>.bak</c> и с сохранением битого файла в сторону вместо молчаливого затирания.
/// Без этого последний записавший затирал поле первого: человек терял пути к играм или пройденный
/// мастер, и программа об этом не сообщала.
/// </summary>
public sealed class JsonFileSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly SharedSettingsFileWriter _writer;

    public JsonFileSettingsStore(string path)
        : this(path, new SharedSettingsFileWriter(path ?? throw new ArgumentNullException(nameof(path))))
    {
    }

    /// <summary>Своя блокировка — для тестов: два писателя в одном процессе должны спорить по-настоящему.</summary>
    public JsonFileSettingsStore(string path, SharedSettingsFileWriter writer)
    {
        SettingsFilePath = path ?? throw new ArgumentNullException(nameof(path));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public string SettingsFilePath { get; }

    /// <summary>
    /// Что было не так при чтении файла. Пусто — всё в порядке или файла ещё нет.
    /// Приложение показывает это пользователю: молча взять «обычные значения» нельзя, он бы не понял,
    /// почему настройки сбросились.
    /// </summary>
    public string? LastLoadProblemRu { get; private set; }

    /// <summary>Ошибка записи: файл не сохранился. Пусто — запись прошла.</summary>
    public string? LastSaveProblemRu { get; private set; }

    public AppSettings Load()
    {
        LastLoadProblemRu = null;

        if (!_writer.TryRead(out string json, out string problem))
        {
            LastLoadProblemRu = $"Файл настроек не прочитался ({problem}). Взяты обычные значения. Путь: {SettingsFilePath}";
            return new AppSettings();
        }

        if (json.Length == 0)
        {
            // Файла ещё нет — это первый запуск, а не поломка.
            return new AppSettings();
        }

        try
        {
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings is null)
            {
                LastLoadProblemRu = $"Файл настроек пуст или содержит null: {SettingsFilePath}. Взяты обычные значения.";
                return new AppSettings();
            }

            return settings;
        }
        catch (JsonException ex)
        {
            // Битый файл убирается в сторону: причина потери настроек должна остаться на диске,
            // а следующая запись не обязана решать, что делать с мусором.
            string broken = _writer.QuarantineIfBroken(ex.Message);
            LastLoadProblemRu = broken.Length == 0
                ? $"Файл настроек повреждён ({ex.Message}). Взяты обычные значения. Путь: {SettingsFilePath}"
                : $"Файл настроек повреждён ({ex.Message}). Взяты обычные значения, а битый файл сохранён рядом для разбора: {broken}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        LastSaveProblemRu = null;

        try
        {
            JsonObject ours = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();

            // Пишем поверх прочитанного, а не вместо него: в этом же файле живут поля, которых окно
            // не знает (служба пишет свои), и запись «целиком из своей копии» стирала их молча.
            // Раньше здесь стояло Update(_ => json) — то есть общий файл переписывался снимком,
            // сделанным при запуске окна.
            SharedFileWriteResult result = _writer.Update(root =>
            {
                foreach ((string key, JsonNode? value) in ours)
                {
                    root[key] = value?.DeepClone();
                }

                return root;
            });

            if (!result.Written)
            {
                LastSaveProblemRu = $"Настройки не сохранились: {result.ProblemRu}. Путь: {SettingsFilePath}";
                throw new IOException(result.ProblemRu);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSaveProblemRu ??= $"Настройки не сохранились: {ex.Message}. Путь: {SettingsFilePath}";
            throw;
        }
    }
}
