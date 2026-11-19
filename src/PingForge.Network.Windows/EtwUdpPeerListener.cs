using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PingForge.Network;

namespace PingForge.Network.Windows;

/// <summary>
/// Наблюдение UDP-собеседников через ETW: сеанс <c>PingForge.UdpPeers</c>, провайдер
/// <c>Microsoft-Windows-Kernel-Network</c>. Только чтение: программа не перехватывает трафик,
/// не поднимает драйвер, не трогает память игры и не меняет ни одной настройки.
///
/// Зачем это нужно: таблица соединений UDP знает только локальный сокет — с кем говорит игра,
/// в ней не написано. Без наблюдателя детектор видит у UDP-игры «0.0.0.0:0» и честно говорит
/// «сервер не найден». Наблюдатель даёт адрес и порт собеседника, а дальше работает обычный детектор.
///
/// Границы, зашитые в код:
///   • любая ошибка ETW — это строка в журнале службы и nothing more: наблюдение выключается,
///     служба работает дальше (без него UDP-детектор просто остаётся слепым, как раньше);
///   • события читаются только по номерам UDP (42/43/58/59), TCP сюда не попадает;
///   • собственные адреса машины отбрасываются отдельно: источники расходятся в том, где в
///     событии лежит собеседник, и «сервером» не должен стать собственный интерфейс;
///   • приватные и служебные адреса отбрасываются хранилищем, а не «на глаз» в интерфейсе;
///   • остановка сеанса — в Dispose, в том числе когда служба остановлена диспетчером.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EtwUdpPeerListener : IRemotePeerTracker, IDisposable
{
    /// <summary>Имя сеанса. Оно же видно в <c>logman query -ets</c> — по нему проверяют живой прогон.</summary>
    public const string SessionName = "PingForge.UdpPeers";

    /// <summary>Провайдер Microsoft-Windows-Kernel-Network.</summary>
    public static readonly Guid KernelNetworkProvider = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    // Ключевые слова «IPv4» и «IPv6» провайдера: они покрывают и TCP, и UDP, поэтому номера
    // событий проверяются повторно в разборе.
    private const ulong KeywordIpv4 = 0x10;
    private const ulong KeywordIpv6 = 0x20;

    private readonly Action<string> _log;
    private readonly RecentUdpPeerStore _store;
    private readonly Action<UdpPeerObservation>? _onObservation;
    private readonly object _sync = new();
    private readonly byte[] _payload = new byte[128];

    private EtwInterop.EventRecordCallback? _callback;
    private Thread? _thread;
    private ulong _traceHandle;
    private bool _sessionStarted;
    private bool _disposed;

    // Свои адреса пересчитываются не чаще раза в минуту: перечисление интерфейсов на каждое
    // событие было бы дороже самого наблюдения, а адреса меняются куда реже.
    private HashSet<string>? _ownAddresses;
    private DateTime _ownAddressesUtc;

    /// <summary>
    /// Хранилище можно передать снаружи (например, чтобы прочитать то же самое в тестах или
    /// самопроверке). По умолчанию — свои 30 секунд и 4096 записей.
    /// <paramref name="onObservation"/> — дополнительный получатель каждого наблюдения: по нему служба
    /// считает пассивную задержку живого UDP-потока, не отправляя ни одного пакета.
    /// </summary>
    public EtwUdpPeerListener(
        Action<string>? log = null,
        RecentUdpPeerStore? store = null,
        Action<UdpPeerObservation>? onObservation = null)
    {
        _log = log ?? (_ => { });
        _store = store ?? new RecentUdpPeerStore();
        _onObservation = onObservation;
    }

    /// <summary>Идёт ли наблюдение прямо сейчас. Не «обещание», а факт: сеанс создан и поток читает.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _sessionStarted && _thread is { IsAlive: true };
            }
        }
    }

    /// <summary>Почему наблюдение не пошло (null — пошло или ещё не пробовали).</summary>
    public string? ProblemRu { get; private set; }

    /// <summary>
    /// Запустить сеанс. Ошибки не бросаются: служба обязана работать и без наблюдения — тогда
    /// UDP-игры просто не находятся, и это честно написано в журнале.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _sessionStarted)
            {
                return;
            }

            try
            {
                if (!TryStartSession())
                {
                    return;
                }

                _sessionStarted = true;
                _callback = OnEventRecord;
                _thread = new Thread(ProcessLoop)
                {
                    IsBackground = true,
                    Name = SessionName,
                };
                _thread.Start();
                _log($"UDP-собеседники: сеанс «{SessionName}» открыт (провайдер Microsoft-Windows-Kernel-Network, только чтение).");
            }
            catch (Exception ex)
            {
                ProblemRu = $"наблюдение UDP не запустилось: {ex.GetType().Name}: {ex.Message}";
                _log($"UDP-собеседники: {ProblemRu}. Служба работает дальше без наблюдателя.");
                StopSessionUnsafe();
            }
        }
    }

    /// <summary>Живые UDP-собеседники. Никогда не бросает: нет наблюдения — пустой список.</summary>
    public Task<IReadOnlyList<TrackedRemotePeer>> FindActivePeersAsync(CancellationToken ct = default) =>
        Task.FromResult(_store.Snapshot());

    private bool TryStartSession()
    {
        IntPtr properties = AllocateProperties();
        try
        {
            int result = EtwInterop.StartTrace(out ulong handle, SessionName, properties);

            if (result == EtwInterop.ErrorAlreadyExists)
            {
                // Остался сеанс от прошлого запуска службы (например, после аварийной остановки):
                // это наш собственный сеанс, поэтому забираем имя, а не придумываем новое.
                _log($"UDP-собеседники: сеанс «{SessionName}» уже существует — останавливаю прежний и открываю заново.");
                EtwInterop.ControlTrace(0, SessionName, properties, EtwInterop.EventControlCodeStop);
                result = EtwInterop.StartTrace(out handle, SessionName, properties);
            }

            if (result != EtwInterop.ErrorSuccess)
            {
                ProblemRu = $"сеанс ETW не открылся (код {result}). Нужны права администратора (служба работает от LocalSystem)";
                _log($"UDP-собеседники: {ProblemRu}. Служба работает дальше без наблюдателя.");
                return false;
            }

            Guid provider = KernelNetworkProvider;
            int enabled = EtwInterop.EnableTraceEx2(
                handle,
                ref provider,
                EtwInterop.EventControlCodeEnableProvider,
                EtwInterop.TraceLevelVerbose,
                KeywordIpv4 | KeywordIpv6,
                0,
                0,
                IntPtr.Zero);

            if (enabled != EtwInterop.ErrorSuccess)
            {
                ProblemRu = $"провайдер сети не включился в сеансе (код {enabled})";
                _log($"UDP-собеседники: {ProblemRu}. Служба работает дальше без наблюдателя.");
                EtwInterop.ControlTrace(handle, SessionName, properties, EtwInterop.EventControlCodeStop);
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    private IntPtr AllocateProperties()
    {
        int structSize = Marshal.SizeOf<EtwInterop.EventTraceProperties>();
        int nameBytes = (SessionName.Length + 1) * sizeof(char);
        int total = structSize + nameBytes;

        IntPtr buffer = Marshal.AllocHGlobal(total);
        Marshal.Copy(new byte[total], 0, buffer, total);

        var properties = new EtwInterop.EventTraceProperties
        {
            Wnode = new EtwInterop.WnodeHeader
            {
                BufferSize = (uint)total,
                // QPC: самая дешёвая метка времени. Мы её не читаем, но сеанс обязан быть описан
                // так же, как у проверенных реализаций этого провайдера.
                ClientContext = 1,
                Flags = EtwInterop.WnodeFlagTracedGuid,
            },
            BufferSize = 64,
            MinimumBuffers = 4,
            MaximumBuffers = 16,
            LogFileMode = EtwInterop.EventTraceRealTimeMode,
            FlushTimer = 1,
            LoggerNameOffset = (uint)structSize,
            LogFileNameOffset = 0,
        };
        Marshal.StructureToPtr(properties, buffer, false);

        IntPtr nameTarget = IntPtr.Add(buffer, structSize);
        char[] name = (SessionName + "\0").ToCharArray();
        Marshal.Copy(name, 0, nameTarget, name.Length);

        return buffer;
    }

    private void ProcessLoop()
    {
        ulong handle = EtwInterop.InvalidProcessTraceHandle;

        try
        {
            var logFile = new EtwInterop.EventTraceLogFile
            {
                LoggerName = SessionName,
                ProcessTraceMode = EtwInterop.ProcessTraceModeEventRecord | EtwInterop.EventTraceRealTimeMode,
                EventRecordCallback = _callback,
            };

            handle = EtwInterop.OpenTrace(ref logFile);
            if (EtwInterop.IsInvalidTraceHandle(handle))
            {
                ProblemRu = $"не удалось подключиться к сеансу ETW (код {Marshal.GetLastWin32Error()})";
                _log($"UDP-собеседники: {ProblemRu}. Служба работает дальше без наблюдателя.");
                return;
            }

            _traceHandle = handle;
            _log($"UDP-собеседники: чтение событий пошло (описатель {handle}).");

            // Блокируется, пока сеанс не остановят (Dispose/остановка службы). Возврат — не ошибка.
            int result = EtwInterop.ProcessTrace([handle], 1, IntPtr.Zero, IntPtr.Zero);
            if (result != EtwInterop.ErrorSuccess)
            {
                _log($"UDP-собеседники: чтение событий завершилось с кодом {result} — наблюдение остановлено.");
            }
        }
        catch (Exception ex)
        {
            ProblemRu = $"чтение ETW упало: {ex.GetType().Name}: {ex.Message}";
            _log($"UDP-собеседники: {ProblemRu}. Служба работает дальше без наблюдателя.");
        }
        finally
        {
            if (!EtwInterop.IsInvalidTraceHandle(handle))
            {
                EtwInterop.CloseTrace(handle);
            }

            _traceHandle = 0;
        }
    }

    /// <summary>
    /// Обработчик события. Сюда приходят все события включённых ключевых слов (IPv4/IPv6), включая
    /// TCP: отбираем только UDP по номеру. Ошибка в одном событии не имеет права уронить сеанс.
    /// </summary>
    private void OnEventRecord(ref EtwInterop.EventRecord record)
    {
        try
        {
            ushort eventId = record.EventHeader.EventDescriptor.Id;
            if (!KernelNetworkUdpEventParser.IsUdpEvent(eventId))
            {
                return;
            }

            int length = record.UserDataLength;
            if (length <= 0 || record.UserData == IntPtr.Zero)
            {
                return;
            }

            if (length > _payload.Length)
            {
                // Для UDP-шаблонов хватает 52 байт; более длинное событие — не наше.
                return;
            }

            Marshal.Copy(record.UserData, _payload, 0, length);

            if (KernelNetworkUdpEventParser.TryParse(
                    _payload.AsSpan(0, length),
                    eventId,
                    record.EventHeader.EventDescriptor.Version,
                    (int)record.EventHeader.ProcessId,
                    DateTime.UtcNow,
                    out UdpPeerObservation? observation,
                    IsOwnAddress)
                && observation is not null)
            {
                _store.Observe(observation);

                // Второй получатель — пассивный замер задержки: он считает пары «отправка → ответ»
                // по тем же событиям и не отправляет ничего сам.
                _onObservation?.Invoke(observation);
            }
        }
        catch
        {
            // Одно кривое событие — не повод останавливать наблюдение или ронять службу.
        }
    }

    /// <summary>
    /// Это адрес самой машины? Список берётся у интерфейсов и живёт минуту. Любая ошибка перечисления
    /// (интерфейс исчез, среда не отдаёт адреса) — пустой список, а не исключение: наблюдение важнее.
    /// </summary>
    private bool IsOwnAddress(string address)
    {
        DateTime now = DateTime.UtcNow;
        HashSet<string>? cached = _ownAddresses;

        if (cached is null || now - _ownAddressesUtc > TimeSpan.FromMinutes(1))
        {
            cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        cached.Add(unicast.Address.ToString());
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Список остаётся пустым: без него разбор всё равно отсекает непубличные адреса.
            }
            catch (PlatformNotSupportedException)
            {
                // То же самое: наблюдение не должно падать из-за перечисления интерфейсов.
            }

            _ownAddresses = cached;
            _ownAddressesUtc = now;
        }

        return cached.Contains(address);
    }

    private void StopSessionUnsafe()
    {
        try
        {
            IntPtr properties = AllocateProperties();
            try
            {
                EtwInterop.ControlTrace(0, SessionName, properties, EtwInterop.EventControlCodeStop);
            }
            finally
            {
                Marshal.FreeHGlobal(properties);
            }
        }
        catch
        {
            // Остановка сеанса — уборка: её сбой не должен мешать остановке службы.
        }
    }

    public void Dispose()
    {
        Thread? thread;
        ulong traceHandle;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            traceHandle = _traceHandle;

            try
            {
                IntPtr properties = AllocateProperties();
                try
                {
                    EtwInterop.ControlTrace(0, SessionName, properties, EtwInterop.EventControlCodeStop);
                }
                finally
                {
                    Marshal.FreeHGlobal(properties);
                }
            }
            catch
            {
                // см. StopSessionUnsafe
            }

            _sessionStarted = false;
        }

        // ProcessTrace завершится после остановки сеанса; ждём не бесконечно — служба не должна висеть.
        thread?.Join(TimeSpan.FromSeconds(3));

        if (!EtwInterop.IsInvalidTraceHandle(traceHandle))
        {
            EtwInterop.CloseTrace(traceHandle);
        }

        _store.Clear();
        _log($"UDP-собеседники: сеанс «{SessionName}» закрыт.");
    }
}
