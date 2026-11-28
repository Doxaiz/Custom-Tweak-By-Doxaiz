using System.Text.Json;
using PingForge.App.ViewModels;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Экспорт и импорт настроек: файл либо целый, либо его нет.
/// </summary>
/// <remarks>
/// Копия настроек — единственное, что человек уносит с машины на новую. Поэтому проверяется не только
/// «сохранилось», но и что сбой посреди записи не съел прошлую копию: файл пишется во временный рядом
/// и подменяется одним движением. Тесты работают на настоящем диске во временной папке: подделка
/// файловой системы ничего не сказала бы про подмену.
/// </remarks>
public sealed class SettingsExportTests
{
    [Fact]
    public async Task Экспорт_пишет_готовый_файл_и_не_оставляет_временных()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "PingForge-настройки.json");
        var model = Build(new InMemorySettingsStore(new AppSettings(SetupCompleted: true)));

        await model.ExportToFileAsync(path);

        Assert.Contains("сохранены", model.StatusRu, StringComparison.Ordinal);
        Assert.True(File.Exists(path));

        // Временный файл живёт до подмены и после неё не остаётся: иначе рядом с копией рос бы мусор.
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + SettingsExportViewModel.TempSuffix));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("Settings").GetProperty("SetupCompleted").GetBoolean());
        Assert.Contains("новом компьютере", json.RootElement.GetProperty("NoteRu").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Сбой_записи_не_портит_прошлую_копию()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "PingForge-настройки.json");
        const string было = "{\"SchemaVersion\":1,\"NoteRu\":\"старая копия\",\"Settings\":{}}";
        await File.WriteAllTextAsync(path, было);

        // Место временного файла заняла папка — запись не начнётся. Ровно так же ведёт себя
        // заполненный диск: новая копия не дописывается, а прежняя остаётся нетронутой.
        Directory.CreateDirectory(path + SettingsExportViewModel.TempSuffix);

        var model = Build(new InMemorySettingsStore());
        await model.ExportToFileAsync(path);

        Assert.Contains("Не удалось сохранить", model.StatusRu, StringComparison.Ordinal);
        Assert.Equal(было, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Экспорт_в_несуществующую_папку_честно_говорит_о_сбое()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "нет-такой-папки", "настройки.json");
        var model = Build(new InMemorySettingsStore());

        await model.ExportToFileAsync(path);

        Assert.Contains("Не удалось сохранить", model.StatusRu, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("сохранены", model.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Импорт_чужой_схемы_отказывает_и_не_трогает_настройки()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "чужая.json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":99,\"ExportedUtc\":\"2026-01-01T00:00:00Z\",\"NoteRu\":\"из будущего\",\"Settings\":{\"SetupCompleted\":true}}");
        var store = new InMemorySettingsStore();
        var model = Build(store);

        await model.ImportFromFileAsync(path);

        Assert.Contains("не поддерживается", model.StatusRu, StringComparison.Ordinal);
        Assert.Equal(0, store.SaveCount);
        Assert.False(store.Load().SetupCompleted);
    }

    [Fact]
    public async Task Импорт_своей_копии_заменяет_настройки_после_подтверждения()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "PingForge-настройки.json");
        var source = Build(new InMemorySettingsStore(new AppSettings(SetupCompleted: true)));
        await source.ExportToFileAsync(path);

        var store = new InMemorySettingsStore();
        var dialogs = new FakeDialogs { ConfirmAnswer = true };
        var model = new SettingsExportViewModel(store, dialogs);

        await model.ImportFromFileAsync(path);

        Assert.Equal(1, store.SaveCount);
        Assert.True(store.Load().SetupCompleted);
        Assert.Contains("заменены", model.StatusRu, StringComparison.Ordinal);
        Assert.Equal("Заменить всё", dialogs.Confirmations[0].ConfirmLabel);
    }

    private static SettingsExportViewModel Build(IAppSettingsStore store) =>
        new(store, new FakeDialogs());

    /// <summary>Временная папка, которая убирает за собой всё, что в ней появилось.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingforge-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Временная папка, которую не дали убрать, — не повод валить тест: он уже сказал своё.
            }
        }
    }
}
