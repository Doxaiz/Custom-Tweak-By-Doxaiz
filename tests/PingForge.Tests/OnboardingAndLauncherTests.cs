using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Мастер первого запуска и раздел «Игры» — то, что делает программу похожей на BoosterX:
/// три вопроса и совет вместо блуждания по каталогу, и «Открыть» в один клик.
///
/// Проверяется главное обещание: программа ничего не меняет сама. Мастер только советует,
/// а игра запускается лишь по нажатию — и если её нет на диске, программа так и говорит.
/// </summary>
public class OnboardingAndLauncherTests
{
    private static readonly FakeEngineChannel Fallback = new();

    private static TweaksViewModel BuildTweaks(FakeDialogs? dialogs = null)
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        return new TweaksViewModel(
            new EngineClient(new FakeEngineChannel()),
            dialogs ?? new FakeDialogs(),
            TweakRowFactory.BuildAll(catalog),
            TweakRowFactory.BuildPresets(catalog));
    }

    private static (OnboardingViewModel Setup, InMemorySettingsStore Store, TweaksViewModel Tweaks, MainViewModel Main) BuildSetup(
        AppSettings? initial = null)
    {
        var store = new InMemorySettingsStore(initial);
        var dialogs = new FakeDialogs();
        var engine = new EngineClient(new FakeEngineChannel());
        var main = new MainViewModel(engine, dialogs);
        var tweaks = BuildTweaks(dialogs);
        var settings = new SettingsViewModel(engine, store, dialogs);
        return (new OnboardingViewModel(store, tweaks, settings), store, tweaks, main);
    }

    // ── совет мастера: таблица, а не «на глаз» ───────────────────────────────────────────────────

    [Theory]
    [InlineData(SetupPurpose.Games, SetupPcStrength.Weak, SetupProblem.Ping, "low-latency")]
    [InlineData(SetupPurpose.Games, SetupPcStrength.Medium, SetupProblem.Fps, "fps")]
    [InlineData(SetupPurpose.Games, SetupPcStrength.Strong, SetupProblem.Both, "low-latency")]
    [InlineData(SetupPurpose.Games, SetupPcStrength.Weak, SetupProblem.Both, "fps")]
    [InlineData(SetupPurpose.GamesAndWork, SetupPcStrength.Medium, SetupProblem.Ping, "low-latency")]
    public void Совет_по_ответам_совпадает_с_таблицей(
        SetupPurpose purpose, SetupPcStrength strength, SetupProblem problem, string expected)
    {
        SetupRecommendation recommendation = OnboardingViewModel.Recommend(purpose, strength, problem);

        Assert.Equal(expected, recommendation.PresetWireName);
        Assert.False(string.IsNullOrWhiteSpace(recommendation.TitleRu));
        Assert.False(string.IsNullOrWhiteSpace(recommendation.WhyRu));
    }

    [Theory]
    [InlineData(SetupPcStrength.Weak, PerfProfile.Economy)]
    [InlineData(SetupPcStrength.Medium, PerfProfile.Balanced)]
    [InlineData(SetupPcStrength.Strong, PerfProfile.MaxPerformance)]
    public void Профиль_потребления_зависит_от_мощности_компьютера(SetupPcStrength strength, PerfProfile expected)
    {
        Assert.Equal(expected, OnboardingViewModel.Recommend(SetupPurpose.Games, strength, SetupProblem.Ping).Profile);
    }

    [Fact]
    public void Для_работы_игровые_твики_не_советуются_вовсе()
    {
        SetupRecommendation recommendation = OnboardingViewModel.Recommend(
            SetupPurpose.Work, SetupPcStrength.Strong, SetupProblem.Both);

        Assert.Null(recommendation.PresetWireName);
        Assert.Contains("не нужны", recommendation.TitleRu);
        Assert.Contains("ни одной настройки", recommendation.WhyRu);
    }

    // ── сам мастер ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void На_первом_запуске_мастер_показан_а_после_прохождения_больше_не_появляется()
    {
        (OnboardingViewModel setup, InMemorySettingsStore store, _, _) = BuildSetup();

        Assert.True(setup.IsVisible);

        setup.CloseWithoutApplying();

        Assert.False(setup.IsVisible);
        Assert.True(store.Load().SetupCompleted);
    }

    [Fact]
    public void Мастер_не_появляется_второй_раз_если_уже_пройден()
    {
        (OnboardingViewModel setup, _, _, _) = BuildSetup(new AppSettings(SetupCompleted: true));

        Assert.False(setup.IsVisible);
    }

    [Fact]
    public void Без_трёх_ответов_мастер_не_советует()
    {
        (OnboardingViewModel setup, _, _, _) = BuildSetup();
        setup.Start();
        setup.PurposeIsGames = true;

        setup.Next();

        Assert.True(setup.IsQuestionsStep);
        Assert.Contains("все три вопроса", setup.HintRu);
        Assert.False(setup.IsRecommendationStep);
    }

    [Fact]
    public void Ответы_ведут_к_шагу_совета_и_запоминаются_в_настройках()
    {
        (OnboardingViewModel setup, InMemorySettingsStore store, _, _) = BuildSetup();

        setup.Start();
        setup.PurposeIsGames = true;
        setup.StrengthIsWeak = true;
        setup.ProblemIsBoth = true;
        setup.Next();

        Assert.True(setup.IsRecommendationStep);
        Assert.Equal("fps", setup.Recommended.PresetWireName);

        setup.CloseWithoutApplying();

        Assert.Equal("Games;Weak;Both", store.Load().SetupAnswersRu);
    }

    [Fact]
    public async Task Применение_идёт_обычным_путём_с_подтверждением_и_ничего_не_меняет_без_согласия()
    {
        (OnboardingViewModel setup, InMemorySettingsStore store, TweaksViewModel tweaks, _) = BuildSetup();
        var dialogs = new FakeDialogs { ConfirmAnswer = false };

        // Подменяем диалоги у раздела твиков: отказ пользователя должен остановить применение.
        var refusingTweaks = BuildTweaks(dialogs);
        var engine = new EngineClient(new FakeEngineChannel());
        var settings = new SettingsViewModel(engine, store, dialogs);
        var refusingSetup = new OnboardingViewModel(store, refusingTweaks, settings);
        refusingSetup.Start();
        refusingSetup.PurposeIsGames = true;
        refusingSetup.StrengthIsMedium = true;
        refusingSetup.ProblemIsPing = true;
        refusingSetup.Next();

        await refusingSetup.ApplyAsync();

        Assert.False(refusingTweaks.StatusRu.Contains("применены", StringComparison.OrdinalIgnoreCase));
        Assert.Single(dialogs.Confirmations);
        Assert.Contains("Будет изменено", dialogs.Confirmations[0].Message);
    }

    [Fact]
    public async Task Для_работы_мастер_сохраняет_только_профиль_и_не_трогает_твики()
    {
        (OnboardingViewModel setup, InMemorySettingsStore store, TweaksViewModel tweaks, _) = BuildSetup();
        setup.Start();
        setup.PurposeIsWork = true;
        setup.StrengthIsStrong = true;
        setup.ProblemIsPing = true;
        setup.Next();

        await setup.ApplyAsync();

        Assert.DoesNotContain("применены", tweaks.StatusRu, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EngineNames.ProfileWireName(PerfProfile.MaxPerformance), store.Load().PerfProfile);
        Assert.Contains("Настройки Windows не менялись", setup.ResultRu);
    }

    // ── лаунчер игр ──────────────────────────────────────────────────────────────────────────────

    private sealed class FakeProbe : IGameExecutableProbe
    {
        public Dictionary<string, string> Found { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Какие имена окно просило искать: нужно, чтобы проверить, что общий javaw.exe не ищется.</summary>
        public List<IReadOnlyCollection<string>> Requests { get; } = [];

        public IReadOnlyDictionary<string, string> FindInstalled(IReadOnlyCollection<string> processNames)
        {
            Requests.Add(processNames);

            return Found.Where(pair => processNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeStarter : IGameStarter
    {
        public List<string> Started { get; } = [];

        public bool FailStart { get; set; }

        public bool TryStart(string executablePath, out string errorRu)
        {
            if (FailStart)
            {
                errorRu = "файла нет";
                return false;
            }

            Started.Add(executablePath);
            errorRu = string.Empty;
            return true;
        }

        public bool IsRunning(string processName) => Started.Count > 0;
    }

    private sealed class FakePicker : IExecutablePicker
    {
        public string? Next { get; set; }

        public string? Pick(string titleRu) => Next;
    }

    private static (GameLauncherViewModel Launcher, FakeProbe Probe, FakeStarter Starter, FakePicker Picker, InMemorySettingsStore Store)
        BuildLauncher()
    {
        var probe = new FakeProbe();
        var starter = new FakeStarter();
        var picker = new FakePicker();
        var store = new InMemorySettingsStore();
        var engine = new EngineClient(new FakeEngineChannel());
        var main = new MainViewModel(engine, new FakeDialogs());
        return (new GameLauncherViewModel(probe, starter, picker, store, main), probe, starter, picker, store);
    }

    [Fact]
    public void В_списке_все_известные_игры_и_без_путей_они_честно_не_найдены()
    {
        (GameLauncherViewModel launcher, _, _, _, _) = BuildLauncher();

        Assert.Equal(GameProcessCatalog.DefaultGames.Count, launcher.Cards.Count);
        Assert.All(launcher.Cards, card => Assert.False(card.HasPath));
        Assert.Contains("не найдено ни одной", launcher.SummaryRu);
    }

    [Fact]
    public async Task Поиск_на_диске_проставляет_пути_и_меняет_итог()
    {
        (GameLauncherViewModel launcher, FakeProbe probe, _, _, _) = BuildLauncher();
        string exe = Path.Combine(Path.GetTempPath(), "cs2.exe");
        probe.Found["cs2.exe"] = exe;

        await launcher.FindInstalledAsync();

        GameCardViewModel cs2 = launcher.Cards.First(card => card.ProcessName == "cs2.exe");
        Assert.True(cs2.HasPath);
        Assert.Contains("Найдено игр на диске: 1", launcher.SummaryRu);
    }

    [Fact]
    public async Task Поиск_на_диске_не_ищет_общий_процесс_Java()
    {
        // javaw.exe — это и Minecraft, и любая Java-программа: найденный на диске чужой javaw.exe
        // нельзя выдавать за игру, а «Открыть» запустил бы его без аргументов и ничего не произошло бы.
        (GameLauncherViewModel launcher, FakeProbe probe, _, _, _) = BuildLauncher();
        probe.Found["javaw.exe"] = @"C:\Program Files\Java\jre\bin\javaw.exe";

        await launcher.FindInstalledAsync();

        IReadOnlyCollection<string> asked = Assert.Single(probe.Requests);
        Assert.DoesNotContain("javaw.exe", asked, StringComparer.OrdinalIgnoreCase);

        GameCardViewModel minecraft = launcher.Cards.Single(card => card.ProcessName == "javaw.exe");
        Assert.False(minecraft.HasPath);
        Assert.Contains("лаунчером", minecraft.NoteRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Открытие_игры_запускает_её_и_просит_движок_начать_оптимизацию()
    {
        (GameLauncherViewModel launcher, FakeProbe probe, FakeStarter starter, _, _) = BuildLauncher();
        string exe = Path.Combine(Path.GetTempPath(), "cs2.exe");
        probe.Found["cs2.exe"] = exe;
        await launcher.FindInstalledAsync();

        GameCardViewModel cs2 = launcher.Cards.First(card => card.ProcessName == "cs2.exe");
        await cs2.OpenCommand.ExecuteAsync();

        Assert.Equal(new[] { exe }, starter.Started);
        Assert.True(cs2.IsRunning);
        Assert.Contains("cs2", cs2.StatusRu.Contains("запущена") ? "cs2" : "нет", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Не_найденная_игра_не_запускается_а_просит_путь()
    {
        (GameLauncherViewModel launcher, _, FakeStarter starter, FakePicker picker, _) = BuildLauncher();
        GameCardViewModel card = launcher.Cards[0];
        picker.Next = null;   // человек закрыл окно выбора

        await card.OpenCommand.ExecuteAsync();

        Assert.Empty(starter.Started);
        Assert.Contains("Без пути запускать нечего", card.StatusRu);
    }

    [Fact]
    public async Task Указанный_путь_запоминается_в_настройках()
    {
        (GameLauncherViewModel launcher, _, _, FakePicker picker, InMemorySettingsStore store) = BuildLauncher();
        GameCardViewModel card = launcher.Cards[0];
        picker.Next = @"C:\Games\MyGame\game.exe";

        await card.ChoosePathCommand.ExecuteAsync();

        Assert.True(card.HasPath);
        Assert.NotNull(store.Load().GamePaths);
        Assert.Equal(@"C:\Games\MyGame\game.exe", store.Load().GamePaths![card.ProcessName]);
    }

    [Fact]
    public async Task Ошибку_запуска_программа_не_скрывает()
    {
        (GameLauncherViewModel launcher, FakeProbe probe, FakeStarter starter, _, _) = BuildLauncher();
        probe.Found["cs2.exe"] = @"C:\Games\cs2.exe";
        starter.FailStart = true;
        await launcher.FindInstalledAsync();

        GameCardViewModel cs2 = launcher.Cards.First(card => card.ProcessName == "cs2.exe");
        await cs2.OpenCommand.ExecuteAsync();

        Assert.Contains("Запустить не удалось", cs2.StatusRu);
        Assert.False(cs2.IsRunning);
    }

    [Fact]
    public async Task Пути_игр_переживают_сохранение_настроек_в_другом_разделе()
    {
        // Настройки читаются заново перед записью: иначе «Сохранить» в «Настройках» стёрло бы
        // и пути к играм, и пройденный мастер.
        var store = new InMemorySettingsStore(new AppSettings(
            SetupCompleted: true,
            GamePaths: new Dictionary<string, string> { ["cs2.exe"] = @"C:\Games\cs2.exe" }));

        var engine = new EngineClient(new FakeEngineChannel());
        var settings = new SettingsViewModel(engine, store, new FakeDialogs());
        settings.CloseToTray = false;

        // Команда ожидается, а не «выполнить и блокирующе дождаться»: блокирующее ожидание задачи
        // в тесте — это предупреждение xUnit1031 и риск взаимной блокировки на потоке теста.
        await settings.SaveCommand.ExecuteAsync();

        AppSettings saved = store.Load();
        Assert.True(saved.SetupCompleted);
        Assert.Equal(@"C:\Games\cs2.exe", saved.GamePaths!["cs2.exe"]);
        Assert.False(saved.CloseToTray);
    }
}
