using System.Text.Json;

namespace PingForge.App.ViewModels;

/// <summary>
/// Настройки на диске: обычный JSON рядом с базой службы. Лежит здесь, а не в слое Windows, по одной
/// причине — так их можно проверить тестами: и удачную запись, и битый файл, и отсутствие папки.
///
/// Запись идёт через временный файл: если питание выключится в момент сохранения, настройки не
/// превратятся в мусор — либо прежние, либо новые, но не половина.
/// </summary>
public sealed class JsonFileSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonFileSettingsStore(string path) =>
        SettingsFilePath = path ?? throw new ArgumentNullException(nameof(path));

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

        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsFilePath);
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
            LastLoadProblemRu = $"Файл настроек повреждён ({ex.Message}). Взяты обычные значения, файл не перезаписан. Путь: {SettingsFilePath}";
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadProblemRu = $"Файл настроек не прочитался ({ex.Message}). Взяты обычные значения. Путь: {SettingsFilePath}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        LastSaveProblemRu = null;

        try
        {
            string? directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = SettingsFilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, SettingsFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSaveProblemRu = $"Настройки не сохранились: {ex.Message}. Путь: {SettingsFilePath}";
            throw;
        }
    }
}
