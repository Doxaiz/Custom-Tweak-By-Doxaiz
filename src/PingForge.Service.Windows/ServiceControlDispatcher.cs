using System.Runtime.InteropServices;

namespace PingForge.Service.Windows;

/// <summary>
/// Запуск как настоящая служба Windows: диспетчер служб, обработчик «стоп», честный статус.
///
/// Пакет <c>System.ServiceProcess.ServiceController</c> для этого не нужен: вызовов всего четыре,
/// и они объявлены прямо здесь — так видно, что именно служба говорит системе о себе.
/// Поведение проверяется только на Windows: на сборочной машине этот код лишь компилируется.
/// </summary>
public static class ServiceControlDispatcher
{
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceStateStopped = 0x00000001;
    private const int ServiceStateStartPending = 0x00000002;
    private const int ServiceStateStopPending = 0x00000003;
    private const int ServiceStateRunning = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;
    private const int NoError = 0;

    private static readonly ServiceMainDelegate ServiceMainProc = OnServiceMain;
    private static readonly ServiceControlHandlerDelegate ControlHandler = OnControl;

    private static string _serviceName = "PingForge";
    private static Func<CancellationToken, Task>? _run;
    private static Action<string> _log = _ => { };
    private static IntPtr _statusHandle = IntPtr.Zero;
    private static CancellationTokenSource? _stop;
    private static Task? _work;
    private static int _currentState = ServiceStateStopped;

    /// <summary>
    /// Запуск от диспетчера служб. Возвращает <c>false</c>, если процесс стартовали руками:
    /// тогда вызывающий решает сам — работать как обычный процесс (так удобно при отладке) или выйти.
    /// </summary>
    public static bool TryRunAsService(string serviceName, Func<CancellationToken, Task> run, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(log);

        _serviceName = serviceName;
        _run = run;
        _log = log;

        var table = new ServiceTableEntry[2];
        table[0] = new ServiceTableEntry { ServiceName = serviceName, ServiceMain = ServiceMainProc };
        table[1] = new ServiceTableEntry { ServiceName = null, ServiceMain = null };

        if (!StartServiceCtrlDispatcher(table))
        {
            int error = Marshal.GetLastWin32Error();
            log($"Процесс запущен не диспетчером служб (код {error}): продолжаю как обычный процесс.");
            return false;
        }

        return true;
    }

    private static void OnServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, ControlHandler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            _log($"Не удалось зарегистрировать обработчик службы (код {Marshal.GetLastWin32Error()}).");
            return;
        }

        ReportStatus(ServiceStateStartPending, waitHintMs: 3000);
        _stop = new CancellationTokenSource();

        try
        {
            _log($"Служба «{_serviceName}» запускается.");
            ReportStatus(ServiceStateRunning, waitHintMs: 0);
            _work = _run!(_stop.Token);
            _work.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _log("Служба остановлена по команде.");
        }
        catch (Exception ex)
        {
            _log($"Служба остановилась из-за ошибки: {ex.Message}");
        }
        finally
        {
            ReportStatus(ServiceStateStopped, waitHintMs: 0);
            _stop?.Dispose();
            _stop = null;
            _log("Служба остановлена.");
        }
    }

    private static int OnControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            ReportStatus(ServiceStateStopPending, waitHintMs: 5000);
            _stop?.Cancel();
            return NoError;
        }

        // Остальные управляющие коды (пауза, продолжение) служба не поддерживает: возвращаем отказ,
        // чтобы диспетчер служб не считал, что команда принята.
        return 1;
    }

    private static void ReportStatus(int state, int waitHintMs)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        _currentState = state;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == ServiceStateRunning ? ServiceAcceptStop | ServiceAcceptShutdown : 0,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            CheckPoint = state is ServiceStateStartPending or ServiceStateStopPending ? 1 : 0,
            WaitHint = waitHintMs,
        };

        SetServiceStatus(_statusHandle, ref status);
    }

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    private delegate int ServiceControlHandlerDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;

        public ServiceMainDelegate? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceStartTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        ServiceControlHandlerDelegate handler,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);
}
