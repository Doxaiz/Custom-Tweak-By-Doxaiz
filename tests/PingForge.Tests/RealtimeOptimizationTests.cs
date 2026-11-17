using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

public sealed class RealtimeOptimizationTests
{

    [Fact]
    public async Task Нет_игры_но_есть_похожее_соединение_объяснение_доходит_до_человека()
    {
        // Так выглядит запущенная игра, которой нет в каталоге: соединение есть, игры «официально» нет.
        // Отказ должен называть процесс и адрес, а не ограничиваться «не найдена».
        const string reason = "Похожее на игру соединение есть: процесс «MyGame.exe» на 34.16.1.5:3074.";
        var watcher = new ExplainingWatcher(reason);

        var optimizer = new RealtimeOptimizer(
            watcher,
            new ScriptedMeasurements(),
            new RecordingRunner());

        RealtimeSnapshot snapshot = await optimizer.StartAsync();

        Assert.Equal(RealtimeOptimizationState.Failed, snapshot.State);
        Assert.Contains("MyGame.exe", snapshot.StatusRu, StringComparison.Ordinal);
        Assert.Contains("34.16.1.5:3074", snapshot.StatusRu, StringComparison.Ordinal);

        // Таблица соединений читается один раз: объяснение приходит от того же прохода.
        Assert.Equal(1, watcher.Calls);
    }
    private sealed class StubWatcher : IGameFlowWatcher
    {
        public StubWatcher(params GameFlowInfo[] flows) => Flows = flows;

        public IReadOnlyList<GameFlowInfo> Flows { get; set; }

        public Task<IReadOnlyList<GameFlowInfo>> FindActiveGameFlowsAsync(CancellationToken ct = default) =>
            Task.FromResult(Flows);
    }

    /// <summary>Наблюдатель, который не нашёл игру, но умеет объяснить почему.</summary>
    private sealed class ExplainingWatcher : IGameFlowReporter
    {
        public ExplainingWatcher(string reasonRu) => ReasonRu = reasonRu;

        public string ReasonRu { get; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<GameFlowInfo>> FindActiveGameFlowsAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<GameFlowInfo>>(Array.Empty<GameFlowInfo>());
        }

        public Task<(IReadOnlyList<GameFlowInfo> Flows, string? NoGameReasonRu)> FindGameFlowsWithReasonAsync(
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<(IReadOnlyList<GameFlowInfo>, string?)>(
                (Array.Empty<GameFlowInfo>(), ReasonRu));
        }
    }

    /// <summary>Замеры идут по заранее заданной ленте: тест полностью определяет «реальность».</summary>
    private sealed class ScriptedMeasurements : IRealtimeMeasurementSource
    {
        private readonly Queue<PathMeasurement> _windows;

        public ScriptedMeasurements(params PathMeasurement[] windows) => _windows = new Queue<PathMeasurement>(windows);

        public PathMeasurement Fallback { get; set; } = Measurable(80, 3, 0);

        public int Calls { get; private set; }

        public Task<PathMeasurement> MeasurePathAsync(string endpoint, int port, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_windows.Count > 0 ? _windows.Dequeue() : Fallback);
        }
    }

    private sealed class RecordingRunner : IOptimizationActionRunner
    {
        public List<string> Applied { get; } = new();
        public List<string> Reverted { get; } = new();
        public HashSet<string> FailToApply { get; } = new();

        /// <summary>Общий журнал в порядке вызовов: по нему видно, что снятие было РАНЬШЕ добавления.</summary>
        public List<string> Events { get; } = new();

        /// <summary>Действия, которые реально дошли до исполнителя: узел маршрута виден только здесь.</summary>
        public List<OptimizationActionDescriptor> Seen { get; } = new();

        public Task<OptimizationActionOutcome> ApplyAsync(OptimizationActionDescriptor action, CancellationToken ct = default)
        {
            Seen.Add(action);
            Events.Add("apply:" + action.ActionId);

            if (FailToApply.Contains(action.ActionId))
            {
                return Task.FromResult(OptimizationActionOutcome.Fail("тестовый отказ применения"));
            }

            Applied.Add(action.ActionId);
            return Task.FromResult(OptimizationActionOutcome.Ok("применено"));
        }

        public Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct = default)
        {
            Events.Add("revert:" + action.ActionId);
            Reverted.Add(action.ActionId);
            return Task.FromResult(OptimizationActionOutcome.Ok("снято"));
        }
    }

    private sealed class StubPathOptions : IRealtimePathOptionsProvider
    {
        public StubPathOptions(params RealtimePathOption[] options) => Options = options;

        public IReadOnlyList<RealtimePathOption> Options { get; set; }

        public Task<IReadOnlyList<RealtimePathOption>> GetOptionsAsync(GameFlowInfo flow, CancellationToken ct = default) =>
            Task.FromResult(Options);
    }

    private static PathMeasurement Measurable(double rtt, double jitter = 2, double loss = 0) =>
        new(true, rtt, jitter, loss, 12, "Замер живого потока игры (SIO_TCP_INFO).");

    private static GameFlowInfo Fortnite(AntiCheatStatus antiCheat = AntiCheatStatus.VerifiedSafe) =>
        new(4242, "FortniteClient-Win64-Shipping", "Fortnite", "3.4.5.6", 9000, "UDP", antiCheat);

    private static RealtimeOptimizer Build(
        StubWatcher watcher,
        ScriptedMeasurements measurements,
        RecordingRunner runner,
        StubPathOptions? paths = null) =>
        new(watcher, measurements, runner, pathOptions: paths);

    [Fact]
    public async Task Start_MeasuresBaselineThenApplies_AndReportsMeasuredImprovement()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        // Три окна «до»: медиана 120, затем контрольное окно «после»: 80.
        var measurements = new ScriptedMeasurements(Measurable(122), Measurable(118), Measurable(120), Measurable(80));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        RealtimeSnapshot snapshot = await optimizer.StartAsync();

        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, snapshot.State);
        Assert.Equal(120, snapshot.BaselineRttMs!.Value, 0.01);
        Assert.Equal(80, snapshot.CurrentRttMs!.Value, 0.01);
        Assert.Equal(40, snapshot.ImprovementMs!.Value, 0.01);
        Assert.True(snapshot.HasMeasuredImprovement);
        Assert.Equal("Fortnite", snapshot.GameTitleRu);
        Assert.Equal("3.4.5.6:9000", snapshot.Endpoint);
        Assert.Equal("Терпимо", snapshot.QualityLabelRu);
        Assert.Contains("стало 80", snapshot.StatusRu);
        Assert.Contains(runner.Applied, a => a == "route:game-server");
        Assert.Contains(runner.Applied, a => a == "qos:dscp-46");
    }

    [Fact]
    public async Task Start_WithoutRunningGame_DoesNotInventNumbers()
    {
        var optimizer = Build(new StubWatcher(), new ScriptedMeasurements(), new RecordingRunner());

        RealtimeSnapshot snapshot = await optimizer.StartAsync();

        Assert.Equal(RealtimeOptimizationState.Failed, snapshot.State);
        Assert.Null(snapshot.BaselineRttMs);
        Assert.Null(snapshot.ImprovementMs);
        Assert.Contains("игра не найдена", snapshot.StatusRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WithoutAdministratorRights_OnlyMeasures()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(Measurable(120), Measurable(120), Measurable(120));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        RealtimeSnapshot snapshot = await optimizer.StartAsync(isAdministrator: false);

        Assert.Equal(RealtimeOptimizationState.ReadOnlyNoAdmin, snapshot.State);
        Assert.Equal(120, snapshot.BaselineRttMs!.Value, 0.01);
        Assert.Empty(runner.Applied);
        Assert.Contains("права администратора", snapshot.StatusRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KernelAntiCheatGame_GetsNoPacketFilter_OnlyRouteAndQos()
    {
        var watcher = new StubWatcher(Fortnite(AntiCheatStatus.KnownKernelConflict));
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(Measurable(120), Measurable(120), Measurable(120), Measurable(110));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        RealtimeSnapshot snapshot = await optimizer.StartAsync();

        Assert.Equal(2, snapshot.AppliedActions.Count);
        Assert.DoesNotContain(snapshot.AppliedActions, a => a.ActionId.Contains("filter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Warnings, w => w.Contains("ядерный анти-чит", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Warnings, w => w.Contains("фильтр поднимаем", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Degradation_TriesAnotherPath_AndKeepsOptimizationRunning()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var paths = new StubPathOptions(
            new RealtimePathOption("direct", "Прямое подключение", false, null,
                new OptimizationActionDescriptor("route:direct", OptimizationActionKind.RouteForGame, "Прямой путь", "тест")),
            new RealtimePathOption("relay:fra", "Релей Франкфурт", true, "10.0.0.1",
                new OptimizationActionDescriptor("route:relay:fra", OptimizationActionKind.RouteForGame, "Релей Франкфурт", "тест")));

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(130, jitter: 30, loss: 5),
            Measurable(132, jitter: 31, loss: 6),
            Measurable(131, jitter: 29, loss: 5),
            Measurable(78));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);

        // Порог «плохих» окон — 2, чтобы тест не гонял десятки окон.
        RealtimeSnapshot started = await optimizer.StartAsync(new RealtimeOptions(DegradedWindowsBeforeSwitch: 2));
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, started.State);

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.Contains(runner.Applied, a => a == "route:relay:fra");
        Assert.Contains(runner.Reverted, a => a == "route:direct");
        Assert.Contains(optimizer.Current.Warnings, w => w.Contains("Переключение пути", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(optimizer.Current.Warnings, w => w.Contains("Релей Франкфурт", StringComparison.Ordinal));
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);

        // Прежний маршрут снимается ДО добавления нового: иначе исполнитель упрётся в свой же /32
        // и «переключение» осталось бы на старом узле.
        int revertedAt = runner.Events.IndexOf("revert:route:direct");
        int appliedAt = runner.Events.IndexOf("apply:route:relay:fra");
        Assert.True(revertedAt >= 0 && appliedAt >= 0, string.Join(", ", runner.Events));
        Assert.True(revertedAt < appliedAt, string.Join(", ", runner.Events));
    }

    [Fact]
    public async Task Переключение_на_резерв_идёт_в_его_узел_а_не_в_узел_прежнего_пути()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var paths = new StubPathOptions(
            new RealtimePathOption("node:10.8.0.1", "«WG» — основной", true, "10.8.0.1",
                new OptimizationActionDescriptor("route:game-server", OptimizationActionKind.RouteForGame, "Маршрут через «WG»", "тест", RouteNextHop: "10.8.0.1")),
            new RealtimePathOption("node:10.0.0.5", "«LTE» — резерв", true, "10.0.0.5",
                new OptimizationActionDescriptor("route:game-server:runner-up", OptimizationActionKind.RouteForGame, "Маршрут через «LTE»", "тест", RouteNextHop: "10.0.0.5")));

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(130, jitter: 30, loss: 5),
            Measurable(132, jitter: 31, loss: 6));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(DegradedWindowsBeforeSwitch: 2));

        // Основной путь замеров назвал провайдер: активен именно он, а не «первый попавшийся».
        Assert.Contains(runner.Applied, a => a == "route:game-server");

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        OptimizationActionDescriptor switched = Assert.Single(runner.Seen, a => a.ActionId == "route:game-server:runner-up");
        Assert.Equal("10.0.0.5", switched.RouteNextHop);
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);
    }

    private static StubPathOptions MeasuredPaths(double activeMs, double alternativeMs) => new(
        new RealtimePathOption("node:10.8.0.1", "«WG» (10.8.0.1)", true, "10.8.0.1",
            new OptimizationActionDescriptor("route:game-server", OptimizationActionKind.RouteForGame, "Маршрут через «WG»", "тест", RouteNextHop: "10.8.0.1"),
            MedianRttMs: activeMs),
        new RealtimePathOption("node:10.0.0.5", "«LTE» (10.0.0.5) — резерв", true, "10.0.0.5",
            new OptimizationActionDescriptor("route:game-server:runner-up", OptimizationActionKind.RouteForGame, "Маршрут через «LTE»", "тест", RouteNextHop: "10.0.0.5"),
            MedianRttMs: alternativeMs));

    [Fact]
    public async Task Поиск_пути_быстрее_переключает_когда_альтернатива_ощутимо_быстрее()
    {
        // Так ведут себя игровые ускорители: пока текущий путь в норме, они всё равно перепроверяют
        // альтернативы и уходят на ту, что быстрее. Мы делаем это по измеренным числам сессии,
        // а не по «AI-предсказанию», и не чаще, чем раз в несколько минут.
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        StubPathOptions paths = MeasuredPaths(activeMs: 45, alternativeMs: 38);

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(80), Measurable(80));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(HealthyWindowsBetweenRouteReviews: 2));

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.Contains("route:game-server:runner-up", runner.Applied);
        Assert.Contains("route:game-server", runner.Reverted);
        Assert.Contains(optimizer.Current.Warnings, w => w.Contains("Поиск пути быстрее", StringComparison.Ordinal));
        Assert.Contains("в этой сессии замеров он на", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);
    }

    [Fact]
    public async Task Поиск_пути_быстрее_не_дёргает_маршрут_когда_выигрыш_в_пределах_порога()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        StubPathOptions paths = MeasuredPaths(activeMs: 45, alternativeMs: 44.2);

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(80), Measurable(80));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(HealthyWindowsBetweenRouteReviews: 2));

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.DoesNotContain("route:game-server:runner-up", runner.Applied);
        Assert.Contains("не быстрее текущего", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);
    }

    [Fact]
    public async Task Если_свежие_замеры_не_нашли_узла_быстрее_прямого_об_этом_сказано()
    {
        // Узел мог «перестать помогать»: тогда держать маршрут через него — самообман, но снять
        // маршрут на ходу нельзя. Честный ответ: сказать об этом и предложить остановить оптимизацию.
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var paths = new StubPathOptions(); // ни одного измеренного варианта

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(80));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(HealthyWindowsBetweenRouteReviews: 1));
        await optimizer.TickOnceAsync();

        Assert.Contains("не нашли узла быстрее прямого пути", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Contains("остановкой оптимизации", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);
    }

    [Fact]
    public async Task Поиск_пути_быстрее_уважает_паузу_после_переключения()
    {
        // Переключение — это разрыв соединения в игре. Поэтому после любого перехода даём маршруту
        // пожить: даже если замеры нашли путь ещё быстрее, раньше паузы не уходим.
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        StubPathOptions paths = MeasuredPaths(activeMs: 45, alternativeMs: 38);

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(80), Measurable(80),
            Measurable(80), Measurable(80));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(HealthyWindowsBetweenRouteReviews: 2));

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();
        Assert.Contains("route:game-server:runner-up", runner.Applied);

        // Появился «ещё более быстрый» третий путь — но пауза после переключения не прошла.
        paths.Options =
        [
            paths.Options[1],
            paths.Options[0],
            new RealtimePathOption("node:10.0.0.9", "«третий» (10.0.0.9)", true, "10.0.0.9",
                new OptimizationActionDescriptor("route:game-server:third", OptimizationActionKind.RouteForGame, "Маршрут через «третий»", "тест", RouteNextHop: "10.0.0.9"),
                MedianRttMs: 20)
        ];

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.DoesNotContain("route:game-server:third", runner.Applied);
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);
    }

    [Fact]
    public async Task После_переключения_на_быстрый_путь_прежний_остаётся_рабочим_кандидатом()
    {
        // Ушли на путь быстрее — он не «плохой», а просто медленнее. Значит, при просадке нового
        // контроль имеет право вернуться на прежний: он не помечен как неудачный.
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        StubPathOptions paths = MeasuredPaths(activeMs: 45, alternativeMs: 38);

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(80), Measurable(80),
            Measurable(150, jitter: 40, loss: 9),
            Measurable(151, jitter: 41, loss: 10));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(HealthyWindowsBetweenRouteReviews: 2, DegradedWindowsBeforeSwitch: 2));

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();
        Assert.Contains("route:game-server:runner-up", runner.Applied);

        // Просадка нового пути: возвращаемся на прежний — он рабочий, а не «провалившийся».
        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.Equal(2, runner.Applied.Count(a => a == "route:game-server"));
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, optimizer.Current.State);
    }

    [Fact]
    public async Task Отказ_нового_маршрута_возвращает_прежний_и_говорит_правду()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        runner.FailToApply.Add("route:game-server:runner-up");

        var paths = new StubPathOptions(
            new RealtimePathOption("node:10.8.0.1", "«WG» — основной", true, "10.8.0.1",
                new OptimizationActionDescriptor("route:game-server", OptimizationActionKind.RouteForGame, "Маршрут через «WG»", "тест", RouteNextHop: "10.8.0.1")),
            new RealtimePathOption("node:10.0.0.5", "«LTE» — резерв", true, "10.0.0.5",
                new OptimizationActionDescriptor("route:game-server:runner-up", OptimizationActionKind.RouteForGame, "Маршрут через «LTE»", "тест", RouteNextHop: "10.0.0.5")));

        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(140, jitter: 40, loss: 8),
            Measurable(141, jitter: 41, loss: 9));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(DegradedWindowsBeforeSwitch: 2));
        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        // Прежний маршрут вернули: без этого человек остался бы вообще без маршрута к серверу игры.
        Assert.Equal(2, runner.Applied.Count(a => a == "route:game-server"));
        Assert.Contains(optimizer.Current.Warnings, w => w.Contains("Прежний маршрут возвращён", StringComparison.Ordinal));
        Assert.Contains("не применился", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Equal(RealtimeOptimizationState.Degraded, optimizer.Current.State);
    }

    [Fact]
    public async Task На_путь_где_уже_было_хуже_второй_раз_не_переключаемся()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var paths = new StubPathOptions(
            new RealtimePathOption("node:10.8.0.1", "«WG» — основной", true, "10.8.0.1",
                new OptimizationActionDescriptor("route:game-server", OptimizationActionKind.RouteForGame, "Маршрут через «WG»", "тест", RouteNextHop: "10.8.0.1")),
            new RealtimePathOption("node:10.0.0.5", "«LTE» — резерв", true, "10.0.0.5",
                new OptimizationActionDescriptor("route:game-server:runner-up", OptimizationActionKind.RouteForGame, "Маршрут через «LTE»", "тест", RouteNextHop: "10.0.0.5")));

        // Сначала просадка основного (переход на резерв), затем просадка резерва: обратно на
        // «плохой» основной не идём — это был бы флаппинг на ровном месте.
        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(130, jitter: 30, loss: 5),
            Measurable(132, jitter: 31, loss: 6),
            Measurable(160, jitter: 50, loss: 10),
            Measurable(161, jitter: 51, loss: 11));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(DegradedWindowsBeforeSwitch: 2));

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();
        Assert.Single(runner.Seen, a => a.ActionId == "route:game-server:runner-up");

        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.Single(runner.Seen, a => a.ActionId == "route:game-server"); // применений основного ровно одно — в начале
        Assert.Contains("других путей не осталось", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Equal(RealtimeOptimizationState.Degraded, optimizer.Current.State);
    }

    [Fact]
    public async Task Сервер_сменился_маршрут_переставляется_а_числа_начинаются_заново()
    {
        // Матчмейкер выдал другой сервер, игра не перезапускалась: прежний /32 указывает на адрес,
        // где игры уже нет. Маршрут обязан переехать, а «до» — мериться заново: старый пинг
        // к другому серверу к новому не относится.
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        StubPathOptions paths = MeasuredPaths(activeMs: 45, alternativeMs: 38);

        var measurements = new ScriptedMeasurements(Measurable(80), Measurable(80), Measurable(80), Measurable(80));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(WindowsBetweenServerChecks: 1));
        Assert.Equal("3.4.5.6:9000", optimizer.Current.Endpoint);

        watcher.Flows = [Fortnite() with { RemoteEndpoint = "34.16.9.77", RemotePort = 9001 }];

        await optimizer.TickOnceAsync();

        Assert.Equal("34.16.9.77:9001", optimizer.Current.Endpoint);
        Assert.Contains("Сервер игры сменился", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Contains("34.16.9.77:9001", optimizer.Current.StatusRu, StringComparison.Ordinal);
        Assert.Equal(2, runner.Applied.Count(a => a == "route:game-server")); // прежний адрес и новый
        Assert.Contains("route:game-server", runner.Reverted);
        Assert.Null(optimizer.Current.BaselineRttMs); // «до» к новому серверу ещё не измерено

        // Отчёт при остановке не заявляет выигрыш: базовая линия относится к прежнему серверу.
        RealtimeStopReport report = await optimizer.StopAsync();
        Assert.Null(report.BaselineRttMs);
        Assert.Contains("недоступны", report.HonestSummaryRu, StringComparison.Ordinal);
        Assert.DoesNotContain("Пинг снизился", report.HonestSummaryRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Пока_прежний_адрес_виден_у_игры_сервер_не_считается_сменившимся()
    {
        // У игры бывает не одно соединение (матч + служебное). Пока прежний адрес на месте,
        // переставлять маршрут нельзя: «новое» соединение может быть вовсе не тем сервером.
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        StubPathOptions paths = MeasuredPaths(activeMs: 45, alternativeMs: 38);
        var measurements = new ScriptedMeasurements(Measurable(80), Measurable(80), Measurable(80), Measurable(80));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner, paths);
        await optimizer.StartAsync(new RealtimeOptions(WindowsBetweenServerChecks: 1));

        watcher.Flows =
        [
            Fortnite(),
            Fortnite() with { RemoteEndpoint = "34.16.9.77", RemotePort = 9001 }
        ];

        await optimizer.TickOnceAsync();

        Assert.Equal("3.4.5.6:9000", optimizer.Current.Endpoint);
        Assert.Single(runner.Applied, a => a == "route:game-server");
        Assert.Empty(runner.Reverted);
        Assert.DoesNotContain("сменился", optimizer.Current.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Degradation_WithoutAlternative_MarksDegraded_AndSaysTweaksCannotFixIt()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(
            Measurable(80), Measurable(80), Measurable(80), Measurable(80),
            Measurable(140, jitter: 40, loss: 8),
            Measurable(141, jitter: 41, loss: 9));

        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync(new RealtimeOptions(DegradedWindowsBeforeSwitch: 2));
        await optimizer.TickOnceAsync();
        await optimizer.TickOnceAsync();

        Assert.Equal(RealtimeOptimizationState.Degraded, optimizer.Current.State);
        Assert.Contains("твиками такое не лечится", optimizer.Current.StatusRu);
    }

    [Fact]
    public async Task Stop_RevertsEverythingInReverseOrder_AndReportsHonestly()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(Measurable(120), Measurable(120), Measurable(120), Measurable(80), Measurable(82));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync();
        RealtimeStopReport report = await optimizer.StopAsync();

        Assert.Equal(120, report.BaselineRttMs!.Value, 0.01);
        Assert.Equal(82, report.FinalRttMs!.Value, 0.01);
        Assert.Equal(38, report.ImprovementMs!.Value, 0.01);
        Assert.True(report.EverythingReverted);
        Assert.Equal(2, runner.Reverted.Count);
        Assert.Equal("qos:dscp-46", runner.Reverted[0]);
        Assert.Equal("route:game-server", runner.Reverted[1]);
        Assert.Contains("опуститься нельзя", report.HonestSummaryRu);
        Assert.Equal(RealtimeOptimizationState.Stopped, optimizer.Current.State);
    }

    [Fact]
    public async Task Stop_WhenOptimizationMadeItWorse_SaysSoPlainly_AndTellsAboutRelay()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(Measurable(80), Measurable(80), Measurable(80), Measurable(95), Measurable(96));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync();
        RealtimeStopReport report = await optimizer.StopAsync();

        Assert.True(report.ImprovementMs < 0);
        Assert.Contains("Стало хуже", report.HonestSummaryRu);
        Assert.Contains("релей", report.HonestSummaryRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_WhenNoGain_DoesNotClaimVictory()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(Measurable(120), Measurable(120), Measurable(120), Measurable(120), Measurable(120));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync();
        RealtimeStopReport report = await optimizer.StopAsync();

        Assert.Equal(0, report.ImprovementMs!.Value, 0.01);
        Assert.Contains("Заметного выигрыша нет", report.HonestSummaryRu);
        Assert.Contains("так не бывает", report.HonestSummaryRu);
        Assert.False(optimizer.Current.HasMeasuredImprovement);
    }

    [Fact]
    public async Task FailedAction_IsReported_AndOtherActionsStillApply()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        runner.FailToApply.Add("qos:dscp-46");
        var measurements = new ScriptedMeasurements(Measurable(120), Measurable(120), Measurable(120), Measurable(100));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        RealtimeSnapshot snapshot = await optimizer.StartAsync();

        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, snapshot.State);
        Assert.Single(snapshot.AppliedActions);
        Assert.Contains(snapshot.Warnings, w => w.Contains("тестовый отказ применения", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(30.0, "Отлично")]
    [InlineData(55.0, "Хорошо")]
    [InlineData(95.0, "Терпимо")]
    [InlineData(150.0, "Плохо для шутера")]
    [InlineData(400.0, "Очень плохо")]
    public void QualityLabel_UsesShooterThresholds(double rtt, string expected) =>
        Assert.Equal(expected, RealtimeOptimizer.QualityLabel(rtt));

    [Fact]
    public void QualityLabel_WithoutMeasurements_SaysThereIsNoData() =>
        Assert.Equal("нет данных", RealtimeOptimizer.QualityLabel(null));

    [Fact]
    public void Planner_WithoutAdmin_BlocksEverything()
    {
        var planner = new OptimizationActionPlanner();

        OptimizationPlan plan = planner.Plan(Fortnite(), new RealtimeOptions(), isAdministrator: false);

        Assert.True(plan.IsBlocked);
        Assert.Empty(plan.Actions);
        Assert.Contains("без прав администратора", plan.BlockReasonRu!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Planner_DoesNotTouchTweaks_AndWarnsAboutUntestedAntiCheat()
    {
        var planner = new OptimizationActionPlanner();

        OptimizationPlan plan = planner.Plan(Fortnite(AntiCheatStatus.Untested), new RealtimeOptions(), isAdministrator: true);

        Assert.False(plan.IsBlocked);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == OptimizationActionKind.TweakRefresh);
        Assert.Contains(plan.Notes, n => n.Contains("не проверялась", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Contains("отдельное окно со снапшотом", StringComparison.OrdinalIgnoreCase));
    }

    // ── Задача F: история замеров для графика сети ────────────────────────────────────────────

    [Fact]
    public async Task История_замеров_хранит_окна_сессии_а_провалы_остаются_null()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        // Три окна «до», контрольное окно — и дальше контроль: одно окно не измеряется.
        var measurements = new ScriptedMeasurements(
            Measurable(120), Measurable(118), Measurable(120),
            Measurable(80), Measurable(82))
        {
            Fallback = PathMeasurement.Unmeasurable("поток пропал"),
        };
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync();
        await optimizer.TickOnceAsync();

        IReadOnlyList<RealtimeMeasurementPoint> samples = optimizer.RecentSamples;

        // Базовые окна + контрольное + тик: пять измеренных точек с датами.
        Assert.Equal(5, samples.Count);
        Assert.All(samples, point => Assert.True(point.IsMeasurable));
        Assert.All(samples, point => Assert.NotNull(point.RttMedianMs));
        Assert.All(samples, point => Assert.True(point.AtUtc > DateTime.MinValue));

        // Шестое окно не измерилось: точка с null-полями — на графике это разрыв, а не ноль.
        await optimizer.TickOnceAsync();
        samples = optimizer.RecentSamples;

        Assert.Equal(6, samples.Count);
        RealtimeMeasurementPoint gap = samples[^1];
        Assert.False(gap.IsMeasurable);
        Assert.Null(gap.RttMedianMs);
        Assert.Null(gap.RttP95Ms);
        Assert.Null(gap.JitterMadMs);
        Assert.Null(gap.LossPercent);
        Assert.Contains("поток пропал", gap.MethodRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task История_замеров_очищается_между_сессиями()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(
            Measurable(120), Measurable(120), Measurable(120), Measurable(80),   // первая сессия
            Measurable(90), Measurable(90), Measurable(90), Measurable(70));      // вторая сессия
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync();
        Assert.NotEmpty(optimizer.RecentSamples);
        Assert.Contains(optimizer.RecentSamples, point => point.RttMedianMs is >= 120);

        await optimizer.StopAsync();
        await optimizer.StartAsync();

        // Новая сессия начинается с чистой истории: точек первой сессии (120+) в графике нет.
        Assert.NotEmpty(optimizer.RecentSamples);
        Assert.All(optimizer.RecentSamples, point => Assert.True(point.RttMedianMs is null or <= 90));
    }

    [Fact]
    public async Task Окно_замера_несёт_минимум_среднее_и_p95_когда_они_есть()
    {
        var watcher = new StubWatcher(Fortnite());
        var runner = new RecordingRunner();
        var measurements = new ScriptedMeasurements(
            WithDistribution(120), WithDistribution(120), WithDistribution(120), WithDistribution(80));
        RealtimeOptimizer optimizer = Build(watcher, measurements, runner);

        await optimizer.StartAsync();

        RealtimeMeasurementPoint point = optimizer.RecentSamples[^1];
        Assert.True(point.IsMeasurable);
        Assert.Equal(78, point.RttMinMs!.Value, 0.01);
        Assert.Equal(80, point.RttMedianMs!.Value, 0.01);
        Assert.Equal(96, point.RttP95Ms!.Value, 0.01);
    }

    private static PathMeasurement WithDistribution(double median) =>
        new(true, median, 2, 0, 12, "ICMP-эхо до игрового сервера", RttMinMs: median - 2, RttAvgMs: median + 1, RttP95Ms: median + 16);
}
