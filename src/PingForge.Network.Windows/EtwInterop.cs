using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PingForge.Network.Windows;

/// <summary>
/// Объявления Windows API для чтения ETW. Здесь только типы и вызовы — никакой логики продукта.
///
/// Почему структуры описаны вручную, а не через пакет: у продукта нет сторонних зависимостей, а
/// раскладка этих структур зафиксирована Windows SDK. Поля, которые мы не читаем, всё равно обязаны
/// стоять на своих местах: ETW заполняет структуру по своему разумению, и сдвиг на байт превратил бы
/// разбор в мусор. Раскладка сверена с объявлениями Microsoft (TraceEvent/Tx) для x64.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class EtwInterop
{
    /// <summary>Сеанс ETW в реальном времени (без файла .etl).</summary>
    public const uint EventTraceRealTimeMode = 0x00000100;

    /// <summary>Присылать события современным EVENT_RECORD, а не старым EVENT_TRACE.</summary>
    public const uint ProcessTraceModeEventRecord = 0x10000000;

    /// <summary>Wnode.Flags: сеанс описан GUID провайдера.</summary>
    public const uint WnodeFlagTracedGuid = 0x00020000;

    public const uint EventControlCodeEnableProvider = 1;
    public const uint EventControlCodeStop = 1;

    public const int ErrorSuccess = 0;
    public const int ErrorAlreadyExists = 183;

    /// <summary>Недействительный описатель разбора: 32- и 64-битная запись INVALID_HANDLE_VALUE.</summary>
    public const ulong InvalidProcessTraceHandle = 0x00000000FFFFFFFF;

    /// <summary>Описатель не годится: 0, 32-битная или 64-битная запись «нет описателя».</summary>
    public static bool IsInvalidTraceHandle(ulong handle) =>
        handle is 0 or InvalidProcessTraceHandle or ulong.MaxValue;

    /// <summary>Уровень «подробно»: включает и информационные события провайдера.</summary>
    public const byte TraceLevelVerbose = 5;

    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", EntryPoint = "EnableTraceEx2", SetLastError = true)]
    public static extern int EnableTraceEx2(
        ulong traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ulong OpenTrace(ref EventTraceLogFile logFile);

    [DllImport("advapi32.dll", EntryPoint = "ProcessTrace", SetLastError = true)]
    public static extern int ProcessTrace(ulong[] handles, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", EntryPoint = "CloseTrace", SetLastError = true)]
    public static extern int CloseTrace(ulong traceHandle);

    [StructLayout(LayoutKind.Sequential)]
    public struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    /// <summary>EVENT_TRACE_PROPERTIES; имя сеанса дописывается сразу за структурой.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint KernelTime;
        public uint UserTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TimeZoneInformation
    {
        public int Bias;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? StandardName;

        public SystemTime StandardDate;
        public int StandardBias;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DaylightName;

        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TraceLogFileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public uint StartBuffers;
        public uint PointerSize;
        public uint EventsLost;
        public uint CpuSpeedInMHz;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    public delegate void EventRecordCallback(ref EventRecord eventRecord);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct EventTraceLogFile
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LogFileName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LoggerName;

        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogFileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;

        [MarshalAs(UnmanagedType.FunctionPtr)]
        public EventRecordCallback? EventRecordCallback;

        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventDescriptor
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EventDescriptor EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EtwBufferContext
    {
        public ushort ProcessorIndex;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventRecord
    {
        public EventHeader EventHeader;
        public EtwBufferContext BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }
}
