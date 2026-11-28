using System.Net;

namespace PingForge.Network;

/// <summary>
/// Одно наблюдение UDP-собеседника процесса: с кем именно процесс только что обменялся датаграммой.
///
/// Почему это отдельная запись, а не строка таблицы соединений: таблица UDP в Windows знает только
/// локальный сокет, удалённая сторона в ней не хранится. Наблюдение приходит из событий сетевого
/// стека (ETW), то есть это факт обмена, а не догадка по имени процесса.
/// </summary>
/// <param name="ProcessId">Процесс-владелец сокета (из полезной нагрузки события, не из заголовка).</param>
/// <param name="RemoteAddress">Адрес собеседника в обычной записи IPv4/IPv6.</param>
/// <param name="RemotePort">Порт собеседника. Ноль — «порт не определён», такое не сохраняется.</param>
/// <param name="IsSend">true — процесс отправил датаграмму, false — получил.</param>
/// <param name="ObservedUtc">Когда наблюдение сделано (UTC).</param>
public sealed record UdpPeerObservation(
    int ProcessId,
    string RemoteAddress,
    int RemotePort,
    bool IsSend,
    DateTime ObservedUtc);

/// <summary>
/// Короткая память о UDP-собеседниках: последние 30 секунд, не больше 4096 записей, только публичные
/// адреса.
///
/// Правила, зашитые в код:
///   • приватные, петлевые, канальные и многоадресные адреса не сохраняются: это не игровой сервер,
///     и оптимизировать там нечего;
///   • запись живёт не дольше <see cref="DefaultMaxAge"/>: остывший собеседник — это уже не поток;
///   • дубликат (тот же процесс, адрес и порт) обновляет время, а не раздувает список;
///   • переполнение выбрасывает самые старые записи, а не самые «неудобные»;
///   • наружу отдаётся копия: прочитавший не может незаметно поменять память наблюдателя.
///
/// Хранилище потокобезопасно: события ETW приходят из одного потока, а спрашивает их служба из другого.
/// </summary>
public sealed class RecentUdpPeerStore
{
    /// <summary>Сколько записей хранится одновременно.</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>Сколько живёт наблюдение. Совпадает с возрастом собеседника у детектора игр.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, UdpPeerObservation> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _clock;

    public RecentUdpPeerStore(
        int capacity = DefaultCapacity,
        TimeSpan? maxAge = null,
        Func<DateTime>? clock = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ёмкость хранилища должна быть положительной.");
        }

        _capacity = capacity;
        _maxAge = maxAge ?? DefaultMaxAge;
        if (_maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Срок жизни наблюдения должен быть положительным.");
        }

        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Сколько записей лежит сейчас (вместе с теми, что вот-вот остынут).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Запомнить наблюдение. false — запись не сохранена (мусор, приватный адрес или нулевой порт):
    /// это не ошибка, а честный отказ.
    /// </summary>
    public bool Observe(UdpPeerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ProcessId <= 0
            || observation.RemotePort is <= 0 or > 65535
            || !GameFlowDetector.IsPublicRemoteAddress(observation.RemoteAddress))
        {
            return false;
        }

        string key = Key(observation.ProcessId, observation.RemoteAddress, observation.RemotePort);

        lock (_sync)
        {
            PruneExpiredLocked(observation.ObservedUtc);
            _entries[key] = observation;

            if (_entries.Count > _capacity)
            {
                foreach (string stale in _entries
                             .OrderBy(pair => pair.Value.ObservedUtc)
                             .Take(_entries.Count - _capacity)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    _entries.Remove(stale);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Живые собеседники на момент вызова. Возраст считается от текущего времени хранилища,
    /// а не от времени последнего наблюдения: так «остывшая» запись не может притвориться живой.
    /// </summary>
    public IReadOnlyList<TrackedRemotePeer> Snapshot()
    {
        DateTime now = _clock();

        lock (_sync)
        {
            PruneExpiredLocked(now);

            return _entries.Values
                .OrderByDescending(observation => observation.ObservedUtc)
                .ThenBy(observation => observation.ProcessId)
                .Select(observation => new TrackedRemotePeer(
                    observation.ProcessId,
                    observation.RemoteAddress,
                    observation.RemotePort,
                    ConnectionProtocol.Udp,
                    observation.ObservedUtc))
                .ToList();
        }
    }

    /// <summary>Забыть всё: используется при остановке наблюдения и в тестах.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private void PruneExpiredLocked(DateTime nowUtc)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        foreach (string stale in _entries
                     .Where(pair => nowUtc - pair.Value.ObservedUtc > _maxAge)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(stale);
        }
    }

    private static string Key(int processId, string address, int port) =>
        $"{processId}|{address}:{port}";

    /// <summary>
    /// Публичный ли адрес собеседника — та же проверка, что у детектора игр. Отдельный метод нужен,
    /// чтобы правило было одно: если детектор считает адрес игровым, хранилище не может решить иначе.
    /// </summary>
    public static bool IsPublicPeer(string? address) =>
        !string.IsNullOrWhiteSpace(address) && GameFlowDetector.IsPublicRemoteAddress(address!);

    /// <summary>Разбор адреса для тестов и журнала: null — строка не является адресом.</summary>
    public static IPAddress? ParseAddress(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) ? parsed : null;
}
