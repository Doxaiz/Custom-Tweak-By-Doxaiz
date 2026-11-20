using System.Net;
using System.Runtime.InteropServices;
using PingForge.Probing;

namespace PingForge.Network;

/// <summary>
/// Статистика уже открытого TCP-соединения через iphlpapi: задержка берётся у сетевого стека,
/// ни одного пакета не отправляется.
///
/// Как это работает (и почему именно так):
///   1. <c>GetExtendedTcpTable</c> перечисляет TCP-соединения с номерами процессов;
///   2. для нужного соединения включается сбор статистики пути — <c>SetPerTcpConnectionEStats</c>
///      с типом <c>TcpConnectionEstatsPath</c>. Windows разрешает это только членам группы
///      администраторов, а служба работает под системной учётной записью, поэтому у неё права есть;
///   3. читается снимок <c>TCP_ESTATS_PATH_ROD_v0</c>: сглаженная задержка, её разброс, число
///      измерений и число повторных пакетов;
///   4. если в прочитанной структуре <c>EnableCollection</c> не TRUE, снимок не используется:
///      Windows прямо пишет, что тогда значения не определены.
///
/// Числа стека — микросекунды; перевод в миллисекунды в <see cref="TcpEstatsMath"/>, чтобы его
/// можно было проверить тестом без Windows.
///
/// Чего здесь нет и почему: потерь и джиттера «средним отклонением» по отдельным замерам. Стек
/// отдаёт сглаженный разброс RTT (RttVar), а не выборку замеров, поэтому медиану и MAD взять неоткуда;
/// провайдер сообщает об этом текстом, а не подставляет похожие числа.
/// </summary>
public sealed class WindowsTcpEstatsReader : ITcpConnectionStatsReader
{
    // TCP_ESTATS_TYPE.TcpConnectionEstatsPath — порядок значений задан в tcpestats.h и меняться не может.
    private const int TcpConnectionEstatsPath = 3;

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint MibTcpStateEstablished = 5;

    // Соединения, на которых сбор уже включён. Повторный Set сбрасывает накопленные замеры, поэтому
    // включаем один раз и дальше только читаем — иначе «0 измерений» окажется нашей же виной.
    private readonly HashSet<string> _collectionEnabled = [];

    public string ProblemRu { get; private set; } = string.Empty;

    public IReadOnlyList<LiveTcpConnectionStats> ReadEstablishedConnections(int processId)
    {
        ProblemRu = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            ProblemRu = "Статистика TCP-соединений читается только на Windows.";
            return [];
        }

        List<MibTcpRowOwnerPid> rows = ReadConnectionTable();
        if (rows.Count == 0)
        {
            // Причина уже названа в ProblemRu: пустой список и «нет соединений» — разные вещи.
            return [];
        }

        var result = new List<LiveTcpConnectionStats>();
        string? lastProblem = null;
        int established = 0;

        foreach (MibTcpRowOwnerPid row in rows)
        {
            if (row.State != MibTcpStateEstablished)
            {
                continue;
            }

            if (processId > 0 && row.OwningPid != (uint)processId)
            {
                continue;
            }

            established++;

            var native = new MibTcpRow
            {
                State = row.State,
                LocalAddr = row.LocalAddr,
                LocalPort = row.LocalPort,
                RemoteAddr = row.RemoteAddr,
                RemotePort = row.RemotePort,
            };

            if (!TryReadPathStats(ref native, out TcpEstatsPathRod rod, out string? problem))
            {
                // Одно соединение без статистики не отменяет остальные: причину запоминаем.
                lastProblem ??= problem;
                continue;
            }

            result.Add(new LiveTcpConnectionStats(
                new IPAddress(row.RemoteAddr).ToString(),
                NetworkPort(row.RemotePort),
                TcpEstatsMath.MicrosecondsToMilliseconds(rod.SmoothedRtt),
                TcpEstatsMath.MicrosecondsToMilliseconds(rod.RttVar),
                (int)Math.Min(rod.CountRtt, (uint)int.MaxValue),
                (int)Math.Min(rod.PktsRetrans, (uint)int.MaxValue)));
        }

        if (result.Count == 0)
        {
            ProblemRu = lastProblem
                ?? (established == 0
                    ? "У этого процесса нет установленных TCP-соединений."
                    : "Установленные TCP-соединения найдены, но статистику по ним прочитать не удалось.");
        }

        return result;
    }

    private List<MibTcpRowOwnerPid> ReadConnectionTable()
    {
        int size = 0;
        uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (status != ErrorInsufficientBuffer && status != NoError)
        {
            ProblemRu = $"Список TCP-соединений не прочитан: система вернула код {status}.";
            return [];
        }

        if (size <= 0)
        {
            ProblemRu = "Система вернула пустой список TCP-соединений.";
            return [];
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (status != NoError)
            {
                ProblemRu = $"Список TCP-соединений не прочитан: система вернула код {status}.";
                return [];
            }

            // Таблица — число записей (DWORD) и сразу за ним сами записи; выравнивание 4 байта.
            int count = Marshal.ReadInt32(buffer);
            if (count <= 0)
            {
                return [];
            }

            const int rowSize = 6 * 4;
            int header = 4;
            IntPtr first = IntPtr.Add(buffer, header);

            var rows = new List<MibTcpRowOwnerPid>(count);
            for (int i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(first, i * rowSize)));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadPathStats(ref MibTcpRow row, out TcpEstatsPathRod rod, out string? problem)
    {
        rod = default;
        problem = null;

        // Сбор включаем сами (без этого Windows прямо говорит, что значения не определены), но только
        // один раз на соединение: повторный Set обнуляет накопленные замеры.
        string key = $"{row.LocalAddr}:{row.LocalPort}-{row.RemoteAddr}:{row.RemotePort}";
        uint status = NoError;

        if (!_collectionEnabled.Contains(key))
        {
            var enable = new TcpEstatsPathRw { EnableCollection = 1 };
            status = SetPerTcpConnectionEStats(
                ref row, TcpConnectionEstatsPath,
                ref enable, 0, (uint)Marshal.SizeOf<TcpEstatsPathRw>(),
                IntPtr.Zero, 0, 0,
                IntPtr.Zero, 0, 0);

            if (status != NoError)
            {
                problem = status == 5
                    ? "Сбор статистики TCP-соединения запрещён: нужны права администратора."
                    : $"Включить сбор статистики TCP-соединения не удалось: система вернула код {status}.";
                return false;
            }

            _collectionEnabled.Add(key);
        }

        int rwBytes = Marshal.SizeOf<TcpEstatsPathRw>();
        int rodBytes = Marshal.SizeOf<TcpEstatsPathRod>();
        IntPtr rwBuffer = Marshal.AllocHGlobal(rwBytes);
        IntPtr rodBuffer = Marshal.AllocHGlobal(rodBytes);
        byte enabled;
        TcpEstatsPathRod snapshot;

        try
        {
            Marshal.WriteByte(rwBuffer, 0, 0);

            status = GetPerTcpConnectionEStatsRaw(
                ref row, TcpConnectionEstatsPath,
                rwBuffer, 0, (uint)rwBytes,
                IntPtr.Zero, 0, 0,
                rodBuffer, 0, (uint)rodBytes);

            enabled = status == NoError ? Marshal.ReadByte(rwBuffer) : (byte)0;
            snapshot = status == NoError ? Marshal.PtrToStructure<TcpEstatsPathRod>(rodBuffer) : default;
        }
        finally
        {
            Marshal.FreeHGlobal(rwBuffer);
            Marshal.FreeHGlobal(rodBuffer);
        }

        if (status != NoError)
        {
            problem = DescribeReadFailure(status);
            return false;
        }

        if (enabled == 0)
        {
            problem = "Сбор статистики TCP-соединения не включён: числа не определены.";
            return false;
        }

        rod = snapshot;
        return true;
    }

    /// <summary>Отказ чтения называется по коду: «не нашёл соединение» и «не принял буфер» — разные вещи.</summary>
    private static string DescribeReadFailure(uint status) => status switch
    {
        1168 => "Статистика TCP-соединения не прочитана: система не нашла это соединение (код 1168) — оно могло закрыться.",
        1784 => "Статистика TCP-соединения не прочитана: система не приняла буфер (код 1784).",
        5 => "Статистика TCP-соединения не прочитана: нет прав администратора (код 5).",
        _ => $"Статистика TCP-соединения не прочитана: система вернула код {status}."
    };

    /// <summary>Порт в структурах iphlpapi лежит в младших 16 битах и в сетевом порядке.</summary>
    private static int NetworkPort(uint port) => (int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

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

    /// <summary>TCP_ESTATS_PATH_RW_v0 — единственное поле: включить сбор.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsPathRw
    {
        public byte EnableCollection;
    }

    /// <summary>
    /// TCP_ESTATS_PATH_ROD_v0 из tcpestats.h. Порядок полей менять нельзя: структуру заполняет система,
    /// и сдвиг поля означает чтение чужого числа. Нужны последние по счёту поля: задержка и её разброс,
    /// число измерений и повторные пакеты.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsPathRod
    {
        public uint FastRetran;
        public uint Timeouts;
        public uint SubsequentTimeouts;
        public uint CurTimeoutCount;
        public uint AbruptTimeouts;
        public uint PktsRetrans;
        public uint BytesRetrans;
        public uint DupAcksIn;
        public uint SacksRcvd;
        public uint SackBlocksRcvd;
        public uint CongSignals;
        public uint PreCongSumCwnd;
        public uint PreCongSumRtt;
        public uint PostCongSumRtt;
        public uint PostCongCountRtt;
        public uint EcnSignals;
        public uint EceRcvd;
        public uint SendStall;
        public uint QuenchRcvd;
        public uint RetranThresh;
        public uint SndDupAckEpisodes;
        public uint SumBytesReordered;
        public uint NonRecovDa;
        public uint NonRecovDaEpisodes;
        public uint AckAfterFr;
        public uint DsackDups;
        public uint SampleRtt;
        public uint SmoothedRtt;
        public uint RttVar;
        public uint MaxRtt;
        public uint MinRtt;
        public uint SumRtt;
        public uint CountRtt;
        public uint CurRto;
        public uint MaxRto;
        public uint MinRto;
        public uint CurMss;
        public uint MaxMss;
        public uint MinMss;
        public uint SpuriousRtoDetections;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true, EntryPoint = "SetPerTcpConnectionEStats")]
    private static extern uint SetPerTcpConnectionEStats(
        ref MibTcpRow row, int estatsType,
        ref TcpEstatsPathRw rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    // Размеры буферов у этой функции передаются значением (ULONG), а не указателем: если передать
    // указатель, iphlpapi увидит в нём длину в миллиарды байт и ответит «Invalid user buffer» (1784).
    [DllImport("iphlpapi.dll", SetLastError = true, EntryPoint = "GetPerTcpConnectionEStats")]
    private static extern uint GetPerTcpConnectionEStatsRaw(
        ref MibTcpRow row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);
}
