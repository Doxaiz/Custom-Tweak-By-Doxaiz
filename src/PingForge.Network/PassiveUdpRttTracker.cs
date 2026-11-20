namespace PingForge.Network;

/// <summary>
/// Итог пассивного наблюдения за одним UDP-собеседником: сколько раз успели сопоставить отправку с ответом,
/// какая вышла медиана и в каком порядке шли замеры (порядок нужен джиттеру — он считается по соседним).
/// </summary>
/// <param name="Samples">Сколько полных «отправка → ответ» попало в окно.</param>
/// <param name="MedianMs">Медиана задержки по этим замерам.</param>
/// <param name="SamplesInOrder">Замеры в том порядке, в котором случились, — для расчёта джиттера.</param>
/// <param name="UnansweredSends">Сколько отправок осталось без ответа (или ответ пришёл позже окна).</param>
/// <param name="LastSampleUtc">Когда случился последний полный замер.</param>
public sealed record PassiveUdpRttSummary(
    int Samples,
    double MedianMs,
    IReadOnlyList<double> SamplesInOrder,
    int UnansweredSends,
    DateTime LastSampleUtc);

/// <summary>
/// Пассивный замер задержки живого UDP-потока: время между отправкой датаграммы и ответом на неё.
///
/// Зачем это: до сих пор живой поток читался только активными пробами — ICMP-эхо и TCP-рукопожатием.
/// Это лишние пакеты сверх трафика игры, и текст программы так и говорил. События сетевого стека уже
/// приходят в службу (<see cref="RecentUdpPeerStore"/>), и по ним можно посчитать задержку, ничего не отправляя.
///
/// Правила, зашитые в код:
///   • замер — это пара «отправка → ответ» к одному и тому же собеседнику (процесс, адрес, порт);
///   • ответ без отправки числом не считается: откуда он взялся, мы не знаем;
///   • ответ, пришедший раньше отправки (сбитые часы, переставленные события), отбрасывается, а не
///     превращается в отрицательную задержку;
///   • ответ позже окна <see cref="DefaultMaxRoundTrip"/> — это уже не замер, а потеря: отправка
///     считается неотвеченной;
///   • собеседники разных процессов не смешиваются: у каждой игры своё окно;
///   • старые замеры выпадают из окна, самые свежие остаются;
///   • приватные и служебные адреса не наблюдается вовсе — то же правило, что у хранилища собеседников.
///
/// Класс потокобезопасен: наблюдения приходят из потока ETW, а спрашивает служба из другого.
/// </summary>
public sealed class PassiveUdpRttTracker
{
    /// <summary>Сколько замеров хранится на одного собеседника.</summary>
    public const int DefaultSamplesPerPeer = 128;

    /// <summary>Дольше этого ответ уже не ответ: задержка такого размера ничего не говорит о пути.</summary>
    public static readonly TimeSpan DefaultMaxRoundTrip = TimeSpan.FromSeconds(2);

    /// <summary>Сколько живёт собеседник без событий.</summary>
    public static readonly TimeSpan DefaultMaxIdle = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _samplesPerPeer;
    private readonly TimeSpan _maxRoundTrip;
    private readonly TimeSpan _maxIdle;
    private readonly Func<DateTime> _clock;

    public PassiveUdpRttTracker(
        int samplesPerPeer = DefaultSamplesPerPeer,
        TimeSpan? maxRoundTrip = null,
        TimeSpan? maxIdle = null,
        Func<DateTime>? clock = null)
    {
        if (samplesPerPeer <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerPeer), "Окно замеров должно быть положительным.");
        }

        _samplesPerPeer = samplesPerPeer;
        _maxRoundTrip = maxRoundTrip ?? DefaultMaxRoundTrip;
        _maxIdle = maxIdle ?? DefaultMaxIdle;
        if (_maxRoundTrip <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRoundTrip), "Окно ответа должно быть положительным.");
        }

        if (_maxIdle <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdle), "Срок жизни собеседника должен быть положительным.");
        }

        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Сколько собеседников помнит трекер прямо сейчас.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _peers.Count;
            }
        }
    }

    /// <summary>
    /// Запомнить наблюдение. false — наблюдение не наше: мусор, приватный адрес, нулевой порт.
    /// Отказ ничего не ломает: это не ошибка, а честное «не наблюдаем».
    /// </summary>
    public bool Observe(UdpPeerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ProcessId <= 0
            || observation.RemotePort is <= 0 or > 65535
            || !RecentUdpPeerStore.IsPublicPeer(observation.RemoteAddress))
        {
            return false;
        }

        string key = Key(observation.ProcessId, observation.RemoteAddress, observation.RemotePort);

        lock (_sync)
        {
            PruneIdleLocked(observation.ObservedUtc);

            if (!_peers.TryGetValue(key, out PeerState? state))
            {
                state = new PeerState { Address = observation.RemoteAddress, Port = observation.RemotePort };
                _peers[key] = state;
            }

            state.LastSeenUtc = observation.ObservedUtc;

            if (observation.IsSend)
            {
                // Прошлая отправка так и не получила ответа — считаем это потерей, а не тишиной.
                if (state.PendingSendUtc is { } pending
                    && observation.ObservedUtc - pending > _maxRoundTrip)
                {
                    state.UnansweredSends++;
                }

                state.PendingSendUtc = observation.ObservedUtc;
                return true;
            }

            if (state.PendingSendUtc is not { } sent)
            {
                // Ответ без отправки: сопоставлять не с чем, число не выдумываем.
                return true;
            }

            state.PendingSendUtc = null;

            double elapsedMs = (observation.ObservedUtc - sent).TotalMilliseconds;

            if (elapsedMs <= 0)
            {
                // Часы или порядок событий сбились: отрицательная задержка невозможна.
                return true;
            }

            if (elapsedMs > _maxRoundTrip.TotalMilliseconds)
            {
                state.UnansweredSends++;
                return true;
            }

            state.Samples.Add(elapsedMs);
            if (state.Samples.Count > _samplesPerPeer)
            {
                state.Samples.RemoveRange(0, state.Samples.Count - _samplesPerPeer);
            }

            state.LastSampleUtc = observation.ObservedUtc;
            return true;
        }
    }

    /// <summary>Есть ли замеры у этого процесса и этого собеседника.</summary>
    public bool TryGet(int processId, string address, int port, out PassiveUdpRttSummary summary)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(address) || port is <= 0 or > 65535)
        {
            summary = null!;
            return false;
        }

        DateTime now = _clock();

        lock (_sync)
        {
            PruneIdleLocked(now);

            if (!_peers.TryGetValue(Key(processId, address, port), out PeerState? state) || state.Samples.Count == 0)
            {
                summary = null!;
                return false;
            }

            summary = BuildSummary(state);
            return true;
        }
    }

    /// <summary>
    /// Самый свежий замер среди процессов, говорящих с этим адресом и портом. Нужен, когда вызывающий
    /// не знает номер процесса: замер всё равно про этого собеседника, а не про «интернет вообще».
    /// </summary>
    public bool TryGetFreshest(string address, int port, out PassiveUdpRttSummary summary)
    {
        if (string.IsNullOrWhiteSpace(address) || port is <= 0 or > 65535)
        {
            summary = null!;
            return false;
        }

        DateTime now = _clock();

        lock (_sync)
        {
            PruneIdleLocked(now);

            PeerState? freshest = null;
            foreach (PeerState state in _peers.Values)
            {
                if (state.Samples.Count == 0
                    || !string.Equals(state.Address, address, StringComparison.OrdinalIgnoreCase)
                    || state.Port != port)
                {
                    continue;
                }

                if (freshest is null || state.LastSampleUtc > freshest.LastSampleUtc)
                {
                    freshest = state;
                }
            }

            if (freshest is null)
            {
                summary = null!;
                return false;
            }

            summary = BuildSummary(freshest);
            return true;
        }
    }

    /// <summary>Забыть всё: остановка наблюдения и тесты.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _peers.Clear();
        }
    }

    private static PassiveUdpRttSummary BuildSummary(PeerState state)
    {
        List<double> samples = state.Samples.ToList();
        return new PassiveUdpRttSummary(
            samples.Count,
            Median(samples),
            samples,
            state.UnansweredSends,
            state.LastSampleUtc);
    }

    private static double Median(IReadOnlyList<double> samples)
    {
        List<double> sorted = samples.OrderBy(value => value).ToList();
        int middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private void PruneIdleLocked(DateTime nowUtc)
    {
        if (_peers.Count == 0)
        {
            return;
        }

        foreach (string stale in _peers
                     .Where(pair => nowUtc - pair.Value.LastSeenUtc > _maxIdle)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _peers.Remove(stale);
        }
    }

    private static string Key(int processId, string address, int port) => $"{processId}|{address}:{port}";

    private sealed class PeerState
    {
        public string Address { get; init; } = string.Empty;

        public int Port { get; init; }

        public DateTime? PendingSendUtc { get; set; }

        public List<double> Samples { get; } = new();

        public DateTime LastSeenUtc { get; set; }

        public DateTime LastSampleUtc { get; set; }

        public int UnansweredSends { get; set; }
    }
}
