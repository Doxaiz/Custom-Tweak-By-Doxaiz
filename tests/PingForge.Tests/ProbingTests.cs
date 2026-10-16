using System.Net;
using PingForge.Core.Models;
using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

public sealed class StatisticalMetricsTests
{
    [Fact]
    public void NoSamples_ProducesUnmeasurable_WithNullMetrics()
    {
        ProbeStatistics stats = StatisticalMetricsCalculator.Compute(new ProbeMeasurementInput(
            "1.2.3.4:27015", "Direct", ProbeMethodUsed.ActiveIcmpEcho, 30, Array.Empty<double>(), ProbeFailureCause.AllProbesTimedOut));

        Assert.False(stats.IsMeasurable);
        Assert.Null(stats.PacketLossPercent);
        Assert.Null(stats.StabilityScore0To100);
        Assert.Null(stats.RttAvgMs);
        Assert.Equal(ProbeFailureCause.AllProbesTimedOut, stats.FailureCause);
    }

    [Fact]
    public void Jitter_IsMeanAbsoluteDeviationOfNeighbouringRtt()
    {
        double[] alternating = Enumerable.Range(1, 30).Select(i => i % 2 == 0 ? 22.0 : 20.0).ToArray();

        ProbeStatistics stats = StatisticalMetricsCalculator.Compute(new ProbeMeasurementInput(
            "1.2.3.4", "Direct", ProbeMethodUsed.ActiveIcmpEcho, 30, alternating, ProbeFailureCause.None));

        Assert.True(stats.IsMeasurable);
        Assert.Equal(2.0, stats.JitterMadMs);
        Assert.Equal(21.0, stats.RttAvgMs);
        Assert.Equal(20.0, stats.RttMinMs);
        Assert.Equal(22.0, stats.RttMaxMs);
    }

    [Fact]
    public void Loss_IsComputedFromSentVersusReceived()
    {
        ProbeStatistics stats = StatisticalMetricsCalculator.Compute(new ProbeMeasurementInput(
            "1.2.3.4", "Direct", ProbeMethodUsed.ActiveIcmpEcho, 40, Enumerable.Repeat(20.0, 30).ToList(), ProbeFailureCause.None));

        Assert.Equal(25.0, stats.PacketLossPercent);
        Assert.Equal(30, stats.ReceivedCount);
        Assert.Equal(40, stats.SentCount);
    }

    [Fact]
    public void FewerThanThirtySamples_MarksProbeAsPreliminary()
    {
        ProbeStatistics stats = StatisticalMetricsCalculator.Compute(new ProbeMeasurementInput(
            "1.2.3.4", "Direct", ProbeMethodUsed.ActiveTcpHandshake, 5, Enumerable.Repeat(15.0, 5).ToList(), ProbeFailureCause.None));

        Assert.True(stats.IsPreliminary);
        Assert.Contains("Предварительная оценка", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Percentile_UsesLinearInterpolation()
    {
        double[] sorted = { 10, 20, 30, 40, 50 };
        Assert.Equal(50, StatisticalMetricsCalculator.Percentile(sorted, 1.0));
        Assert.Equal(30, StatisticalMetricsCalculator.Percentile(sorted, 0.5));
        Assert.Equal(48, StatisticalMetricsCalculator.Percentile(sorted, 0.95));
    }

    [Fact]
    public void Stability_IsClampedToZeroHundredRange()
    {
        Assert.Equal(100.0, StatisticalMetricsCalculator.ComputeStability(0, 0, 0));
        Assert.Equal(0.0, StatisticalMetricsCalculator.ComputeStability(100, 100, 100));
        Assert.InRange(StatisticalMetricsCalculator.ComputeStability(1, 8, 5), 0.0, 100.0);
    }
}

public sealed class DfMtuDiscoveryTests
{
    [Fact]
    public async Task FindsPppoeMtu1492()
    {
        // PPPoE: 1492 байта MTU ⇒ 1464 байта полезной нагрузки — максимум без фрагментации.
        var icmp = new StubIcmpProbe
        {
            Handler = (payload, _) => payload <= 1464
                ? new IcmpProbeReply(IcmpProbeStatus.Success, 10, IPAddress.Parse("1.1.1.1"))
                : new IcmpProbeReply(IcmpProbeStatus.PacketTooBig, 10, IPAddress.Parse("1.1.1.1"))
        };

        PathMtuDiscoveryResult result = await new DfMtuDiscoveryService(icmp).DiscoverAsync(IPAddress.Parse("1.1.1.1"), 1500);

        Assert.Equal(MtuDiscoveryOutcome.OptimalFound, result.Outcome);
        Assert.Equal(1492, result.OptimalMtuBytes);
        Assert.Equal(1464, result.MaxUnfragmentedPayloadBytes);
    }

    [Fact]
    public async Task WhenIcmpIsBlocked_DoesNotGuessMtu()
    {
        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null) };

        PathMtuDiscoveryResult result = await new DfMtuDiscoveryService(icmp).DiscoverAsync(IPAddress.Parse("1.1.1.1"), 1500);

        Assert.Equal(MtuDiscoveryOutcome.UndeterminedInconsistent, result.Outcome);
        Assert.Null(result.OptimalMtuBytes);
        Assert.Contains("определить MTU нельзя", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleRandomTimeout_DoesNotLowerDiscoveredMtu()
    {
        // Каждый пятый вызов «отваливается» — таймаут НЕ должен уменьшать найденный MTU.
        int call = 0;
        var icmp = new StubIcmpProbe
        {
            Handler = (payload, _) =>
            {
                call++;
                if (call % 5 == 0)
                {
                    return new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null);
                }

                return payload <= 1472
                    ? new IcmpProbeReply(IcmpProbeStatus.Success, 9, IPAddress.Parse("1.1.1.1"))
                    : new IcmpProbeReply(IcmpProbeStatus.PacketTooBig, 9, IPAddress.Parse("1.1.1.1"));
            }
        };

        PathMtuDiscoveryResult result = await new DfMtuDiscoveryService(icmp).DiscoverAsync(IPAddress.Parse("1.1.1.1"), 1500);

        Assert.Equal(MtuDiscoveryOutcome.OptimalFound, result.Outcome);
        Assert.Equal(1500, result.OptimalMtuBytes);
    }

    [Fact]
    public async Task WhenNoFragmentationOccurs_ReportsThatNothingShouldChange()
    {
        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Success, 7, IPAddress.Parse("1.1.1.1")) };

        PathMtuDiscoveryResult result = await new DfMtuDiscoveryService(icmp).DiscoverAsync(IPAddress.Parse("1.1.1.1"), 1500);

        Assert.Equal(MtuDiscoveryOutcome.OptimalFound, result.Outcome);
        Assert.Equal(1500, result.OptimalMtuBytes);
        Assert.Contains("менять нечего", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutAdministratorRights_ReportsPermissionProblem()
    {
        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.PermissionDenied, 0, null) };

        PathMtuDiscoveryResult result = await new DfMtuDiscoveryService(icmp).DiscoverAsync(IPAddress.Parse("1.1.1.1"), 1500);

        Assert.Equal(MtuDiscoveryOutcome.UndeterminedIcmpBlocked, result.Outcome);
        Assert.Contains("администратора", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DnsBenchmarkTests
{
    private static readonly (string Name, string Primary, string Secondary)[] OneResolver =
    {
        ("Test resolver", "9.9.9.9", "9.9.9.10")
    };

    [Fact]
    public async Task ResponseWithMismatchedTransactionId_IsDiscarded()
    {
        var transport = new StubDnsTransport
        {
            // Сервер отвечает чужим ID — ответ обязан быть отброшен.
            Handler = (server, domain, _) => new DnsRawResponse(new IPEndPoint(server, 53), 0xBEEF, true, domain, 0, 1)
        };

        var benchmarker = new DnsResolverBenchmarker(transport, new[] { "example.com" }, () => 0x1234, TimeSpan.FromMilliseconds(50));
        IReadOnlyList<DnsResolverBenchmarkResult> results = await benchmarker.BenchmarkAsync(OneResolver);

        DnsResolverBenchmarkResult single = Assert.Single(results);
        Assert.False(single.IsReachable);
        Assert.Equal(0, single.QueriesAnswered);
    }

    [Fact]
    public async Task ResponseFromDifferentSource_IsDiscarded()
    {
        var transport = new StubDnsTransport
        {
            Handler = (_, domain, id) => new DnsRawResponse(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 53), id, true, domain, 0, 1)
        };

        var benchmarker = new DnsResolverBenchmarker(transport, new[] { "example.com" }, () => 0x2222, TimeSpan.FromMilliseconds(50));
        IReadOnlyList<DnsResolverBenchmarkResult> results = await benchmarker.BenchmarkAsync(OneResolver);

        Assert.False(results[0].IsReachable);
    }

    [Fact]
    public async Task HijackedResolver_IsFlaggedAndPenalisedInScore()
    {
        var transport = new StubDnsTransport
        {
            // Все имена, включая канареечное в зоне .invalid, «успешно» резолвятся — признак перехвата DNS.
            Handler = (server, domain, id) => new DnsRawResponse(new IPEndPoint(server, 53), id, true, domain, 0, 1)
        };

        var benchmarker = new DnsResolverBenchmarker(transport, new[] { "example.com" }, () => 0x3333, TimeSpan.FromMilliseconds(50));
        IReadOnlyList<DnsResolverBenchmarkResult> results = await benchmarker.BenchmarkAsync(OneResolver);

        DnsResolverBenchmarkResult single = Assert.Single(results);
        Assert.True(single.AnswersLookHijacked);
        Assert.Contains("перехват", single.NoteRu, StringComparison.OrdinalIgnoreCase);
        Assert.True(single.CompositeScore >= 500.0);
    }

    [Fact]
    public void CompositeScore_PenalisesHijackAndLowSuccessRate()
    {
        double clean = DnsResolverBenchmarker.ComputeCompositeScore(10, 12, 20, 100, false);
        double hijacked = DnsResolverBenchmarker.ComputeCompositeScore(10, 12, 20, 100, true);
        double lossy = DnsResolverBenchmarker.ComputeCompositeScore(10, 12, 20, 50, false);

        Assert.True(hijacked > clean);
        Assert.True(lossy > clean);
    }

    [Fact]
    public async Task SuccessRate_IsComputedFromActuallySentQueries()
    {
        int call = 0;
        var transport = new StubDnsTransport
        {
            Handler = (server, domain, id) =>
            {
                call++;
                // Отвечает только на каждый второй запрос.
                return call % 2 == 0 ? new DnsRawResponse(new IPEndPoint(server, 53), id, true, domain, 0, 1) : null;
            }
        };

        var benchmarker = new DnsResolverBenchmarker(transport, new[] { "a.com", "b.com" }, () => 0x4444, TimeSpan.FromMilliseconds(50));
        IReadOnlyList<DnsResolverBenchmarkResult> results = await benchmarker.BenchmarkAsync(OneResolver);

        DnsResolverBenchmarkResult single = Assert.Single(results);
        Assert.True(single.QueriesSent > 0);
        Assert.True(single.SuccessRatePercent < 100.0);
        Assert.Equal(transport.QueriesSent, single.QueriesSent);
    }
}

public sealed class RouteProbeServiceTests
{
    private static RouteProbeService CreateService(StubIcmpProbe icmp, StubTcpProbe tcp, StubHostResolver resolver, ILiveFlowRttProvider? liveFlow = null) =>
        new(icmp, tcp, resolver, new HopTracerouteEngine(icmp), liveFlow);

    [Fact]
    public async Task UnresolvableHost_ReportsResolutionFailure()
    {
        var resolver = new StubHostResolver();
        RouteProbeService service = CreateService(new StubIcmpProbe(), new StubTcpProbe(), resolver);

        ProbeStatistics stats = await service.ProbeEndpointAsync("no-such-host.invalid", 27015, "Direct", 5);

        Assert.False(stats.IsMeasurable);
        Assert.Equal(ProbeFailureCause.ResolutionFailed, stats.FailureCause);
        Assert.Null(stats.PacketLossPercent);
    }

    [Fact]
    public async Task IcmpSuccess_IsMeasurable_AndReportsMethod()
    {
        var resolver = new StubHostResolver();
        resolver.Map["game.example"] = IPAddress.Parse("10.0.0.5");

        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Success, 18.5, null) };
        RouteProbeService service = CreateService(icmp, new StubTcpProbe(), resolver);

        ProbeStatistics stats = await service.ProbeEndpointAsync("game.example", 27015, "Direct", 30);

        Assert.True(stats.IsMeasurable);
        Assert.Equal(ProbeMethodUsed.ActiveIcmpEcho, stats.MethodUsed);
        Assert.Equal(18.5, stats.RttAvgMs);
        Assert.Contains("ICMP", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IcmpPermissionDenied_ReportsPermissionCauseInsteadOfBlocked()
    {
        var resolver = new StubHostResolver();
        resolver.Map["game.example"] = IPAddress.Parse("10.0.0.5");

        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.PermissionDenied, 0, null) };
        RouteProbeService service = CreateService(icmp, new StubTcpProbe(), resolver);

        ProbeStatistics stats = await service.ProbeEndpointAsync("game.example", 27015, "Direct", 30);

        Assert.False(stats.IsMeasurable);
        Assert.Equal(ProbeFailureCause.PermissionDenied, stats.FailureCause);
        Assert.Contains("администратора", stats.StatusMessageRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TcpRefusal_CountsAsValidRttSample_NotAsFailure()
    {
        var resolver = new StubHostResolver();
        resolver.Map["game.example"] = IPAddress.Parse("10.0.0.5");

        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null) };
        var tcp = new StubTcpProbe { Handler = _ => new TcpProbeReply(TcpProbeStatus.ConnectionRefused, 9.5) };

        RouteProbeService service = CreateService(icmp, tcp, resolver);
        ProbeStatistics stats = await service.ProbeEndpointAsync("game.example", 27015, "Direct", 30);

        Assert.True(stats.IsMeasurable);
        Assert.Equal(ProbeMethodUsed.ActiveTcpHandshake, stats.MethodUsed);
        Assert.Equal(9.5, stats.RttAvgMs);
        Assert.Contains("RST", stats.StatusMessageRu, StringComparison.Ordinal);
        Assert.Contains("НЕ замер игрового потока", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveTcpFlow_TakesPriority_AndLeavesUnavailablePercentilesNull()
    {
        var resolver = new StubHostResolver();
        resolver.Map["game.example"] = IPAddress.Parse("10.0.0.5");

        RouteProbeService service = CreateService(
            new StubIcmpProbe(), new StubTcpProbe(), resolver,
            new StubLiveFlowProvider(new LiveFlowRttSample("tcp", 21.4, 3.1, 2, 120)));

        ProbeStatistics stats = await service.ProbeEndpointAsync("game.example", 27015, "Relay", 30);

        Assert.True(stats.IsMeasurable);
        Assert.Equal(ProbeMethodUsed.LiveTcpEstats, stats.MethodUsed);
        Assert.Equal(21.4, stats.RttAvgMs);
        Assert.Null(stats.RttP95Ms);
        Assert.Null(stats.RttMinMs);
        Assert.Contains("живого потока", stats.StatusMessageRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllMethodsBlocked_ReportsHonestUnmeasurable()
    {
        var resolver = new StubHostResolver();
        resolver.Map["game.example"] = IPAddress.Parse("10.0.0.5");

        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null) };
        var tcp = new StubTcpProbe { Handler = _ => new TcpProbeReply(TcpProbeStatus.Timeout, 0) };

        RouteProbeService service = CreateService(icmp, tcp, resolver);
        ProbeStatistics stats = await service.ProbeEndpointAsync("game.example", 27015, "Direct", 30);

        Assert.False(stats.IsMeasurable);
        Assert.Equal(ProbeFailureCause.AllProbesTimedOut, stats.FailureCause);
        Assert.Null(stats.StabilityScore0To100);
        Assert.Null(stats.PacketLossPercent);
    }

    [Fact]
    public async Task QuickProbe_UsesFewerSamplesAndIsMarkedPreliminary()
    {
        var resolver = new StubHostResolver();
        resolver.Map["game.example"] = IPAddress.Parse("10.0.0.5");

        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Success, 12, null) };
        RouteProbeService service = CreateService(icmp, new StubTcpProbe(), resolver);

        ProbeStatistics stats = await service.ProbeQuickAsync("game.example", 27015, "Direct");

        Assert.True(stats.IsPreliminary);
        Assert.Equal(RouteProbeService.MinimumUsefulSampleCount, stats.SentCount);
    }

    private sealed class StubLiveFlowProvider : ILiveFlowRttProvider
    {
        private readonly LiveFlowRttSample _sample;

        public StubLiveFlowProvider(LiveFlowRttSample sample) => _sample = sample;

        public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
        {
            sample = _sample;
            return true;
        }
    }
}

public sealed class TracerouteTests
{
    [Fact]
    public async Task HopsNeverContainInventedAsnLabels()
    {
        var icmp = new StubIcmpProbe
        {
            Handler = (_, ttl) => ttl switch
            {
                1 => new IcmpProbeReply(IcmpProbeStatus.Success, 1.2, IPAddress.Parse("192.168.1.1")),
                2 => new IcmpProbeReply(IcmpProbeStatus.Success, 8.4, IPAddress.Parse("100.64.0.1")),
                _ => new IcmpProbeReply(IcmpProbeStatus.Success, 14.9, IPAddress.Parse("1.2.3.4"))
            }
        };

        IReadOnlyList<RouteHopMetric> hops = await new HopTracerouteEngine(icmp).TraceAsync(IPAddress.Parse("1.2.3.4"), 8);

        Assert.Equal(3, hops.Count);
        Assert.All(hops, h => Assert.Equal(AsnLabels.Unknown, h.AsnLabel));
        Assert.Equal("1.2.3.4", hops[^1].IpAddress);
    }

    [Fact]
    public async Task StopsAfterFourConsecutiveSilentHops()
    {
        int calls = 0;
        var icmp = new StubIcmpProbe
        {
            Handler = (_, _) =>
            {
                calls++;
                return new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null);
            }
        };

        IReadOnlyList<RouteHopMetric> hops = await new HopTracerouteEngine(icmp).TraceAsync(IPAddress.Parse("1.2.3.4"), 12);

        Assert.Equal(4, hops.Count);
        Assert.All(hops, h => Assert.True(h.IsTimedOut));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SilentHopsHaveNoLatencyNumber()
    {
        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null) };

        IReadOnlyList<RouteHopMetric> hops = await new HopTracerouteEngine(icmp).TraceAsync(IPAddress.Parse("1.2.3.4"), 3);

        Assert.All(hops, h => Assert.Null(h.RttMs));
    }
}
