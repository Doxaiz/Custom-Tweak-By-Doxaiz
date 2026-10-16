using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PingForge.Network;

/// <summary>
/// Перевод значений из структур Windows (iphlpapi) в понятные строки. Вынесено в общий проект
/// намеренно: сами структуры можно прочитать только на Windows, а вот арифметику — порт в сетевом
/// порядке, разбор состояния TCP — надо проверять тестами на любой машине. Ошибка в этих двух
/// преобразованиях даёт «игровой сервер 0.0.0.0:0», и найти её без тестов почти невозможно.
/// </summary>
public static class NativeTableMapping
{
    /// <summary>Порт в структурах Windows лежит в сетевом порядке в младших 16 битах.</summary>
    public static int PortFromNetworkOrder(uint rawPort) =>
        (ushort)IPAddress.NetworkToHostOrder((short)(rawPort & 0xFFFF));

    /// <summary>
    /// Адрес IPv4 из структуры Windows. В полях iphlpapi адрес лежит в сетевом порядке (для 34.16.1.5 это
    /// 0x22100105), а конструктор <c>IPAddress(uint)</c> читает число как записанное в памяти —
    /// то есть с обратным порядком байт. Поэтому порядок байт меняем явно. Это проверено тестом:
    /// <c>Ipv4FromNetworkOrder_ReadsAddressAsIs</c>. Если на живой машине адреса окажутся зеркальными,
    /// правится ровно одна строка — вот эта.
    /// </summary>
    public static string Ipv4FromNetworkOrder(uint rawAddress) =>
        new IPAddress(BinaryPrimitives.ReverseEndianness(rawAddress)).ToString();

    /// <summary>Коды MIB_TCP_STATE. Незнакомый код не выдумываем, а помечаем честно.</summary>
    public static string TcpStateFromCode(int code) => code switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => $"Unknown({code})"
    };

    /// <summary>Строка TCP идёт в трафик игры только в состоянии Established: слушающий сокет — не поток.</summary>
    public static bool IsStreamCandidate(string tcpState) =>
        tcpState.Equals("Established", StringComparison.OrdinalIgnoreCase);

    public static string DescribeProtocol(ConnectionProtocol protocol) =>
        protocol == ConnectionProtocol.Udp ? "UDP" : "TCP";

    public static ConnectionProtocol ProtocolFromTableKind(bool isUdp) =>
        isUdp ? ConnectionProtocol.Udp : ConnectionProtocol.Tcp;

    /// <summary>Адрес и порт одним текстом — то, что показывается пользователю.</summary>
    public static string Endpoint(string address, int port, AddressFamily family) =>
        family == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
}
