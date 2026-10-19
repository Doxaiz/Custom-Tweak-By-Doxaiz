using System.Buffers.Binary;
using System.Net;
using PingForge.Network;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Наблюдение UDP-собеседников: разбор событий ядра и короткая память о них.
///
/// Почему это проверяется отдельно от Windows: разбор получает только номер события и сырые байты,
/// поэтому ошибку «взяли свой адрес вместо адреса сервера» видно на любом сборочном сервере. Сама
/// тонкая часть (сеанс ETW) живёт в Windows-проекте и проверяется живым прогоном на машине человека.
///
/// Отдельно закреплено то, где источники расходятся: манифест провайдера говорит, что при получении
/// собеседник лежит в saddr, а живой замер в открытом проекте увидел в saddr собственный адрес.
/// Разбор обязан в обоих случаях выбрать публичный адрес, а не «свой».
/// </summary>
public sealed class UdpPeerDetectionTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static bool Parse(
        byte[] payload,
        ushort eventId,
        out UdpPeerObservation? peer,
        int headerProcessId = 0,
        Func<string, bool>? isOwnAddress = null,
        byte version = KernelNetworkUdpEventParser.SupportedEventVersion) =>
        KernelNetworkUdpEventParser.TryParse(
            payload, eventId, version, headerProcessId, Now, out peer, isOwnAddress);

    // ── Разбор полезной нагрузки ────────────────────────────────────────────────────────────────

    [Fact]
    public void UDP_Send_IPv4_берёт_адрес_и_порт_получателя()
    {
        byte[] payload = Ipv4Payload(pid: 8124, daddr: "34.16.1.5", saddr: "192.168.1.50", dport: 3074, sport: 51234);

        Assert.True(Parse(payload, KernelNetworkUdpEventParser.UdpSendIpv4EventId, out UdpPeerObservation? peer));

        Assert.NotNull(peer);
        Assert.Equal(8124, peer!.ProcessId);
        Assert.Equal("34.16.1.5", peer.RemoteAddress);
        Assert.Equal(3074, peer.RemotePort);
        Assert.True(peer.IsSend);
    }

    [Fact]
    public void UDP_Recv_IPv4_берёт_адрес_и_порт_отправителя()
    {
        byte[] payload = Ipv4Payload(pid: 8124, daddr: "192.168.1.50", saddr: "34.16.1.5", dport: 51234, sport: 3074);

        Assert.True(Parse(payload, KernelNetworkUdpEventParser.UdpRecvIpv4EventId, out UdpPeerObservation? peer));

        Assert.NotNull(peer);
        Assert.Equal("34.16.1.5", peer!.RemoteAddress);
        Assert.Equal(3074, peer.RemotePort);
        Assert.False(peer.IsSend);
    }

    [Fact]
    public void UDP_Recv_IPv4_когда_в_источнике_свой_адрес_берётся_другая_сторона()
    {
        // Так выглядит событие, если прав живой замер (observer): saddr — свой публичный адрес,
        // daddr — собеседник. Собственный адрес «сервером игры» стать не должен.
        byte[] payload = Ipv4Payload(pid: 8124, daddr: "34.16.1.5", saddr: "203.0.113.7", dport: 3074, sport: 51234);

        Assert.True(Parse(
            payload,
            KernelNetworkUdpEventParser.UdpRecvIpv4EventId,
            out UdpPeerObservation? peer,
            isOwnAddress: address => address == "203.0.113.7"));

        Assert.NotNull(peer);
        Assert.Equal("34.16.1.5", peer!.RemoteAddress);
        Assert.Equal(3074, peer.RemotePort);
    }

    [Fact]
    public void UDP_Send_IPv6_читает_16_байт_адреса()
    {
        byte[] payload = Ipv6Payload(
            pid: 9001,
            daddr: IPAddress.Parse("2001:db8::1"),
            saddr: IPAddress.Parse("fe80::1234"),
            dport: 7777,
            sport: 50000);

        Assert.True(Parse(payload, KernelNetworkUdpEventParser.UdpSendIpv6EventId, out UdpPeerObservation? peer));

        Assert.NotNull(peer);
        Assert.Equal("2001:db8::1", peer!.RemoteAddress);
        Assert.Equal(7777, peer.RemotePort);
    }

    [Fact]
    public void UDP_Recv_IPv6_читает_адрес_отправителя()
    {
        byte[] payload = Ipv6Payload(
            pid: 9001,
            daddr: IPAddress.Parse("fe80::1234"),
            saddr: IPAddress.Parse("2001:db8::9"),
            dport: 50000,
            sport: 7777);

        Assert.True(Parse(payload, KernelNetworkUdpEventParser.UdpRecvIpv6EventId, out UdpPeerObservation? peer));

        Assert.Equal("2001:db8::9", peer!.RemoteAddress);
        Assert.Equal(7777, peer.RemotePort);
        Assert.False(peer.IsSend);
    }

    [Fact]
    public void Событие_другой_версии_не_разбирается_как_эта()
    {
        // Версия 1 может передвинуть поля — молча разбирать её нельзя (так же поступает PerfView
        // с классическим провайдером TcpIp: у него смещения действительно менялись).
        byte[] payload = Ipv4Payload(pid: 8124, daddr: "34.16.1.5", saddr: "192.168.1.50", dport: 3074, sport: 51234);

        Assert.False(Parse(
            payload,
            KernelNetworkUdpEventParser.UdpSendIpv4EventId,
            out UdpPeerObservation? peer,
            version: 1));

        Assert.Null(peer);
    }

    [Fact]
    public void TCP_события_10_и_11_не_считаются_UDP()
    {
        byte[] payload = Ipv4Payload(pid: 8124, daddr: "34.16.1.5", saddr: "192.168.1.50", dport: 443, sport: 51000);

        Assert.False(KernelNetworkUdpEventParser.IsUdpEvent(KernelNetworkUdpEventParser.TcpSendIpv4EventId));
        Assert.False(KernelNetworkUdpEventParser.IsUdpEvent(KernelNetworkUdpEventParser.TcpRecvIpv4EventId));
        Assert.False(Parse(payload, KernelNetworkUdpEventParser.TcpSendIpv4EventId, out _));
        Assert.False(Parse(payload, KernelNetworkUdpEventParser.TcpRecvIpv4EventId, out _));
    }

    [Theory]
    [InlineData(27, KernelNetworkUdpEventParser.UdpSendIpv4EventId)]
    [InlineData(0, KernelNetworkUdpEventParser.UdpRecvIpv4EventId)]
    [InlineData(51, KernelNetworkUdpEventParser.UdpSendIpv6EventId)]
    public void Обрезанная_полезная_нагрузка_отклоняется(int length, ushort eventId)
    {
        byte[] payload = new byte[length];

        Assert.False(Parse(payload, eventId, out UdpPeerObservation? peer));
        Assert.Null(peer);
    }

    [Fact]
    public void PID_из_полезной_нагрузки_важнее_заголовка_а_ноль_берётся_из_заголовка()
    {
        byte[] fromPayload = Ipv4Payload(pid: 8124, daddr: "34.16.1.5", saddr: "10.0.0.2", dport: 3074, sport: 5000);
        Assert.True(Parse(fromPayload, KernelNetworkUdpEventParser.UdpSendIpv4EventId, out UdpPeerObservation? peer, headerProcessId: 999));
        Assert.Equal(8124, peer!.ProcessId);

        byte[] noPid = Ipv4Payload(pid: 0, daddr: "34.16.1.5", saddr: "10.0.0.2", dport: 3074, sport: 5000);
        Assert.True(Parse(noPid, KernelNetworkUdpEventParser.UdpSendIpv4EventId, out UdpPeerObservation? fallback, headerProcessId: 4242));
        Assert.Equal(4242, fallback!.ProcessId);
    }

    [Fact]
    public void Нулевой_порт_или_безымянный_процесс_наблюдение_не_создают()
    {
        byte[] noPort = Ipv4Payload(pid: 8124, daddr: "34.16.1.5", saddr: "10.0.0.2", dport: 0, sport: 5000);
        Assert.False(Parse(noPort, KernelNetworkUdpEventParser.UdpSendIpv4EventId, out _));

        byte[] noProcess = Ipv4Payload(pid: 0, daddr: "34.16.1.5", saddr: "10.0.0.2", dport: 3074, sport: 5000);
        Assert.False(Parse(noProcess, KernelNetworkUdpEventParser.UdpSendIpv4EventId, out _, headerProcessId: 0));
    }

    [Fact]
    public void Два_приватных_адреса_собеседником_не_считаются()
    {
        // Игра по локальной сети: публичного конца нет, значит и «сервера игры» здесь нет.
        byte[] payload = Ipv4Payload(pid: 8124, daddr: "192.168.1.5", saddr: "192.168.1.50", dport: 3074, sport: 51234);

        Assert.False(Parse(payload, KernelNetworkUdpEventParser.UdpSendIpv4EventId, out UdpPeerObservation? peer));
        Assert.Null(peer);
    }

    // ── Короткая память о собеседниках ──────────────────────────────────────────────────────────

    [Fact]
    public void Хранилище_отдаёт_наблюдённого_собеседника_как_UDP()
    {
        var store = new RecentUdpPeerStore(clock: () => Now);

        Assert.True(store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 3074, true, Now)));

        TrackedRemotePeer peer = Assert.Single(store.Snapshot());
        Assert.Equal(8124, peer.ProcessId);
        Assert.Equal("34.16.1.5", peer.RemoteAddress);
        Assert.Equal(3074, peer.RemotePort);
        Assert.Equal(ConnectionProtocol.Udp, peer.Protocol);
        Assert.Equal(Now, peer.ObservedUtc);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.5.4")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.1")]
    public void Приватные_и_служебные_адреса_не_сохраняются(string address)
    {
        var store = new RecentUdpPeerStore(clock: () => Now);

        Assert.False(store.Observe(new UdpPeerObservation(8124, address, 3074, true, Now)));
        Assert.Empty(store.Snapshot());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Запись_старше_тридцати_секунд_не_считается_живой()
    {
        DateTime clock = Now;
        var store = new RecentUdpPeerStore(clock: () => clock);

        store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 3074, true, Now));
        clock = Now.AddSeconds(31);

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Дубликат_обновляет_время_и_не_раздувает_список()
    {
        DateTime clock = Now;
        var store = new RecentUdpPeerStore(clock: () => clock);

        store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 3074, true, Now));
        clock = Now.AddSeconds(5);
        store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 3074, true, clock));

        TrackedRemotePeer peer = Assert.Single(store.Snapshot());
        Assert.Equal(clock, peer.ObservedUtc);
    }

    [Fact]
    public void Переполнение_выбрасывает_самые_старые_записи()
    {
        DateTime clock = Now;
        var store = new RecentUdpPeerStore(capacity: 3, clock: () => clock);

        for (int i = 1; i <= 5; i++)
        {
            clock = Now.AddSeconds(i);
            store.Observe(new UdpPeerObservation(8124, $"34.16.1.{i}", 3074, true, clock));
        }

        IReadOnlyList<TrackedRemotePeer> peers = store.Snapshot();
        Assert.Equal(3, peers.Count);
        Assert.DoesNotContain(peers, peer => peer.RemoteAddress is "34.16.1.1" or "34.16.1.2");
        Assert.Contains(peers, peer => peer.RemoteAddress == "34.16.1.5");
    }

    [Fact]
    public void Очистка_забывает_все_наблюдения()
    {
        var store = new RecentUdpPeerStore(clock: () => Now);
        store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 3074, true, Now));

        store.Clear();

        Assert.Empty(store.Snapshot());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Собеседник_с_нулевым_портом_не_попадает_в_хранилище()
    {
        var store = new RecentUdpPeerStore(clock: () => Now);

        Assert.False(store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 0, true, Now)));
        Assert.False(store.Observe(new UdpPeerObservation(0, "34.16.1.5", 3074, true, Now)));
    }

    [Fact]
    public void Снимок_не_меняется_от_последующих_наблюдений()
    {
        var store = new RecentUdpPeerStore(clock: () => Now);
        store.Observe(new UdpPeerObservation(8124, "34.16.1.5", 3074, true, Now));

        IReadOnlyList<TrackedRemotePeer> snapshot = store.Snapshot();
        store.Observe(new UdpPeerObservation(8124, "34.16.9.9", 3075, true, Now));

        Assert.Single(snapshot);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public async Task Параллельная_запись_не_ломает_хранилище()
    {
        var store = new RecentUdpPeerStore(clock: () => Now);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                int third = worker + 1;
                store.Observe(new UdpPeerObservation(8124, $"34.16.{third}.{i}", 3074, true, Now));
            }
        })));

        Assert.Equal(400, store.Count);
        Assert.Equal(400, store.Snapshot().Count);
    }

    // ── Сборка полезной нагрузки ровно по шаблону провайдера ────────────────────────────────────

    private static byte[] Ipv4Payload(int pid, string daddr, string saddr, int dport, int sport)
    {
        byte[] payload = new byte[KernelNetworkUdpEventParser.Ipv4PayloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), (uint)pid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 120);
        IPAddress.Parse(daddr).TryWriteBytes(payload.AsSpan(8, 4), out _);
        IPAddress.Parse(saddr).TryWriteBytes(payload.AsSpan(12, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16), (ushort)dport);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(18), (ushort)sport);
        return payload;
    }

    private static byte[] Ipv6Payload(int pid, IPAddress daddr, IPAddress saddr, int dport, int sport)
    {
        byte[] payload = new byte[KernelNetworkUdpEventParser.Ipv6PayloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), (uint)pid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 120);
        daddr.TryWriteBytes(payload.AsSpan(8, 16), out _);
        saddr.TryWriteBytes(payload.AsSpan(24, 16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(40), (ushort)dport);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(42), (ushort)sport);
        return payload;
    }
}
