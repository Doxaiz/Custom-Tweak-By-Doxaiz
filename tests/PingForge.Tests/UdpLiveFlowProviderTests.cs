using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Orchestration.Realtime;
using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Живой UDP-поток из наблюдений сетевого стека: провайдер, которого раньше не было, и честный отчёт о нём.
/// </summary>
/// <remarks>
/// Интерфейс <see cref="ILiveFlowRttProvider"/> был объявлен и нигде не реализован, поэтому цепочка замера
/// до него не доходила: числа всегда брались пробами. Теперь для UDP есть реализация, и её нужно проверить
/// трижды: разбор адреса, сборка образца из пар «отправка → ответ» и текст отчёта, который не имеет права
/// говорить про ретрансмиссии там, где их не бывает.
/// </remarks>
public sealed class UdpLiveFlowProviderTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 13, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("1.2.3.4:27015", true, "1.2.3.4", 27015)]
    [InlineData(" 1.2.3.4:27015 ", true, "1.2.3.4", 27015)]
    [InlineData("[2001:db8::1]:27015", true, "2001:db8::1", 27015)]
    [InlineData("1.2.3.4", false, "", 0)]
    [InlineData("1.2.3.4:", false, "", 0)]
    [InlineData("1.2.3.4:0", false, "", 0)]
    [InlineData("1.2.3.4:70000", false, "", 0)]
    [InlineData("1.2.3.4:порт", false, "", 0)]
    [InlineData("", false, "", 0)]
    public void Адрес_сервера_разбирается_только_с_портом(string endpoint, bool expected, string address, int port)
    {
        bool parsed = UdpObservationsLiveFlowRttProvider.TrySplitEndpoint(endpoint, out string parsedAddress, out int parsedPort);

        Assert.True(parsed == expected, $"разбор «{endpoint}»: получилось {parsed}, ждали {expected}");
        Assert.Equal(address, parsedAddress);
        Assert.Equal(port, parsedPort);
    }

    [Fact]
    public void Замер_собирается_из_пар_и_называет_свой_метод()
    {
        // Замеры подобраны так, чтобы среднее и медиана не совпали: подмена одного другим была бы враньём.
        PassiveUdpRttTracker tracker = Tracker(new[] { 10, 20, 100 });

        var provider = new UdpObservationsLiveFlowRttProvider(tracker);

        Assert.True(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out LiveFlowRttSample sample));
        Assert.Equal(UdpObservationsLiveFlowRttProvider.MethodName, sample.MethodName);
        Assert.Equal("udp-passive", sample.MethodName);
        Assert.Equal(3, sample.SamplesAvailable);

        Assert.Equal(43.33, sample.RttAverageMs, 2);
        Assert.Equal(20.0, sample.RttMedianMs!.Value, 2);

        // Джиттер — та же мера, что у активных проб: среднее |20−10| и |100−20| = 45.
        Assert.Equal(45.0, sample.JitterMs, 2);

        // У UDP ретрансмиссий не бывает: ноль здесь — не «потерь нет», а «механизма нет».
        Assert.Equal(0, sample.Retransmissions);
    }

    [Fact]
    public void Без_порта_замера_нет()
    {
        var provider = new UdpObservationsLiveFlowRttProvider(Tracker(new[] { 30 }));

        Assert.False(provider.TryGetLiveFlowRtt(0, Peer, out _));
        Assert.False(provider.TryGetLiveFlowRtt(0, string.Empty, out _));
    }

    [Fact]
    public void Чужой_собеседник_не_выдаётся()
    {
        var provider = new UdpObservationsLiveFlowRttProvider(
            Tracker(new[] { 30 }, address: "203.0.113.99"));

        Assert.False(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out _));
    }

    [Fact]
    public void Номер_процесса_уточняет_выбор_а_ноль_берёт_самый_свежий()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(30));

        // Первый процесс: 10 мс, потом второй: 90 мс (он свежее).
        ObservePair(tracker, processId: 1, rttMs: 10, at: T0);
        ObservePair(tracker, processId: 2, rttMs: 90, at: T0.AddSeconds(5));

        var provider = new UdpObservationsLiveFlowRttProvider(tracker);

        Assert.True(provider.TryGetLiveFlowRtt(1, $"{Peer}:{Port}", out LiveFlowRttSample first));
        Assert.Equal(10.0, first.RttMedianMs!.Value, 3);

        Assert.True(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out LiveFlowRttSample freshest));
        Assert.Equal(90.0, freshest.RttMedianMs!.Value, 3);
    }

    [Fact]
    public void Без_полных_пар_провайдер_молчит()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0);

        // Только ответы, без отправок: сопоставлять не с чем.
        tracker.Observe(new UdpPeerObservation(4242, Peer, Port, IsSend: false, ObservedUtc: T0));

        var provider = new UdpObservationsLiveFlowRttProvider(tracker);

        Assert.False(provider.TryGetLiveFlowRtt(4242, $"{Peer}:{Port}", out _));
    }

    [Fact]
    public async Task Проба_живого_потока_не_отправляет_пакетов_и_не_говорит_про_ретрансмиссии()
    {
        PassiveUdpRttTracker tracker = Tracker(new[] { 38, 42 });
        var icmp = new StubIcmpProbe();
        int icmpCalls = 0;
        icmp.Handler = (_, _) =>
        {
            icmpCalls++;
            return new IcmpProbeReply(IcmpProbeStatus.Success, 12, null);
        };

        var service = new RouteProbeService(
            icmp,
            new StubTcpProbe(),
            new StubHostResolver(),
            new HopTracerouteEngine(icmp),
            new UdpObservationsLiveFlowRttProvider(tracker));

        ProbeStatistics stats = await service.ProbeEndpointAsync(Peer, Port, "живая оптимизация", 12);

        Assert.Equal(ProbeMethodUsed.LiveUdpPassiveFlow, stats.MethodUsed);
        Assert.True(stats.IsMeasurable);
        Assert.Equal(40.0, stats.RttMedianMs!.Value, 3);

        // Ни одного активного пакета: живой поток читается из наблюдений.
        Assert.Equal(0, icmpCalls);

        Assert.Contains("живого потока", stats.StatusMessageRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ретрансмиссий в UDP не бывает", stats.StatusMessageRu, StringComparison.Ordinal);
        Assert.DoesNotContain("Ретрансмиссий за окно", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Неотвеченные_отправки_называются_числом_и_не_выдаются_за_потери()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(10));

        // Отправка без ответа, затем полная пара.
        tracker.Observe(new UdpPeerObservation(4242, Peer, Port, IsSend: true, ObservedUtc: T0));
        tracker.Observe(new UdpPeerObservation(4242, Peer, Port, IsSend: true, ObservedUtc: T0.AddSeconds(4)));
        tracker.Observe(new UdpPeerObservation(4242, Peer, Port, IsSend: false, ObservedUtc: T0.AddSeconds(4).AddMilliseconds(35)));

        var service = new RouteProbeService(
            new StubIcmpProbe(),
            new StubTcpProbe(),
            new StubHostResolver(),
            new HopTracerouteEngine(new StubIcmpProbe()),
            new UdpObservationsLiveFlowRttProvider(tracker));

        ProbeStatistics stats = await service.ProbeEndpointAsync(Peer, Port, "живая оптимизация", 12);

        Assert.Contains("Отправок без ответа за окно: 1", stats.StatusMessageRu, StringComparison.Ordinal);
        Assert.Contains("не измеренные потери", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TCP_поток_оставляет_разговор_о_ретрансмиссиях_и_среднем()
    {
        var stub = new SampleProvider(new LiveFlowRttSample("tcp", 21.4, 3.1, 2, 120));

        var service = new RouteProbeService(
            new StubIcmpProbe(),
            new StubTcpProbe(),
            new StubHostResolver(),
            new HopTracerouteEngine(new StubIcmpProbe()),
            stub);

        ProbeStatistics stats = await service.ProbeEndpointAsync(Peer, Port, "живая оптимизация", 12);

        Assert.Equal(ProbeMethodUsed.LiveTcpEstats, stats.MethodUsed);
        Assert.Contains("Ретрансмиссий за окно наблюдения: 2", stats.StatusMessageRu, StringComparison.Ordinal);

        // Медианы отдельных замеров у TCP-статистики нет — в отчёте среднее, и это названо.
        Assert.Equal(21.4, stats.RttMedianMs!.Value, 3);
        Assert.Contains("Медиана по отдельным замерам недоступна", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Живой_поток_доходит_до_отчёта_оптимизации_как_измеренный()
    {
        // Прежде чем подключать провайдера, надо убедиться, что его числа не превращаются в «не измеряется»:
        // отчёт оптимизации берёт медиану, а пассивный замер её отдаёт.
        var service = new RouteProbeService(
            new StubIcmpProbe(),
            new StubTcpProbe(),
            new StubHostResolver(),
            new HopTracerouteEngine(new StubIcmpProbe()),
            new UdpObservationsLiveFlowRttProvider(Tracker(new[] { 10, 20, 100 })));

        ProbeStatistics stats = await service.ProbeEndpointAsync(Peer, Port, "живая оптимизация", 12);
        var source = new RouteProbeMeasurementSource(new FixedProbe(stats), samplesPerWindow: 12);

        PathMeasurement path = await source.MeasurePathAsync(Peer, Port);

        Assert.True(path.IsMeasurable);
        Assert.Equal(20.0, path.RttMedianMs, 2);
        Assert.Equal(45.0, path.JitterMs!.Value, 2);
        Assert.Contains("пассивные метки", path.MethodRu, StringComparison.Ordinal);
        Assert.Equal(ProbeMethodUsed.LiveUdpPassiveFlow, path.Method);
    }

    private const string Peer = "203.0.113.10";
    private const int Port = 27015;

    private static PassiveUdpRttTracker Tracker(int[] rtts, string address = Peer)
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(10));

        foreach (int rtt in rtts)
        {
            tracker.Observe(new UdpPeerObservation(4242, address, Port, IsSend: true, ObservedUtc: T0));
            tracker.Observe(new UdpPeerObservation(4242, address, Port, IsSend: false, ObservedUtc: T0.AddMilliseconds(rtt)));
        }

        return tracker;
    }

    private static void ObservePair(PassiveUdpRttTracker tracker, int processId, int rttMs, DateTime at)
    {
        tracker.Observe(new UdpPeerObservation(processId, Peer, Port, IsSend: true, ObservedUtc: at));
        tracker.Observe(new UdpPeerObservation(processId, Peer, Port, IsSend: false, ObservedUtc: at.AddMilliseconds(rttMs)));
    }

    private sealed class FixedProbe : IRouteProbe
    {
        private readonly ProbeStatistics _statistics;

        public FixedProbe(ProbeStatistics statistics) => _statistics = statistics;

        public Task<ProbeStatistics> ProbeEndpointAsync(string hostOrIp, int port, string routeLabel, int sampleCount = 30, CancellationToken ct = default) =>
            Task.FromResult(_statistics);

        public Task<IReadOnlyList<RouteHopMetric>> TraceRouteAsync(string targetHostOrIp, int maxHops = 12, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RouteHopMetric>>(Array.Empty<RouteHopMetric>());
    }

    private sealed class SampleProvider : ILiveFlowRttProvider
    {
        private readonly LiveFlowRttSample _sample;

        public SampleProvider(LiveFlowRttSample sample) => _sample = sample;

        public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
        {
            sample = _sample;
            return true;
        }
    }
}

/// <summary>
/// Сторож формулировки: программа обязана называть источник чисел, а не молчать о том, что часть замеров
/// идёт лишними пакетами. Утверждение «чтение потока не подключено» было правдой до этой волны — теперь
/// оно неправда, и вернуться незамеченным не должно.
/// </summary>
public sealed class LiveFlowWiringTests
{
    [Fact]
    public void Текст_замера_называет_оба_источника_и_не_обещает_лишнего()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-RealtimeOptimizer.cs");
        Assert.True(File.Exists(path), $"нет копии RealtimeOptimizer.cs для проверки: {path}");

        string optimizer = File.ReadAllText(path);

        Assert.Contains("живой поток игры", optimizer, StringComparison.Ordinal);
        Assert.Contains("ICMP-эхо и TCP-рукопожатие", optimizer, StringComparison.Ordinal);
        Assert.Contains("лишних пакетов нет", optimizer, StringComparison.Ordinal);
        Assert.DoesNotContain("пока не подключено", optimizer, StringComparison.Ordinal);
    }

    [Fact]
    public void Служба_подключает_пассивный_замер_к_пробам()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-ServiceProgram.cs");
        string program = File.ReadAllText(path);

        Assert.Contains("new PassiveUdpRttTracker()", program, StringComparison.Ordinal);
        Assert.Contains("UdpObservationsLiveFlowRttProvider", program, StringComparison.Ordinal);
        Assert.Contains("onObservation:", program, StringComparison.Ordinal);
    }
}
