using PingForge.App.ViewModels;
using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Файл настроек: запись, чтение и — главное — битый файл. Настройки лежат в разделе «Настройки»,
/// и пользователь должен видеть их путь; поэтому проверяется и то, что путь настоящий.
/// </summary>
public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory;

    public AppSettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"pf_settings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathIn(params string[] parts) => Path.Combine([_directory, .. parts]);

    [Fact]
    public void Файла_нет_берутся_обычные_значения_и_это_не_ошибка()
    {
        var store = new JsonFileSettingsStore(PathIn("settings.json"));

        AppSettings settings = store.Load();

        Assert.Equal("balanced", settings.PerfProfile);
        Assert.True(settings.OfferRestorePoint);
        Assert.True(settings.CloseToTray);
        Assert.Null(store.LastLoadProblemRu);
    }

    [Fact]
    public void Настройки_сохраняются_и_читаются_обратно()
    {
        var store = new JsonFileSettingsStore(PathIn("settings.json"));
        var written = new AppSettings("economy", OfferRestorePoint: false, StartMinimized: true, CloseToTray: false);

        store.Save(written);
        AppSettings read = new JsonFileSettingsStore(store.SettingsFilePath).Load();

        Assert.Equal(written, read);
        Assert.Null(store.LastSaveProblemRu);
    }

    [Fact]
    public void При_сохранении_не_остаётся_временных_файлов()
    {
        var store = new JsonFileSettingsStore(PathIn("settings.json"));
        store.Save(new AppSettings("max"));

        Assert.True(File.Exists(store.SettingsFilePath));
        Assert.False(File.Exists(store.SettingsFilePath + ".tmp"), "временный файл обязан быть переименован, а не оставлен");
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Папки_нет_она_создаётся()
    {
        var store = new JsonFileSettingsStore(PathIn("вложенная", "папка", "settings.json"));

        store.Save(new AppSettings("max"));

        Assert.True(File.Exists(store.SettingsFilePath));
    }

    [Fact]
    public void Битый_файл_не_роняет_программу_и_об_этом_сказано()
    {
        string path = PathIn("settings.json");
        File.WriteAllText(path, "{ это не json ");

        var store = new JsonFileSettingsStore(path);
        AppSettings settings = store.Load();

        Assert.Equal("balanced", settings.PerfProfile);
        Assert.NotNull(store.LastLoadProblemRu);
        Assert.Contains("повреждён", store.LastLoadProblemRu);
        Assert.Contains(path, store.LastLoadProblemRu);

        // Важно: битый файл не перезаписывается молча — иначе жалко потерять то, что там уцелело.
        // Он убирается в сторону с меткой времени: содержимое остаётся на диске и его можно разобрать,
        // а следующая запись начинается с чистого файла вместо решения «что делать с мусором».
        Assert.False(File.Exists(path), "битый файл обязан быть убран в сторону, а не оставлен на месте записи");
        string broken = Assert.Single(Directory.GetFiles(_directory, "settings.json.broken-*"));
        Assert.Contains("не json", File.ReadAllText(broken));
        Assert.Contains(broken, store.LastLoadProblemRu);
    }

    [Fact]
    public void Пустой_файл_это_тоже_повод_сказать_а_не_молчать()
    {
        string path = PathIn("settings.json");
        File.WriteAllText(path, "null");

        var store = new JsonFileSettingsStore(path);

        Assert.Equal("balanced", store.Load().PerfProfile);
        Assert.NotNull(store.LastLoadProblemRu);
    }

    [Fact]
    public void Настройки_попадают_в_окно_настроек_вместе_с_путём_к_файлу()
    {
        string path = PathIn("settings.json");
        var store = new JsonFileSettingsStore(path);
        store.Save(new AppSettings("max", CloseToTray: false));

        var settings = new SettingsViewModel(new EngineClient(new FakeEngineChannel()), store, new FakeDialogs());

        Assert.Equal(path, settings.SettingsFilePath);
        Assert.Equal(PerfProfile.MaxPerformance, settings.Profile);
        Assert.False(settings.CloseToTray);
        Assert.Contains(path, settings.SettingsFilePath);
    }

    [Fact]
    public async Task Сохранение_из_окна_пишет_файл_и_говорит_что_имя_настоящее()
    {
        string path = PathIn("settings.json");
        var store = new JsonFileSettingsStore(path);
        var settings = new SettingsViewModel(new EngineClient(new FakeEngineChannel()), store, new FakeDialogs())
        {
            Profile = PerfProfile.Economy,
            OfferRestorePoint = false,
        };

        await settings.SaveCommand.ExecuteAsync();

        Assert.Contains("Настройки сохранены", settings.StatusRu);
        Assert.Contains(path, settings.StatusRu);
        Assert.Equal("economy", new JsonFileSettingsStore(path).Load().PerfProfile);
    }
}
