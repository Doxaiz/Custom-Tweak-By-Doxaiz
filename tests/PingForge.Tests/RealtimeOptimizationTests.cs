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

        public Task<OptimizationActionOutcome> ApplyAsync(OptimizationActionDescriptor action, CancellationToken ct = default)
        {
            if (FailToApply.Contains(action.ActionId))
            {
                return Task.FromResult(OptimizationActionOutcome.Fail("тестовый отказ применения"));
            }

            Applied.Add(action.ActionId);
            return Task.FromResult(OptimizationActionOutcome.Ok("применено"));
        }

        public Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct = default)
        {
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
}
