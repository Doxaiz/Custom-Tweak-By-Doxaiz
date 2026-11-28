using System.Buffers.Binary;
using System.Net;

namespace PingForge.Network;

/// <summary>
/// Разбор событий UDP из провайдера ETW <c>Microsoft-Windows-Kernel-Network</c>.
///
/// Здесь нет ни одного вызова Windows: на вход идут номер события, идентификатор процесса из
/// заголовка и сырая полезная нагрузка события. Поэтому разбор проверяется обычными тестами на
/// сборочном сервере, а тонкая часть (сеанс ETW) остаётся в Windows-проекте.
///
/// Номера событий (манифест провайдера, версия 0):
///   • 42 — UDP IPv4, отправка; 43 — UDP IPv4, получение;
///   • 58 — UDP IPv6, отправка; 59 — UDP IPv6, получение.
/// Соседние 10/11 — это TCP IPv4 (SendIPV4/RecvIPV4), а не UDP: их здесь нарочно нет.
///
/// Полезная нагрузка IPv4 (28 байт): PID(4), size(4), daddr(4), saddr(4), dport(2), sport(2),
/// seqnum(4), connid(4). IPv6 (52 байта): PID(4), size(4), daddr(16), saddr(16), dport(2), sport(2),
/// seqnum(4), connid(4). Адреса лежат в сетевом порядке — читаются как есть, без перестановки байт.
/// Порты — единственное поле, которое читается старшим байтом вперёд: так его читают все известные
/// разборы этого провайдера, и живой замер это подтверждает.
///
/// Ориентация полей — место, где источники честно расходятся:
///   • манифест описывает событие как «из saddr:sport в daddr:dport», то есть при получении
///     собеседник лежит в saddr, а при отправке — в daddr;
///   • живой замер в открытом проекте (observer, событие получения TCP) увидел обратное: публичный
///     адрес оказался в daddr, а собственный адрес машины — в saddr. Это значит, что событие может
///     описывать сокет («свой конец / собеседник»), а не пакет.
///
/// Поэтому здесь берётся предпочтительная по манифесту сторона, а если она не публичная или
/// является адресом самой машины — вторая. Так собственный адрес никогда не станет «сервером игры»,
/// а событие всё равно принесёт собеседника. Какие адреса «свои», знает вызывающий и передаёт
/// готовую проверку: у разбора нет права перечислять интерфейсы и решать это самому.
/// </summary>
public static class KernelNetworkUdpEventParser
{
    /// <summary>UDP IPv4, отправка.</summary>
    public const ushort UdpSendIpv4EventId = 42;

    /// <summary>UDP IPv4, получение.</summary>
    public const ushort UdpRecvIpv4EventId = 43;

    /// <summary>UDP IPv6, отправка.</summary>
    public const ushort UdpSendIpv6EventId = 58;

    /// <summary>UDP IPv6, получение.</summary>
    public const ushort UdpRecvIpv6EventId = 59;

    // Соседние события TCP: существуют, но к UDP-собеседникам отношения не имеют.
    public const ushort TcpSendIpv4EventId = 10;
    public const ushort TcpRecvIpv4EventId = 11;

    /// <summary>Минимальная длина полезной нагрузки IPv4-события UDP.</summary>
    public const int Ipv4PayloadBytes = 28;

    /// <summary>Минимальная длина полезной нагрузки IPv6-события UDP.</summary>
    public const int Ipv6PayloadBytes = 52;

    /// <summary>Версия события, которую разбирает этот код. Другая версия — отказ, а не догадка.</summary>
    public const byte SupportedEventVersion = 0;

    // Смещения полей внутри полезной нагрузки.
    private const int PidOffset = 0;
    private const int Ipv4DestinationOffset = 8;
    private const int Ipv4SourceOffset = 12;
    private const int Ipv4DestinationPortOffset = 16;
    private const int Ipv4SourcePortOffset = 18;
    private const int Ipv6DestinationOffset = 8;
    private const int Ipv6SourceOffset = 24;
    private const int Ipv6DestinationPortOffset = 40;
    private const int Ipv6SourcePortOffset = 42;

    /// <summary>Это событие UDP (а не TCP и не что-то ещё)?</summary>
    public static bool IsUdpEvent(ushort eventId) =>
        eventId is UdpSendIpv4EventId or UdpRecvIpv4EventId or UdpSendIpv6EventId or UdpRecvIpv6EventId;

    /// <summary>Направление события: true — отправка, false — получение. Для не-UDP событий — null.</summary>
    public static bool? IsSend(ushort eventId) => eventId switch
    {
        UdpSendIpv4EventId or UdpSendIpv6EventId => true,
        UdpRecvIpv4EventId or UdpRecvIpv6EventId => false,
        _ => null,
    };

    /// <summary>
    /// Прочитать наблюдение из сырой полезной нагрузки. false — это не UDP-событие, версия события
    /// не та, нагрузка короче ожидаемой, ни одна из сторон не публичная или порт/процесс не
    /// определены. Исключений наружу не бывает: кривое событие из ядра не должно ронять службу.
    /// </summary>
    /// <param name="payload">Полезная нагрузка события ETW (EVENT_RECORD.UserData).</param>
    /// <param name="eventId">Номер события из EVENT_HEADER.EventDescriptor.Id.</param>
    /// <param name="eventVersion">Версия события из EVENT_HEADER.EventDescriptor.Version.</param>
    /// <param name="headerProcessId">PID из заголовка события — запасной, если в нагрузке ноль.</param>
    /// <param name="observedUtc">Время наблюдения.</param>
    /// <param name="observation">Разобранное наблюдение или null.</param>
    /// <param name="isOwnAddress">
    /// Проверка «это адрес самой машины». Необязательная: без неё разбор отбрасывает только
    /// непубличные адреса, а собственный публичный адрес отличить не может.
    /// </param>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        ushort eventId,
        byte eventVersion,
        int headerProcessId,
        DateTime observedUtc,
        out UdpPeerObservation? observation,
        Func<string, bool>? isOwnAddress = null)
    {
        observation = null;

        bool? send = IsSend(eventId);
        if (send is null || eventVersion != SupportedEventVersion)
        {
            return false;
        }

        bool ipv6 = eventId is UdpSendIpv6EventId or UdpRecvIpv6EventId;
        int needed = ipv6 ? Ipv6PayloadBytes : Ipv4PayloadBytes;
        if (payload.Length < needed)
        {
            return false;
        }

        int processId = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[PidOffset..]);
        if (processId <= 0)
        {
            processId = headerProcessId;
        }

        if (processId <= 0)
        {
            return false;
        }

        int addressLength = ipv6 ? 16 : 4;
        bool hasDestination = TryReadPeer(
            payload,
            ipv6 ? Ipv6DestinationOffset : Ipv4DestinationOffset,
            addressLength,
            ipv6 ? Ipv6DestinationPortOffset : Ipv4DestinationPortOffset,
            out string destinationAddress,
            out int destinationPort);

        bool hasSource = TryReadPeer(
            payload,
            ipv6 ? Ipv6SourceOffset : Ipv4SourceOffset,
            addressLength,
            ipv6 ? Ipv6SourcePortOffset : Ipv4SourcePortOffset,
            out string sourceAddress,
            out int sourcePort);

        // По манифесту: отправка — собеседник в daddr, получение — в saddr. Вторая сторона —
        // запасной вариант, если предпочтительная оказалась своим или непубличным адресом.
        string preferredAddress;
        int preferredPort;
        bool preferredOk;
        if (send.Value)
        {
            preferredAddress = destinationAddress;
            preferredPort = destinationPort;
            preferredOk = hasDestination;
        }
        else
        {
            preferredAddress = sourceAddress;
            preferredPort = sourcePort;
            preferredOk = hasSource;
        }

        string alternateAddress = send.Value ? sourceAddress : destinationAddress;
        int alternatePort = send.Value ? sourcePort : destinationPort;
        bool alternateOk = send.Value ? hasSource : hasDestination;

        string? remoteAddress = null;
        int remotePort = 0;

        if (preferredOk && IsUsablePeer(preferredAddress, preferredPort, isOwnAddress))
        {
            remoteAddress = preferredAddress;
            remotePort = preferredPort;
        }
        else if (alternateOk && IsUsablePeer(alternateAddress, alternatePort, isOwnAddress))
        {
            remoteAddress = alternateAddress;
            remotePort = alternatePort;
        }

        if (remoteAddress is null)
        {
            return false;
        }

        observation = new UdpPeerObservation(processId, remoteAddress, remotePort, send.Value, observedUtc);
        return true;
    }

    /// <summary>Прочитать одну сторону события: адрес и порт. Поля всегда записываются.</summary>
    private static bool TryReadPeer(
        ReadOnlySpan<byte> payload,
        int addressOffset,
        int addressLength,
        int portOffset,
        out string address,
        out int port)
    {
        // Порт в событии — в сетевом порядке: старший байт первый.
        port = BinaryPrimitives.ReadUInt16BigEndian(payload[portOffset..]);
        address = string.Empty;

        if (port <= 0)
        {
            return false;
        }

        try
        {
            address = new IPAddress(payload.Slice(addressOffset, addressLength)).ToString();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Годится ли сторона в собеседники: публичный адрес, живой порт и это не адрес самой машины.
    /// Приватные и служебные адреса отсекаются здесь же, чтобы служба не завела «сервером» роутер.
    /// </summary>
    private static bool IsUsablePeer(string address, int port, Func<string, bool>? isOwnAddress) =>
        port is > 0 and <= 65535
        && GameFlowDetector.IsPublicRemoteAddress(address)
        && (isOwnAddress is null || !isOwnAddress(address));
}
