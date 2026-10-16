using System.Net;
using PingForge.Analysis;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Probing;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

public sealed class MtuAdviceTests
{
    private sealed class StubEndpoints : IGameServerEndpointProvider
    {
        public IPAddress? Address { get; set; }

        public Task<IPAddress?> TryGetGameServerAddressAsync(CancellationToken ct = default) =>
            Task.FromResult(Address);
    }

    private sealed class StubContext : INetworkContextReader
    {
        public int? Mtu { get; set; } = 1500;

        public Task<NetworkContextSnapshot> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new NetworkContextSnapshot(
                Array.Empty<AdapterInventoryItem>(), Mtu, Array.Empty<string>(), "Ethernet", Array.Empty<string>()));
    }

    private static MtuAdviceProvider Build(IIcmpProbe icmp, IPAddress? server, int? currentMtu) =>
        new(new DfMtuDiscoveryService(icmp),
            new StubEndpoints { Address = server },
            new StubContext { Mtu = currentMtu });

    [Fact]
    public async Task MeasuredValue_IsReturnedWithExplanation()
    {
        var icmp = new StubIcmpProbe
        {
            Handler = (payload, _) => payload <= 1464
                ? new IcmpProbeReply(IcmpProbeStatus.Success, 9, IPAddress.Parse("203.0.113.10"))
                : new IcmpProbeReply(IcmpProbeStatus.PacketTooBig, 9, IPAddress.Parse("203.0.113.10"))
        };

        ComputedTargetValue advice = await Build(icmp, IPAddress.Parse("203.0.113.10"), 1500)
            .GetRecommendedMtuAsync(TweakTarget.Global("MTU"));

        Assert.True(advice.IsAvailable);
        Assert.Equal("1492", advice.Value);
        Assert.Contains("фрагментаци", advice.ExplanationRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Сейчас стоит 1500", advice.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutGameServer_IsUnavailable_AndExplainsWhy()
    {
        ComputedTargetValue advice = await Build(new StubIcmpProbe(), null, 1500)
            .GetRecommendedMtuAsync(TweakTarget.Global("MTU"));

        Assert.False(advice.IsAvailable);
        Assert.Contains("Игровой сервер не выбран", advice.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableCurrentMtu_IsUnavailable()
    {
        ComputedTargetValue advice = await Build(new StubIcmpProbe(), IPAddress.Parse("203.0.113.10"), null)
            .GetRecommendedMtuAsync(TweakTarget.Global("MTU"));

        Assert.False(advice.IsAvailable);
        Assert.Contains("прочитать не удалось", advice.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlockedIcmp_IsUnavailable_AndDoesNotGuessMtu()
    {
        var icmp = new StubIcmpProbe { Handler = (_, _) => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null) };

        ComputedTargetValue advice = await Build(icmp, IPAddress.Parse("203.0.113.10"), 1500)
            .GetRecommendedMtuAsync(TweakTarget.Global("MTU"));

        Assert.False(advice.IsAvailable);
        Assert.Equal(string.Empty, advice.Value);
        Assert.Contains("определить MTU нельзя", advice.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DnsAdviceTests
{
    private sealed class StubRunner : IDnsBenchmarkRunner
    {
        private readonly IReadOnlyList<DnsResolverBenchmarkResult> _results;

        public StubRunner(params DnsResolverBenchmarkResult[] results) => _results = results;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<DnsResolverBenchmarkResult>> RunAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_results);
        }
    }

    private static DnsResolverBenchmarkResult Resolver(
        string name, string primary, string secondary = "", bool reachable = true,
        int answered = 8, double warmMs = 10, double score = 10, bool hijacked = false) =>
        new(name, primary, secondary, reachable, 8, answered, warmMs, warmMs + 1, warmMs * 2, 100, score, hijacked,
            reachable ? "Ответы валидированы." : "Нет ответа.");

    [Fact]
    public async Task FastestCleanResolver_Wins_AndHijackedIsSkipped()
    {
        var runner = new StubRunner(
            Resolver("Провайдерский DNS", "192.168.1.1", warmMs: 3, score: 3, hijacked: true),
            Resolver("Google DNS", "8.8.8.8", "8.8.4.4", warmMs: 12, score: 12),
            Resolver("Quad9", "9.9.9.9", reachable: false, answered: 0, score: 999));

        ComputedTargetValue advice = await new DnsAdviceProvider(runner)
            .GetRecommendedDnsAsync(TweakTarget.Global("DNS"));

        Assert.True(advice.IsAvailable);
        Assert.Equal("8.8.8.8|8.8.4.4", advice.Value);
        Assert.Equal(TweakValueDataType.MultiString, advice.DataType);
        Assert.Contains("Google DNS", advice.ExplanationRu, StringComparison.Ordinal);
        Assert.Contains("перехват", advice.ExplanationRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не снижает пинг внутри боя", advice.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllHijacked_IsUnavailable_AndKeepsCurrentSettings()
    {
        var runner = new StubRunner(
            Resolver("DNS1", "10.0.0.1", hijacked: true),
            Resolver("DNS2", "10.0.0.2", hijacked: true));

        ComputedTargetValue advice = await new DnsAdviceProvider(runner)
            .GetRecommendedDnsAsync(TweakTarget.Global("DNS"));

        Assert.False(advice.IsAvailable);
        Assert.Contains("Оставляем текущие настройки", advice.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingReachable_IsUnavailable_WithoutInventingWinner()
    {
        var runner = new StubRunner(Resolver("Cloudflare", "1.1.1.1", reachable: false, answered: 0, score: 0));

        ComputedTargetValue advice = await new DnsAdviceProvider(runner)
            .GetRecommendedDnsAsync(TweakTarget.Global("DNS"));

        Assert.False(advice.IsAvailable);
        Assert.Contains("Ни один публичный DNS-сервер не ответил", advice.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyBenchmark_IsUnavailable_WithClearReason()
    {
        ComputedTargetValue advice = await new DnsAdviceProvider(new StubRunner())
            .GetRecommendedDnsAsync(TweakTarget.Global("DNS"));

        Assert.False(advice.IsAvailable);
        Assert.Contains("Бенчмарк DNS не выполнен", advice.ExplanationRu, StringComparison.Ordinal);
    }
}

public sealed class TweakAnalyzerTests
{
    [Fact]
    public async Task SplitsTweaksIntoAlreadyOptimal_Recommended_AndDoNotTouch()
    {
        var already = new FakeTweak("TCP_ACK_FREQUENCY", defaultValue: "1", existingValuePresent: true, isAlreadyOptimal: true);
        var toChange = new FakeTweak("NIC_ITR_OFF", defaultValue: "0", existingValuePresent: false);
        var unsupported = new FakeTweak("NIC_RSS_ON", isSupported: false);
        var computedUnavailable = new FakeComputedTweak("DNS_SET", computedValue: null);

        var analyzer = new TweakAnalyzer(new FakeTweakCatalog(new ITweak[] { already, toChange, unsupported, computedUnavailable }));
        TweakAnalysisReport report = await analyzer.AnalyzeAsync(new TweakTarget("NIC", "{GUID-ADAPTER-A}", "Ethernet"));

        Assert.Single(report.AlreadyOptimal);
        Assert.Equal("TCP_ACK_FREQUENCY", report.AlreadyOptimal[0].TweakCode);

        Assert.Single(report.RecommendedToChange);
        Assert.Equal("NIC_ITR_OFF", report.RecommendedToChange[0].TweakCode);
        Assert.Equal("(не задано)", report.RecommendedToChange[0].CurrentValueDisplay);
        Assert.Equal("1", report.RecommendedToChange[0].RecommendedValueDisplay);

        Assert.Contains(report.DoNotTouch, i => i.TweakCode == "NIC_RSS_ON");
        Assert.Contains(report.DoNotTouch, i => i.TweakCode == "DNS_SET");
        Assert.Contains(report.DoNotTouch, i => i.ReasonRu.Contains("Определить значение не удалось"));
    }

    [Fact]
    public async Task AdapterBoundTweaks_AreSkipped_WhenActiveAdapterIsUnknown()
    {
        var adapterTweak = new FakeTweak("NIC_RSS_ON");
        var analyzer = new TweakAnalyzer(new FakeTweakCatalog(new ITweak[] { adapterTweak }));

        TweakAnalysisReport report = await analyzer.AnalyzeAsync(activeAdapterTarget: null);

        Assert.False(report.AdapterResolved);
        Assert.Empty(report.RecommendedToChange);
        Assert.Single(report.DoNotTouch);
        Assert.Contains(report.Notes, n => n.Contains("Активный адаптер не определён"));
    }

    [Fact]
    public async Task NothingToChange_ProducesHonestNote_NotInventedWork()
    {
        var already = new FakeTweak("WIN_GAME_MODE", defaultValue: "1", existingValuePresent: true, isAlreadyOptimal: true);
        var analyzer = new TweakAnalyzer(new FakeTweakCatalog(new ITweak[] { already }));

        TweakAnalysisReport report = await analyzer.AnalyzeAsync(new TweakTarget("NIC", "{GUID-ADAPTER-A}", "Ethernet"));

        Assert.Empty(report.RecommendedToChange);
        Assert.Contains(report.Notes, n => n.Contains("Изменять нечего"));
    }
}

public sealed class NetworkAnalyzerTests
{
    private sealed class StubContext : INetworkContextReader
    {
        public NetworkContextSnapshot Snapshot { get; set; } = new(
            Array.Empty<AdapterInventoryItem>(), 1500, Array.Empty<string>(), "Ethernet", Array.Empty<string>());

        public Task<NetworkContextSnapshot> ReadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
    }

    private sealed class StubMtuAdvice : IMtuAdviceProvider
    {
        public ComputedTargetValue Value { get; set; } = ComputedTargetValue.Unavailable("MTU не измерялся.");

        public Task<ComputedTargetValue> GetRecommendedMtuAsync(TweakTarget target, CancellationToken ct = default) =>
            Task.FromResult(Value);
    }

    private sealed class StubDnsAdvice : IDnsAdviceProvider
    {
        public ComputedTargetValue Value { get; set; } = ComputedTargetValue.Unavailable("DNS не измерялся.");

        public Task<ComputedTargetValue> GetRecommendedDnsAsync(TweakTarget target, CancellationToken ct = default) =>
            Task.FromResult(Value);
    }

    private static NetworkAnalyzer Build(
        StubContext context, StubMtuAdvice mtu, StubDnsAdvice dns, bool withCatalog = true) =>
        new(mtu, dns, context, withCatalog ? new TweakCatalog(new TweakPlatformPorts()) : null);

    private static AdapterInventoryItem Adapter(string name, bool up = true, bool tunnel = false, string desc = "") =>
        new($"{{{name}}}", name, string.IsNullOrEmpty(desc) ? name : desc, up, !tunnel, tunnel, true, 1500);

    [Fact]
    public async Task ActiveVpnAlongsidePhysicalAdapter_IsReported()
    {
        var context = new StubContext
        {
            Snapshot = new NetworkContextSnapshot(
                new[] { Adapter("Ethernet"), Adapter("WireGuard Tunnel", tunnel: true) },
                1500, new[] { "192.168.1.1" }, "Ethernet", Array.Empty<string>())
        };

        NetworkAnalysisReport report = await Build(context, new StubMtuAdvice(), new StubDnsAdvice())
            .AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.Contains(report.Warnings, w => w.Code == NetworkAnalyzer.WarningTunnelActive);
        Assert.Contains(report.Warnings, w => w.RemediationRu.Contains("выключите его", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwoPhysicalAdaptersUp_AreReported()
    {
        var context = new StubContext
        {
            Snapshot = new NetworkContextSnapshot(
                new[] { Adapter("Ethernet"), Adapter("Wi-Fi") },
                1500, Array.Empty<string>(), "Ethernet", Array.Empty<string>())
        };

        NetworkAnalysisReport report = await Build(context, new StubMtuAdvice(), new StubDnsAdvice())
            .AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.Contains(report.Warnings, w => w.Code == NetworkAnalyzer.WarningMultiplePaths);
    }

    [Fact]
    public async Task UnresolvedFields_ProduceHonestWarning_NotSilentSkip()
    {
        var context = new StubContext
        {
            Snapshot = new NetworkContextSnapshot(
                new[] { Adapter("Ethernet") },
                1500, Array.Empty<string>(), "Ethernet", new[] { "Список NDIS-фильтров: нет прав." })
        };

        NetworkAnalysisReport report = await Build(context, new StubMtuAdvice(), new StubDnsAdvice())
            .AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.Contains(report.Warnings, w => w.Code == NetworkAnalyzer.WarningContextIncomplete);
        Assert.Contains("нет прав", report.Warnings.First(w => w.Code == NetworkAnalyzer.WarningContextIncomplete).DescriptionRu);
    }

    [Fact]
    public async Task MeasuredAdvice_BecomesRecommendation_WithRealCatalogText()
    {
        var mtu = new StubMtuAdvice
        {
            Value = new ComputedTargetValue("1492", TweakValueDataType.DWord, "Полезная нагрузка 1464 байт прошла без фрагментации.")
        };
        var dns = new StubDnsAdvice
        {
            Value = new ComputedTargetValue("1.1.1.1|1.0.0.1", TweakValueDataType.MultiString, "Бенчмарк DNS: победил Cloudflare.")
        };
        var context = new StubContext
        {
            Snapshot = new NetworkContextSnapshot(
                new[] { Adapter("Ethernet") }, 1500, new[] { "192.168.1.1" }, "Ethernet", Array.Empty<string>())
        };

        NetworkAnalysisReport report = await Build(context, mtu, dns).AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.Equal(2, report.Recommendations.Count);
        TweakRecommendationItem mtuItem = report.Recommendations.First(r => r.TweakCode == "MTU_SUBINTERFACE_OPTIMAL");
        Assert.Equal("1492", mtuItem.RecommendedValueDisplay);
        Assert.Contains("Сейчас: 1500 байт", mtuItem.CurrentValueDisplay, StringComparison.Ordinal);
        Assert.Contains("Полезная нагрузка", mtuItem.ReasonRu, StringComparison.Ordinal);
        Assert.Equal(GameRecommendationBadge.Recommended, mtuItem.Badge);
        Assert.Contains("Предлагается изменить: 2", report.SummaryRu, StringComparison.Ordinal);
        Assert.Contains(report.Notes, n => n.Contains("не снижают", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AlreadyRecommendedDns_IsSkipped_InsteadOfPointlessChange()
    {
        var dns = new StubDnsAdvice
        {
            Value = new ComputedTargetValue("1.1.1.1|1.0.0.1", TweakValueDataType.MultiString, "Бенчмарк DNS: победил Cloudflare.")
        };
        var context = new StubContext
        {
            Snapshot = new NetworkContextSnapshot(
                new[] { Adapter("Ethernet") }, 1500, new[] { "1.1.1.1", "1.0.0.1" }, "Ethernet", Array.Empty<string>())
        };

        NetworkAnalysisReport report = await Build(context, new StubMtuAdvice(), dns).AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode == "DNS_SERVERS_FASTEST");
        Assert.Contains(report.SkippedChecks, s => s.Contains("уже используются рекомендованные", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableAdvice_GoesToSkippedChecks_WithReason()
    {
        NetworkAnalysisReport report = await Build(new StubContext(), new StubMtuAdvice(), new StubDnsAdvice())
            .AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.Empty(report.Recommendations);
        Assert.Contains(report.SkippedChecks, s => s.Contains("MTU_SUBINTERFACE_OPTIMAL", StringComparison.Ordinal));
        Assert.Contains(report.SkippedChecks, s => s.Contains("DNS_SERVERS_FASTEST", StringComparison.Ordinal));
        Assert.Contains("Предлагается изменить: 0", report.SummaryRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCatalogEntry_IsNoted_NotSilentlyDropped()
    {
        var mtu = new StubMtuAdvice
        {
            Value = new ComputedTargetValue("1492", TweakValueDataType.DWord, "Измерено DF-пробами.")
        };

        NetworkAnalysisReport report = await Build(new StubContext(), mtu, new StubDnsAdvice(), withCatalog: false)
            .AnalyzeAsync(TweakTarget.Global("NIC"));

        Assert.Empty(report.Recommendations);
        Assert.Contains(report.Notes, n => n.Contains("твик отсутствует в каталоге", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class NetworkAnalysisServiceTests
{
    private static NetworkAnalysisService Build(SystemStateReading reading, params ITweak[] tweaks)
    {
        var reader = new FakeSystemStateReader { Reading = reading };
        var analyzer = new TweakAnalyzer(new FakeTweakCatalog(tweaks));
        return new NetworkAnalysisService(reader, analyzer, pathProbe: null, adapterResolver: new FakeActiveAdapterResolver());
    }

    [Fact]
    public async Task CapturesState_AndWarnsAboutDeliveryOptimization()
    {
        NetworkAnalysisService service = Build(new FakeSystemStateReader().Reading);

        NetworkAndOsSnapshot snapshot = await service.CaptureFullDiagnosticAsync();

        Assert.Equal("Windows 11 Pro 26100", snapshot.WindowsEditionAndBuild);
        Assert.True(snapshot.IsRunningAsAdministrator);
        Assert.Equal("Ethernet", snapshot.ActiveAdapterName);
        Assert.Equal(1500, snapshot.CurrentMtuBytes);
        Assert.Contains(snapshot.ConfiguredDnsServers, d => d == "192.168.1.1");

        Assert.Contains(snapshot.Warnings, w => w.Code == "DELIVERY_OPTIMIZATION_P2P");
        Assert.Contains(snapshot.Warnings, w => w.Code == "EXTRA_NDIS_FILTERS");
        Assert.DoesNotContain(snapshot.Warnings, w => w.Code == "NOT_ELEVATED");
    }

    [Fact]
    public async Task NoAdminRights_ProducesWarning_AndKeepsTweaksAnalysis()
    {
        SystemStateReading reading = new FakeSystemStateReader().Reading with
        {
            IsRunningAsAdministrator = false,
            UnresolvedFields = new[] { "Настройки TCP не прочитаны: нет прав." }
        };

        NetworkAnalysisService service = Build(reading, new FakeTweak("TCP_ACK_FREQUENCY", defaultValue: "0"));

        NetworkAndOsSnapshot snapshot = await service.CaptureFullDiagnosticAsync();

        Assert.True(snapshot.IsPartiallyIncomplete);
        Assert.Contains(snapshot.Warnings, w => w.Code == "NOT_ELEVATED");
        Assert.Single(snapshot.RecommendedToChangeItems);
        Assert.Equal("TCP_ACK_FREQUENCY", snapshot.RecommendedToChangeItems[0].TweakCode);
    }

    [Fact]
    public async Task MissingRestorePoint_AndUnknownFields_AreReportedHonestly()
    {
        SystemStateReading reading = new FakeSystemStateReader().Reading with
        {
            IsSystemRestoreEnabledOnSystemDrive = false,
            IsTimerResolutionRaised = false,
            ConfiguredDnsServers = Array.Empty<string>(),
            UnresolvedFields = new[] { "Список DNS-серверов: команда не ответила." }
        };

        NetworkAnalysisService service = Build(reading);

        NetworkAndOsSnapshot snapshot = await service.CaptureFullDiagnosticAsync();

        Assert.Contains(snapshot.Warnings, w => w.Code == "NO_RESTORE_POINT");
        Assert.Contains(snapshot.Warnings, w => w.Code == "SYSTEM_TIMER_LOW");
        Assert.Equal(new[] { "Список DNS-серверов: команда не ответила." }, snapshot.UnresolvedFields);
    }
}
