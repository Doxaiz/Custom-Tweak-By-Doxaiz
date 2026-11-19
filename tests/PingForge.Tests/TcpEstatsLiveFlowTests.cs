using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Живой TCP-поток: задержка из статистики уже открытого соединения. Сам вызов iphlpapi на Linux
/// не проверить, поэтому проверяется всё, что от него не зависит: выбор соединения, отказы,
/// перевод микросекунд, передача ретрансмиссий и честные тексты.
/// </summary>
public sealed class TcpEstatsLiveFlowTests
{
    private const string Peer = "203.0.113.20";
    private const int Port = 25565;

    [Fact]
    public void Соединение_ищется_по_адресу_и_порту_а_не_по_похожести()
    {
        var provider = new TcpEstatsLiveFlowRttProvider(new FakeReader(
            Connection("198.51.100.7", 25565, rtt: 40, samples: 5),
            Connection(Peer, Port, rtt: 21.5, variation: 2.5, samples: 40)));

        Assert.True(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out LiveFlowRttSample sample));
        Assert.Equal(21.5, sample.RttAverageMs, 3);
        Assert.Equal(2.5, sample.JitterMs, 3);
        Assert.Equal(40, sample.SamplesAvailable);
        Assert.Equal(TcpEstatsLiveFlowRttProvider.MethodName, sample.MethodName);
        Assert.Null(sample.RttMedianMs);
    }

    [Fact]
    public void Чужое_соединение_не_выдаётся_за_игровое()
    {
        var provider = new TcpEstatsLiveFlowRttProvider(new FakeReader(
            Connection("198.51.100.7", 25565, rtt: 40, samples: 5)));

        Assert.False(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out _));
        Assert.Contains($"{Peer}:{Port}", provider.LastProblemRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Нулевая_задержка_это_не_замер()
    {
        var provider = new TcpEstatsLiveFlowRttProvider(new FakeReader(
            Connection(Peer, Port, rtt: 0, samples: 0)));

        Assert.False(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out _));
        Assert.Contains("измерений RTT", provider.LastProblemRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Упавшее_чтение_не_становится_числом()
    {
        var provider = new TcpEstatsLiveFlowRttProvider(new ThrowingReader());

        Assert.False(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out _));
        Assert.Contains("прочитать не удалось", provider.LastProblemRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Ретрансмиссии_берутся_из_того_же_снимка_соединения()
    {
        var provider = new TcpEstatsLiveFlowRttProvider(new FakeReader(
            Connection(Peer, Port, rtt: 30, samples: 12, retransmissions: 7)));

        Assert.True(provider.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out LiveFlowRttSample sample));
        Assert.Equal(7, sample.Retransmissions);
    }

    [Fact]
    public void Без_порта_соединение_не_ищется()
    {
        var provider = new TcpEstatsLiveFlowRttProvider(new FakeReader(
            Connection(Peer, Port, rtt: 30, samples: 12)));

        Assert.False(provider.TryGetLiveFlowRtt(0, Peer, out _));
        Assert.Contains("без порта", provider.LastProblemRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Читатель_на_чужой_системе_называет_причину()
    {
        // На Windows этот случай — живая проверка P/Invoke: список соединений читается системой,
        // и отсутствие брокера статистики превращается в названную причину, а не в исключение.
        var reader = new WindowsTcpEstatsReader();

        IReadOnlyList<LiveTcpConnectionStats> connections = reader.ReadEstablishedConnections(Environment.ProcessId);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(connections);
            Assert.Contains("только на Windows", reader.ProblemRu, StringComparison.Ordinal);
        }
        else
        {
            // На Windows вызов настоящий: важно, что он не падает и не выдаёт отрицательных чисел.
            Assert.All(connections, connection => Assert.True(connection.SmoothedRttMs >= 0));
        }
    }

    [Fact]
    public void Микросекунды_становятся_миллисекундами()
    {
        Assert.Equal(21.5, TcpEstatsMath.MicrosecondsToMilliseconds(21_500), 3);
        Assert.Equal(1.234, TcpEstatsMath.MicrosecondsToMilliseconds(1_234), 3);
        Assert.Equal(0.0, TcpEstatsMath.MicrosecondsToMilliseconds(0), 3);
    }

    [Fact]
    public void Цепочка_берёт_первого_кто_знает_числа()
    {
        var silent = new SilentProvider();
        var knowing = new StubProvider(TcpEstatsLiveFlowRttProvider.MethodName, 12.5);

        var chain = new ChainedLiveFlowRttProvider(silent, knowing);

        Assert.True(chain.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out LiveFlowRttSample sample));
        Assert.Equal(12.5, sample.RttAverageMs, 3);
        Assert.Equal(1, silent.Calls);
        Assert.Equal(1, knowing.Calls);
    }

    [Fact]
    public void Цепочка_не_спрашивает_следующего_если_первый_ответил()
    {
        var first = new StubProvider(UdpObservationsLiveFlowRttProvider.MethodName, 18.0);
        var second = new StubProvider(TcpEstatsLiveFlowRttProvider.MethodName, 99.0);

        var chain = new ChainedLiveFlowRttProvider(first, second);

        Assert.True(chain.TryGetLiveFlowRtt(0, $"{Peer}:{Port}", out LiveFlowRttSample sample));
        Assert.Equal(UdpObservationsLiveFlowRttProvider.MethodName, sample.MethodName);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Tcp_статистика_доходит_до_отчёта_как_живой_поток()
    {
        // Отчёт должен узнать способ сам: по имени метода из образца, а не по настройке.
        var service = new RouteProbeService(
            new StubIcmpProbe(),
            new StubTcpProbe(),
            new StubHostResolver(),
            new HopTracerouteEngine(new StubIcmpProbe()),
            new TcpEstatsLiveFlowRttProvider(new FakeReader(
                Connection(Peer, Port, rtt: 21.4, variation: 3.1, samples: 120, retransmissions: 2))));

        ProbeStatistics stats = await service.ProbeEndpointAsync(Peer, Port, "живая оптимизация", 12);

        Assert.Equal(ProbeMethodUsed.LiveTcpEstats, stats.MethodUsed);
        Assert.Equal(ProbeMethodTags.LiveTcp, ProbeMethodTags.Tag(stats.MethodUsed));
        Assert.Equal(21.4, stats.RttAvgMs!.Value, 3);
        Assert.Contains("Ретрансмиссий за окно наблюдения: 2", stats.StatusMessageRu, StringComparison.Ordinal);
        Assert.Contains("RttVar", stats.StatusMessageRu, StringComparison.Ordinal);
        Assert.Contains("Медиана по отдельным замерам недоступна", stats.StatusMessageRu, StringComparison.Ordinal);
    }

    private static LiveTcpConnectionStats Connection(
        string address, int port, double rtt, double variation = 0, int samples = 1, int retransmissions = 0) =>
        new(address, port, rtt, variation, samples, retransmissions);

    private sealed class FakeReader : ITcpConnectionStatsReader
    {
        private readonly IReadOnlyList<LiveTcpConnectionStats> _connections;

        public FakeReader(params LiveTcpConnectionStats[] connections) => _connections = connections;

        public string ProblemRu => "Подставной читатель: системы нет, числа заданы тестом.";

        public IReadOnlyList<LiveTcpConnectionStats> ReadEstablishedConnections(int processId) => _connections;
    }

    private sealed class ThrowingReader : ITcpConnectionStatsReader
    {
        public string ProblemRu => string.Empty;

        public IReadOnlyList<LiveTcpConnectionStats> ReadEstablishedConnections(int processId) =>
            throw new InvalidOperationException("статистика недоступна");
    }

    private sealed class SilentProvider : ILiveFlowRttProvider
    {
        public int Calls { get; private set; }

        public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
        {
            Calls++;
            sample = null!;
            return false;
        }
    }

    private sealed class StubProvider : ILiveFlowRttProvider
    {
        private readonly string _methodName;
        private readonly double _rtt;

        public StubProvider(string methodName, double rtt)
        {
            _methodName = methodName;
            _rtt = rtt;
        }

        public int Calls { get; private set; }

        public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
        {
            Calls++;
            sample = new LiveFlowRttSample(_methodName, _rtt, 1.0, Retransmissions: 0, SamplesAvailable: 10);
            return true;
        }
    }
}
