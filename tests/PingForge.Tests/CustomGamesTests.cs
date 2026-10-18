using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Свои игры (задача A): человек добавляет .exe, которого нет в списке известных.
///
/// Проверяется вся цепочка, а не одна кнопка: запись в общий файл настроек, каталог
/// «встроенные + свои», детектор (своя — игра с неизвестным анти-читом, удалённая — снова
/// «кандидат, а не игра»), отказы на плохом пути и то, что встроенные 20 незыблемы.
/// </summary>
public sealed class CustomGamesTests
{
    private static readonly RawConnectionRow[] OneFlow =
    [
        new(ConnectionProtocol.Udp, 4242, "192.168.1.50", 50000, "34.16.1.5", 3074, ""),
    ];

    private static readonly Dictionary<int, string> MyGameProcess = new() { [4242] = "MyGame.exe" };

    // ── каталог и детектор ────────────────────────────────────────────────────────────────

    [Fact]
    public void Своя_игра_матчится_детектором_с_неизвестным_античитом()
    {
        var catalog = GameProcessCatalog.WithCustomGames([new CustomGameEntry("mygame.exe", "Моя игра")]);
        var detector = new GameFlowDetector(catalog);

        GameFlowDetectionReport report = detector.Detect(OneFlow, MyGameProcess);

        DetectedGameFlow found = Assert.Single(report.Games);
        Assert.Equal("Моя игра", found.GameTitleRu);
        Assert.Equal(AntiCheatStatus.Untested, found.Flow.AntiCheat);
        Assert.True(catalog.TryMatch("mygame.exe", out KnownGame known));
        Assert.True(known.IsCustom);
        Assert.Equal("своя игра: анти-чит неизвестен", known.NoteRu);
    }

    [Fact]
    public void Без_своей_игры_тот_же_процесс_кандидат_а_не_игра()
    {
        // «Удаление возвращает кандидат»: каталог без своей записи не называет процесс игрой.
        var detector = new GameFlowDetector(new GameProcessCatalog());

        GameFlowDetectionReport report = detector.Detect(OneFlow, MyGameProcess);

        Assert.Empty(report.Games);
        Assert.Contains(report.UnknownCandidates, candidate => candidate.ProcessName.Contains("MyGame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Битый_список_своих_игр_не_ломает_каталог()
    {
        var catalog = GameProcessCatalog.WithCustomGames(
        [
            null!,
            new CustomGameEntry("", ""),
            new CustomGameEntry("cs2.exe", "Дубликат встроенной"),
            new CustomGameEntry("steam.exe", "Лаунчер"),
            new CustomGameEntry("mygame.exe", "Моя игра"),
            new CustomGameEntry("MYGAME.EXE", "Дубликат своей"),
        ]);

        Assert.Equal(GameProcessCatalog.DefaultGames.Count + 1, catalog.All.Count);
        Assert.Single(catalog.CustomGames);
        Assert.Equal(20, GameProcessCatalog.DefaultGames.Count);
    }

    // ── валидация добавления ──────────────────────────────────────────────────────────────

    [Fact]
    public void Плохой_путь_и_не_exe_отклоняются_с_причиной()
    {
        Assert.False(CustomGameValidation.TryCreate(@"C:\Games\nope.exe", "Игра", _ => false, [], out _, out string missing));
        Assert.Contains("нет", missing);

        Assert.False(CustomGameValidation.TryCreate(@"C:\Games\game.dll", "Игра", _ => true, [], out _, out string notExe));
        Assert.Contains(".exe", notExe);

        Assert.False(CustomGameValidation.TryCreate(@"C:\Games\game.exe", " ", _ => true, [], out _, out string noTitle));
        Assert.Contains("Название", noTitle);

        Assert.False(CustomGameValidation.TryCreate(@"C:\Games\cs2.exe", "Дубль", _ => true, ["cs2.exe"], out _, out string duplicate));
        Assert.Contains("уже есть", duplicate);
    }

    // ── окно: добавить, переименовать, удалить ────────────────────────────────────────────

    [Fact]
    public async Task Окно_добавляет_переименовывает_и_удаляет_свою_игру()
    {
        (GameLauncherViewModel launcher, InMemorySettingsStore store) = BuildLauncher();
        string exe = Path.Combine(Path.GetTempPath(), "mygame.exe");
        File.WriteAllBytes(exe, []);

        try
        {
            int builtins = launcher.Cards.Count;
            await launcher.AddCustomGameAsync(exe, "Моя игра");

            GameCardViewModel card = Assert.Single(launcher.Cards, candidate => candidate.IsCustom);
            Assert.Equal("Моя игра", card.TitleRu);
            Assert.Contains("своя игра: анти-чит неизвестен", card.NoteRu);
            Assert.Equal(builtins + 1, launcher.Cards.Count);
            Assert.Contains(store.Load().CustomGames!, game => game.ProcessName == "mygame.exe");

            Assert.True(launcher.RenameCustomGame(card, "Моя игра 2"));
            Assert.Equal("Моя игра 2", card.TitleRu);

            Assert.True(launcher.RemoveCustomGame(card));
            Assert.Equal(builtins, launcher.Cards.Count);
            Assert.Empty(store.Load().CustomGames!);

            // Встроенную удалить нельзя.
            GameCardViewModel builtin = launcher.Cards[0];
            Assert.False(launcher.RemoveCustomGame(builtin));
            Assert.Equal(builtins, launcher.Cards.Count);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public async Task Окно_отклоняет_несуществующий_файл()
    {
        (GameLauncherViewModel launcher, _) = BuildLauncher();

        await launcher.AddCustomGameAsync(@"C:\Games\no-such-game.exe", "Призрак");

        Assert.DoesNotContain(launcher.Cards, card => card.IsCustom);
        Assert.Contains("не добавлена", launcher.StatusRu);
    }

    // ── общий файл настроек ───────────────────────────────────────────────────────────────

    [Fact]
    public void Свои_игры_переживают_запись_чужих_настроек_в_общий_файл()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf_custom_games_{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileSettingsStore(path);
            store.Save(new AppSettings(CustomGames: [new CustomGameEntry("mygame.exe", "Моя игра")]));

            string text = File.ReadAllText(path);
            Assert.Contains("\"CustomGames\"", text);
            Assert.Contains("mygame.exe", text);

            // Второй писатель меняет только своё поле — свои игры обязаны остаться.
            AppSettings current = store.Load();
            store.Save(current with { PerfProfile = "max" });

            AppSettings reloaded = store.Load();
            Assert.Equal("max", reloaded.PerfProfile);
            Assert.Single(reloaded.CustomGames!);
            Assert.Equal("Моя игра", reloaded.CustomGames![0].TitleRu);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (GameLauncherViewModel Launcher, InMemorySettingsStore Store) BuildLauncher()
    {
        var store = new InMemorySettingsStore();
        var engine = new EngineClient(new FakeEngineChannel());
        var main = new MainViewModel(engine, new FakeDialogs());
        var launcher = new GameLauncherViewModel(
            new FakeProbe(), new FakeStarter(), new FakePicker(), store, main);
        return (launcher, store);
    }

    private sealed class FakeProbe : IGameExecutableProbe
    {
        public IReadOnlyDictionary<string, string> FindInstalled(IReadOnlyCollection<string> processNames) =>
            new Dictionary<string, string>();
    }

    private sealed class FakeStarter : IGameStarter
    {
        public bool TryStart(string executablePath, out string errorRu)
        {
            errorRu = string.Empty;
            return true;
        }

        public bool IsRunning(string processName) => false;
    }

    private sealed class FakePicker : IExecutablePicker
    {
        public string? Pick(string titleRu) => null;
    }
}
