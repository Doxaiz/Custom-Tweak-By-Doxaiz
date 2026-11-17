using System.Text.Json.Nodes;
using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Core.Security;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Запись файлов и падения от неё. Два настоящих дефекта, найденных при общем разборе:
///
///   1. временный файл рядом с настройками был один и тот же на всех («settings.json.tmp»), а писать
///      без общей блокировки здесь разрешено — два писателя затирали временный файл друг друга,
///      и на место настроек мог встать обрывок;
///   2. мастер первого запуска писал настройки без обработки ошибки: при обычном запуске без прав
///      администратора запись в %ProgramData% не проходит, и первый же запуск выглядел как падение
///      программы с отчётом о сбое.
/// </summary>
public sealed class WriteRobustnessTests : IDisposable
{
    private readonly string _directory;

    public WriteRobustnessTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"pf_write_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathIn(string name) => Path.Combine(_directory, name);

    private SharedSettingsFileWriter IsolatedWriter(string path, string tag) =>
        new(path, $@"Local\PingForge.Write.Test.{tag}.{Guid.NewGuid():N}", $"PingForge.Write.Test.{tag}.{Guid.NewGuid():N}");

    [Fact]
    public void После_записи_временный_файл_не_остаётся()
    {
        string path = PathIn("settings.json");
        SharedFileWriteResult result = IsolatedWriter(path, "one").Update(root =>
        {
            root["PerfProfile"] = "balanced";
            return root;
        });

        Assert.True(result.Written, result.ProblemRu);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal("balanced", JsonNode.Parse(File.ReadAllText(path))!["PerfProfile"]!.GetValue<string>());
    }

    [Fact]
    public async Task Два_писателя_без_общей_блокировки_не_оставляют_обрывок()
    {
        // «Общей блокировки нет» — это не выдумка теста: так бывает, когда мьютекс создать не удалось,
        // и писатель по правилам продолжает работу с предупреждением.
        string path = PathIn("settings.json");
        SharedSettingsFileWriter left = IsolatedWriter(path, "left");
        SharedSettingsFileWriter right = IsolatedWriter(path, "right");

        for (int i = 0; i < 30; i++)
        {
            string leftValue = $"left-{i}";
            string rightValue = $"right-{i}";
            await Task.WhenAll(
                Task.Run(() => left.Update(root => { root["Left"] = leftValue; return root; })),
                Task.Run(() => right.Update(root => { root["Right"] = rightValue; return root; })));
        }

        // Главное: файл всегда остаётся целым JSON, а не обрывком одного из писателей.
        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(path));
        Assert.NotNull(parsed);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Имя_временного_файла_уникально_и_лежит_рядом()
    {
        string path = PathIn("settings.json");
        string first = SharedSettingsFileWriter.TempPathNear(path);
        string second = SharedSettingsFileWriter.TempPathNear(path);

        Assert.NotEqual(first, second);
        Assert.Equal(_directory, Path.GetDirectoryName(first));
        Assert.EndsWith(".tmp", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Мастер_первого_запуска_не_падает_если_настройки_не_записались()
    {
        // Так выглядит обычный запуск без прав администратора: файл настроек в %ProgramData%
        // недоступен на запись. Раньше исключение улетало из команды кнопки и закрывало программу.
        var store = new FailingSaveStore();
        OnboardingViewModel setup = BuildSetup(store);

        Exception? thrown = Record.Exception(() => setup.CloseWithoutApplying());

        Assert.Null(thrown);
        Assert.False(setup.IsVisible);
        Assert.True(store.SaveAttempts > 0, "запись должна была состояться — иначе тест ничего не проверил");
        Assert.Contains("не сохранился", setup.ResultRu, StringComparison.Ordinal);
        Assert.Contains("права", setup.ResultRu, StringComparison.OrdinalIgnoreCase);
    }

    private static OnboardingViewModel BuildSetup(IAppSettingsStore store)
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        var dialogs = new FakeDialogs();
        var engine = new EngineClient(new FakeEngineChannel());
        var tweaks = new TweaksViewModel(
            engine,
            dialogs,
            TweakRowFactory.BuildAll(catalog),
            TweakRowFactory.BuildPresets(catalog));
        var settings = new SettingsViewModel(engine, store, dialogs);
        return new OnboardingViewModel(store, tweaks, settings);
    }

    /// <summary>Хранилище, которое не может записать файл — как папка настроек без прав на запись.</summary>
    private sealed class FailingSaveStore : IAppSettingsStore
    {
        private AppSettings _settings = new();

        public string SettingsFilePath => @"C:\ProgramData\PingForge\settings.json";

        public int SaveAttempts { get; private set; }

        public AppSettings Load() => _settings;

        public void Save(AppSettings settings)
        {
            SaveAttempts++;
            throw new UnauthorizedAccessException("доступ к пути запрещён");
        }
    }
}
