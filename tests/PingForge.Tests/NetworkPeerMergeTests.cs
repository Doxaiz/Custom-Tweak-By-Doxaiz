using System.Net.Sockets;
using PingForge.Network;
using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Проверки перевода чисел из структур Windows и сведения таблицы соединений с найденными собеседниками.
/// Это то место, где ошибка выглядит как «сервер 0.0.0.0:0» или «игр не найдено», поэтому проверяем
/// обе вещи отдельно: и саму арифметику, и поведение при неполных данных.
/// </summary>
public class NetworkPeerMergeTests
{
    // ── Перевод значений из структур iphlpapi ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(524u, 3074)]     // 0x020C → 0x0C02: порт 3074 в сетевом порядке
    [InlineData(20480u, 80)]     // 0x5000 → 0x0050: порт 80
    [InlineData(65535u, 65535)]
    [InlineData(0u, 0)]
    public void PortFromNetworkOrder_SwapsBytes(uint raw, int expected)
    {
        Assert.Equal(expected, NativeTableMapping.PortFromNetworkOrder(raw));
    }

    [Fact]
    public void Ipv4FromNetworkOrder_ReadsAddressAsIs()
    {
        // 34.16.1.5 в сетевом порядке — это 0x22100105; конструктор IPAddress(uint) читает число
        // в порядке памяти, поэтому байты переставляются явно (см. комментарий в NativeTableMapping)
        Assert.Equal("34.16.1.5", NativeTableMapping.Ipv4FromNetworkOrder(0x22100105u));
        Assert.Equal("0.0.0.0", NativeTableMapping.Ipv4FromNetworkOrder(0u));
    }

    [Theory]
    [InlineData(1, "Closed")]
    [InlineData(2, "Listen")]
    [InlineData(5, "Established")]
    [InlineData(11, "TimeWait")]
    [InlineData(99, "Unknown(99)")]
    public void TcpStateFromCode_MapsKnownStatesAndAdmitsUnknown(int code, string expected)
    {
        Assert.Equal(expected, NativeTableMapping.TcpStateFromCode(code));
    }

    [Fact]
    public void StreamCandidate_RequiresEstablishedState()
    {
        Assert.True(NativeTableMapping.IsStreamCandidate("Established"));
        Assert.False(NativeTableMapping.IsStreamCandidate("Listen"));
        Assert.False(NativeTableMapping.IsStreamCandidate("TimeWait"));
    }

    [Fact]
    public void Endpoint_FormatsIpv6InBrackets()
    {
        Assert.Equal("34.16.1.5:3074", NativeTableMapping.Endpoint("34.16.1.5", 3074, AddressFamily.InterNetwork));
        Assert.Equal("[2001:db8::1]:3074", NativeTableMapping.Endpoint("2001:db8::1", 3074, AddressFamily.InterNetworkV6));
        Assert.Equal("UDP", NativeTableMapping.DescribeProtocol(ConnectionProtocol.Udp));
        Assert.Equal(ConnectionProtocol.Udp, NativeTableMapping.ProtocolFromTableKind(isUdp: true));
    }

    // ── Сведение таблицы соединений и найденных собеседников ──────────────────────────────────────

    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Merge_AddsUdpPeerThatTableCannotKnow()
    {
        var rows = new List<RawConnectionRow>
        {
            // так выглядит UDP-сокет игры в таблице Windows: собеседник неизвестен
            new(ConnectionProtocol.Udp, 8124, "192.168.1.50", 51234, "0.0.0.0", 0, "")
        };

        var peers = new List<TrackedRemotePeer>
        {
            new(8124, "34.16.1.5", 3074, ConnectionProtocol.Udp, Now.AddSeconds(-3))
        };

        IReadOnlyList<RawConnectionRow> merged = ConnectionRowMerger.Merge(rows, peers, TimeSpan.FromSeconds(30), Now);

        Assert.Equal(2, merged.Count);
        RawConnectionRow added = merged[1];
        Assert.Equal(3074, added.RemotePort);
        Assert.Equal("34.16.1.5", added.RemoteAddress);
        Assert.Equal(string.Empty, added.TcpState); // «Established» для UDP было бы выдумкой
    }

    [Fact]
    public void Merge_DoesNotDuplicateKnownTcpRow()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Tcp, 900, "192.168.1.50", 50000, "52.14.7.9", 443, "Established")
        };

        var peers = new List<TrackedRemotePeer>
        {
            new(900, "52.14.7.9", 443, ConnectionProtocol.Tcp, Now)
        };

        Assert.Single(ConnectionRowMerger.Merge(rows, peers, TimeSpan.FromSeconds(30), Now));
    }

    [Fact]
    public void Merge_DropsStalePeersAndInvalidEntries()
    {
        var peers = new List<TrackedRemotePeer>
        {
            new(8124, "34.16.1.5", 3074, ConnectionProtocol.Udp, Now.AddMinutes(-5)), // остыл
            new(8124, "34.16.1.9", 0, ConnectionProtocol.Udp, Now),                 // порт не определён
            new(8124, "  ", 3074, ConnectionProtocol.Udp, Now)                      // адрес пустой
        };

        Assert.Empty(ConnectionRowMerger.Merge(Array.Empty<RawConnectionRow>(), peers, TimeSpan.FromSeconds(30), Now));
    }

    [Fact]
    public async Task Watcher_WithPeerTracker_FindsUdpOnlyGame()
    {
        // Игра общается только по UDP: в таблице соединений виден лишь локальный сокет.
        var table = new StubTable(new[]
        {
            new RawConnectionRow(ConnectionProtocol.Udp, 8124, "192.168.1.50", 51234, "0.0.0.0", 0, "")
        });

        var names = new StubNames(new Dictionary<int, string> { [8124] = "FortniteClient-Win64-Shipping.exe" });

        // Без источника собеседников поток найти нельзя — и это честно видно в отчёте.
        var without = new WindowsGameFlowWatcher(table, names);
        GameFlowDetectionReport plain = await without.DetectAsync();
        Assert.Empty(plain.Games);
        Assert.Contains(plain.NotesRu, n => n.Contains("трассировки сетевого стека", StringComparison.Ordinal));

        var with = new WindowsGameFlowWatcher(
            table,
            names,
            peerTracker: new StubPeerTracker(new[]
            {
                new TrackedRemotePeer(8124, "34.16.1.5", 3074, ConnectionProtocol.Udp, DateTime.UtcNow)
            }));

        GameFlowDetectionReport report = await with.DetectAsync();

        DetectedGameFlow flow = Assert.Single(report.Games);
        Assert.Equal("Fortnite", flow.GameTitleRu);
        Assert.Equal("34.16.1.5:3074", flow.EndpointText);
        Assert.Equal("UDP", flow.Flow.Transport);
    }

    private sealed class StubTable : IRawConnectionTableProvider
    {
        private readonly IReadOnlyList<RawConnectionRow> _rows;

        public StubTable(IReadOnlyList<RawConnectionRow> rows) => _rows = rows;

        public Task<IReadOnlyList<RawConnectionRow>> ReadAsync(CancellationToken ct = default) => Task.FromResult(_rows);
    }

    private sealed class StubNames : IProcessNameResolver
    {
        private readonly IReadOnlyDictionary<int, string> _names;

        public StubNames(IReadOnlyDictionary<int, string> names) => _names = names;

        public Task<string?> TryGetProcessNameAsync(int processId, CancellationToken ct = default) =>
            Task.FromResult(_names.TryGetValue(processId, out string? name) ? name : null);
    }

    private sealed class StubPeerTracker : IRemotePeerTracker
    {
        private readonly IReadOnlyList<TrackedRemotePeer> _peers;

        public StubPeerTracker(IReadOnlyList<TrackedRemotePeer> peers) => _peers = peers;

        public Task<IReadOnlyList<TrackedRemotePeer>> FindActivePeersAsync(CancellationToken ct = default) =>
            Task.FromResult(_peers);
    }
}
