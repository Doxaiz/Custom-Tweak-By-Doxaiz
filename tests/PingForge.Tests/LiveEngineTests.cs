using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Живой сетевой движок: то, из-за чего кнопка раньше отвечала «движок не подключён».
///
/// Здесь проверяется стык, а не Windows: мост «пробы → окно замера» и фоновый контроль.
/// Главное правило, которое держится тестами: <b>нет измерения — нет чисел</b>.
/// </summary>
public class LiveEngineTests
{
    // ── мост «пробы → окно замера» ──────────────────────────────────────────────────────────────

    private sealed class StubProbes : IRouteProbe
    {
        public Func<ProbeStatistics> Answer { get; set; } = () => Statistics.Healthy(78.4, 2.5, 0, 12);

        public string? LastEndpoint { get; private set; }
        public int? LastPort { get; private set; }
        public int LastSampleCount { get; private set; }

        public Task<ProbeStatistics> ProbeEndpointAsync(
            string hostOrIp, int port, string routeLabel, int sampleCount = 30, CancellationToken ct = default)
        {
            LastEndpoint = hostOrIp;
            LastPort = port;
            LastSampleCount = sampleCount;
            return Task.FromResult(Answer());
        }

        public Task<IReadOnlyList<RouteHopMetric>> TraceRouteAsync(
            string targetHostOrIp, int maxHops = 12, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RouteHopMetric>>([]);
    }

    private static class Statistics
    {
        public static ProbeStatistics Healthy(double rtt, double jitter, double loss, int received) =>
            new(
                "1.2.3.4:7777", "живая оптимизация", IsMeasurable: true,
                ProbeMethodUsed.ActiveIcmpEcho, ProbeFailureCause.None,
                "ICMP-эхо: 12 сэмплов, ответов 12", SentCount: 12, ReceivedCount: received,
                RttMinMs: rtt - 5, RttAvgMs: rtt, RttMaxMs: rtt + 9,
                RttMedianMs: rtt, RttP95Ms: rtt + 6, RttP99Ms: rtt + 8,
                JitterMadMs: jitter, PacketLossPercent: loss,
                OutlierSpikesCount: 0, StabilityScore0To100: 88, IsPreliminary: false,
                Hops: [], MeasuredAtUtc: DateTime.UtcNow);

        public static ProbeStatistics Silent() =>
            ProbeStatistics.Unmeasurable(
                "1.2.3.4:7777", "живая оптимизация", ProbeFailureCause.AllProbesTimedOut, 12,
                "Все пробы истекли: сервер не отвечает ни на ICMP, ни на TCP. Игра может быть в меню.");
    }

    [Fact]
    public async Task Замер_превращается_в_окно_с_медианой_джиттером_и_методом()
    {
        var probes = new StubProbes();
        var source = new RouteProbeMeasurementSource(probes, samplesPerWindow: 12);

        PathMeasurement window = await source.MeasurePathAsync("1.2.3.4", 7777);

        Assert.True(window.IsMeasurable);
        Assert.Equal(78.4, window.RttMedianMs, 3);
        Assert.Equal(2.5, window.JitterMs, 3);
        Assert.Equal(0, window.LossPercent, 3);
        Assert.Equal(12, window.Samples);
        Assert.Contains("ICMP", window.MethodRu);
        Assert.Equal("1.2.3.4", probes.LastEndpoint);
        Assert.Equal(7777, probes.LastPort);
        Assert.Equal(12, probes.LastSampleCount);
    }

    [Fact]
    public async Task Неизмеримый_путь_даёт_причину_а_не_нули()
    {
        var probes = new StubProbes { Answer = Statistics.Silent };
        var source = new RouteProbeMeasurementSource(probes);

        PathMeasurement window = await source.MeasurePathAsync("1.2.3.4", 7777);

        Assert.False(window.IsMeasurable);
        Assert.Equal(0, window.RttMedianMs);
        Assert.Contains("не отвечает", window.MethodRu);
        Assert.Contains("в меню", window.MethodRu);
    }

    [Fact]
    public async Task Пустой_адрес_не_превращается_в_замер_по_умолчанию()
    {
        var probes = new StubProbes();
        var source = new RouteProbeMeasurementSource(probes);

        PathMeasurement window = await source.MeasurePathAsync("   ", 0);

        Assert.False(window.IsMeasurable);
        Assert.Contains("нет адреса", window.MethodRu);
    }

    // ── фоновый контроль ────────────────────────────────────────────────────────────────────────

    private sealed class AlwaysHealthyWatcher : IGameFlowWatcher
    {
        public Task<IReadOnlyList<GameFlowInfo>> FindActiveGameFlowsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameFlowInfo>>(
            [
                new GameFlowInfo(4242, "cs2.exe", "Counter-Strike 2", "1.2.3.4", 7777, "UDP", AntiCheatStatus.Untested),
            ]);
    }

    private sealed class SteadyMeasurement : IRealtimeMeasurementSource
    {
        public int Calls { get; private set; }

        public Task<PathMeasurement> MeasurePathAsync(string endpoint, int port, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new PathMeasurement(true, 79, 2, 0, 12, "ICMP-эхо, 12 сэмплов"));
        }
    }

    private sealed class QuietActions : IOptimizationActionRunner
    {
        public Task<OptimizationActionOutcome> ApplyAsync(OptimizationActionDescriptor action, CancellationToken ct = default) =>
            Task.FromResult(OptimizationActionOutcome.Ok("применено"));

        public Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct = default) =>
            Task.FromResult(OptimizationActionOutcome.Ok("снято"));
    }

    private static RealtimeOptimizer BuildOptimizer(out SteadyMeasurement measurement)
    {
        measurement = new SteadyMeasurement();
        return new RealtimeOptimizer(new AlwaysHealthyWatcher(), measurement, new QuietActions());
    }

    [Fact]
    public async Task Контроль_идёт_пока_оптимизация_активна()
    {
        RealtimeOptimizer optimizer = BuildOptimizer(out _);
        var monitor = new RealtimeMonitor(optimizer, interval: () => TimeSpan.FromMilliseconds(5));

        await optimizer.StartAsync();
        Assert.True(monitor.IsActive);

        // Ждём до трёх окон, а не «сколько успеется за 300 мс»: на загруженном раннере этого
        // хватало ровно на одно окно (прогон 44079925781: «окон контроля было 1»), и тест
        // говорил «контроль не идёт» про работающий контроль. Предел в пять секунд остаётся:
        // если контроль действительно стоит, тест падает, а не висит.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task loop = monitor.RunAsync(stop.Token);
        while (monitor.Ticks < 3 && !loop.IsCompleted)
        {
            await Task.Delay(10);
        }

        stop.Cancel();
        await loop;

        Assert.True(monitor.Ticks >= 3, $"окон контроля было {monitor.Ticks} — контроль не идёт");
    }

    [Fact]
    public async Task Контроль_молчит_когда_оптимизация_не_запущена()
    {
        RealtimeOptimizer optimizer = BuildOptimizer(out SteadyMeasurement measurement);
        var monitor = new RealtimeMonitor(optimizer, interval: () => TimeSpan.FromMilliseconds(5));

        bool ticked = await monitor.TickAsync();

        Assert.False(ticked);
        Assert.False(monitor.IsActive);
        Assert.Equal(0, measurement.Calls);
    }

    [Fact]
    public async Task Ошибка_одного_окна_не_останавливает_контроль()
    {
        RealtimeOptimizer optimizer = BuildOptimizer(out _);
        var monitor = new RealtimeMonitor(optimizer, interval: () => TimeSpan.FromMilliseconds(5));

        await optimizer.StartAsync();

        // Останавливаем оптимизатор «из-под» монитора: окно контроля после остановки не считается.
        await optimizer.StopAsync();
        bool ticked = await monitor.TickAsync();

        Assert.False(ticked);
        Assert.False(monitor.IsActive);
    }
}
