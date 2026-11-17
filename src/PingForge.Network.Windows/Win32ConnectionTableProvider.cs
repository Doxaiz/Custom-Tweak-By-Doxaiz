using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PingForge.Network;

namespace PingForge.Network.Windows;

/// <summary>
/// Таблица соединений через iphlpapi: <c>GetExtendedTcpTable</c> и <c>GetExtendedUdpTable</c> с флагом
/// OWNER_PID. Это единственный честный способ узнать, какой процесс держит соединение: не «угадать по
/// порту», а спросить у системы.
///
/// Права: чтение таблицы доступно обычному пользователю (администратор нужен не здесь, а для изменения
/// маршрута и политик). Если вызов не удался, метод возвращает ошибку словами и НЕ выдумывает строки —
/// «соединений нет» и «прочитать не смогли» — разные новости для пользователя.
///
/// Никакого перехвата трафика здесь нет: мы только читаем то, что и так показывает «Монитор ресурсов».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ConnectionTableProvider : IRawConnectionTableProvider
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int NoError = 0;

    private readonly bool _includeIpv6;

    public Win32ConnectionTableProvider(bool includeIpv6 = false) => _includeIpv6 = includeIpv6;

    /// <summary>Последняя ошибка чтения словами — её показывает интерфейс вместо пустого списка.</summary>
    public string? LastErrorRu { get; private set; }

    public Task<IReadOnlyList<RawConnectionRow>> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var rows = new List<RawConnectionRow>();

        if (!TryReadTcp(AfInet, rows, out string? tcpV4Error))
        {
            LastErrorRu = tcpV4Error;
            return Task.FromResult<IReadOnlyList<RawConnectionRow>>(rows);
        }

        TryReadUdp(AfInet, rows, out string? udpV4Error);
        LastErrorRu = udpV4Error;

        if (_includeIpv6)
        {
            // IPv6 читаем только когда попросили. Служба просит: сервер игры бывает IPv6-ным
            // (Minecraft Bedrock, IPv6-only серверы), и человеку нужно видеть его в отчёте, а не пустой
            // список. Маршрут по IPv6 продукт не ставит — детектор такие строки считает и объясняет.
            // Ошибка IPv6 не считается ошибкой чтения IPv4.
            TryReadTcp(AfInet6, rows, out _);
            TryReadUdp(AfInet6, rows, out _);
        }

        return Task.FromResult<IReadOnlyList<RawConnectionRow>>(rows);
    }

    private bool TryReadTcp(int family, List<RawConnectionRow> rows, out string? errorRu)
    {
        errorRu = null;

        if (!TryFetch(
                (IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0),
                out IntPtr table,
                out int tableSize,
                out string? failure))
        {
            errorRu = failure;
            return false;
        }

        try
        {
            int count = Marshal.ReadInt32(table);

            // dwNumEntries (4 байта) + массив MIB_TCPROW_OWNER_PID по 24 байта, начиная с 64-битной выравненной границы.
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr cursor = IntPtr.Add(table, 4);

            for (int i = 0; i < count; i++)
            {
                MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                cursor = IntPtr.Add(cursor, rowSize);

                rows.Add(new RawConnectionRow(
                    ConnectionProtocol.Tcp,
                    (int)row.OwningPid,
                    NativeTableMapping.Ipv4FromNetworkOrder(row.LocalAddr),
                    NativeTableMapping.PortFromNetworkOrder(row.LocalPort),
                    NativeTableMapping.Ipv4FromNetworkOrder(row.RemoteAddr),
                    NativeTableMapping.PortFromNetworkOrder(row.RemotePort),
                    NativeTableMapping.TcpStateFromCode((int)row.State)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return true;

        static bool TryFetch(FetchFunc fetch, out IntPtr table, out int tableSize, out string? failure)
        {
            table = IntPtr.Zero;
            tableSize = 0;
            failure = null;

            uint result = fetch(IntPtr.Zero, ref tableSize);
            if (result != ErrorInsufficientBuffer && result != NoError)
            {
                failure = $"Не удалось прочитать таблицу соединений: {new Win32Exception((int)result).Message}.";
                return false;
            }

            table = Marshal.AllocHGlobal(tableSize);
            result = fetch(table, ref tableSize);

            if (result != NoError)
            {
                Marshal.FreeHGlobal(table);
                table = IntPtr.Zero;
                failure = $"Не удалось прочитать таблицу соединений: {new Win32Exception((int)result).Message}.";
                return false;
            }

            return true;
        }
    }

    private bool TryReadUdp(int family, List<RawConnectionRow> rows, out string? errorRu)
    {
        errorRu = null;

        int size = 0;
        uint probe = GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpTableOwnerPid, 0);

        if (probe != ErrorInsufficientBuffer && probe != NoError)
        {
            errorRu = $"Не удалось прочитать таблицу UDP-соединений: {new Win32Exception((int)probe).Message}.";
            return false;
        }

        IntPtr table = Marshal.AllocHGlobal(size);

        try
        {
            uint result = GetExtendedUdpTable(table, ref size, false, family, UdpTableOwnerPid, 0);

            if (result != NoError)
            {
                errorRu = $"Не удалось прочитать таблицу UDP-соединений: {new Win32Exception((int)result).Message}.";
                return false;
            }

            int count = Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            IntPtr cursor = IntPtr.Add(table, 4);

            for (int i = 0; i < count; i++)
            {
                MibUdpRowOwnerPid row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(cursor);
                cursor = IntPtr.Add(cursor, rowSize);

                // У UDP нет удалённой стороны: известен только локальный сокет. Это не «потеря данных»,
                // а свойство протокола — удалённый адрес для UDP узнаётся из живого потока (см. замеры).
                rows.Add(new RawConnectionRow(
                    ConnectionProtocol.Udp,
                    (int)row.OwningPid,
                    NativeTableMapping.Ipv4FromNetworkOrder(row.LocalAddr),
                    NativeTableMapping.PortFromNetworkOrder(row.LocalPort),
                    "0.0.0.0",
                    0,
                    string.Empty));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return true;
    }

    private delegate uint FetchFunc(IntPtr buffer, ref int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
