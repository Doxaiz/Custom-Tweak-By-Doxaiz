using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Orchestration.Realtime;
using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Правило «нет измерения — нет нуля» на пути живой оптимизации. Ноль — это измеренное отсутствие
/// разброса и потерь; отсутствие замера нулём не является, и подставлять его нельзя ни в числа,
/// ни в тексты.
/// </summary>
public sealed class UnmeasuredNumbersTests
{
    [Fact]
    public async Task Живой_поток_не_показывает_потери_нулём()
    {
        // Живой замер видит задержку и разброс, но потерь не видит: отправки без ответа — это
        // незакрытые пары наблюдения, а не потерянные пакеты.
        var service = new RouteProbeService(
            new StubIcmpProbe(),
            new StubTcpProbe(),
            new StubHostResolver(),
            new HopTracerouteEngine(new StubIcmpProbe()),
            liveFlowProvider: new StubLiveFlowProvider());

        ProbeStatistics stats = await service.ProbeEndpointAsync("203.0.113.30", 27015, "живая оптимизация", 12);

        Assert.Equal(ProbeMethodUsed.LiveUdpPassiveFlow, stats.MethodUsed);
        Assert.Null(stats.PacketLossPercent);
        Assert.Contains("Потери в этом способе не измеряются", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Окно_замера_не_подставляет_ноль_вместо_неизмеренного()
    {
        var order = new ProbeStatistics(
            "203.0.113.30:27015", "живая оптимизация", IsMeasurable: true,
            ProbeMethodUsed.LiveTcpEstats, ProbeFailureCause.None, "статистика соединения",
            SentCount: 12, ReceivedCount: 12,
            RttMinMs: null, RttAvgMs: 30, RttMaxMs: null, RttMedianMs: 30,
            RttP95Ms: null, RttP99Ms: null,
            JitterMadMs: null, PacketLossPercent: null,
            OutlierSpikesCount: 0, StabilityScore0To100: null, IsPreliminary: false,
            Hops: [], MeasuredAtUtc: DateTime.UtcNow);

        var source = new RouteProbeMeasurementSource(new FixedProbe(order), samplesPerWindow: 12);

        PathMeasurement window = await source.MeasurePathAsync("203.0.113.30", 27015);

        Assert.True(window.IsMeasurable);
        Assert.Equal(30, window.RttMedianMs, 2);
        Assert.Null(window.JitterMs);
        Assert.Null(window.LossPercent);
    }

    [Fact]
    public void Неизмеренное_называется_словами_а_измеренное_числом()
    {
        PathMeasurement unmeasured = new(true, 30, null, null, 12, "живой поток игры");

        Assert.Equal("не измерено", unmeasured.JitterRu);
        Assert.Equal("не измерено", unmeasured.LossRu);

        PathMeasurement measured = new(true, 30, 3.1, 0, 12, "живой поток игры");

        Assert.Contains("мс", measured.JitterRu, StringComparison.Ordinal);
        Assert.DoesNotContain("не измерено", measured.JitterRu, StringComparison.Ordinal);
        Assert.Contains("%", measured.LossRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Незавершённое_окно_не_несёт_чисел_вовсе()
    {
        PathMeasurement window = PathMeasurement.Unmeasurable("живой поток не найден");

        Assert.False(window.IsMeasurable);
        Assert.Null(window.JitterMs);
        Assert.Null(window.LossPercent);
        Assert.Equal("не измерено", window.JitterRu);
    }

    private sealed class FixedProbe : IRouteProbe
    {
        private readonly ProbeStatistics _statistics;

        public FixedProbe(ProbeStatistics statistics) => _statistics = statistics;

        public Task<ProbeStatistics> ProbeEndpointAsync(
            string hostOrIp, int port, string routeLabel, int sampleCount = 30, CancellationToken ct = default) =>
            Task.FromResult(_statistics);

        public Task<IReadOnlyList<RouteHopMetric>> TraceRouteAsync(
            string targetHostOrIp, int maxHops = 12, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RouteHopMetric>>([]);
    }

    private sealed class StubLiveFlowProvider : ILiveFlowRttProvider
    {
        public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
        {
            sample = new LiveFlowRttSample(
                UdpObservationsLiveFlowRttProvider.MethodName,
                RttAverageMs: 18.5,
                JitterMs: 2.2,
                Retransmissions: 0,
                SamplesAvailable: 40,
                UnansweredSends: 3,
                RttMedianMs: 18.0);

            return true;
        }
    }
}
