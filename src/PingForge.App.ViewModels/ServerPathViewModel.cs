using System.Globalization;
using System.Net;
using PingForge.Probing;

namespace PingForge.App.ViewModels;

/// <summary>
/// Транспорт трассы для модели окна. Отдельный интерфейс, а не прямой вызов
/// <see cref="ServerPathTracer"/>: тесты проверяют поведение окна без единого пакета в сеть.
/// </summary>
public interface IServerPathRunner
{
    /// <summary>
    /// Построить путь до адреса. <paramref name="onHop"/> вызывается на каждый законченный хоп —
    /// модель может показывать прогресс; сама трасса ничего в системе не меняет.
    /// </summary>
    Task<ServerPathResult> TraceAsync(
        IPAddress destination,
        Action<ServerPathProbeHop>? onHop = null,
        CancellationToken ct = default);
}

/// <summary>Настоящая трасса поверх ICMP-транспорта Windows.</summary>
public sealed class IcmpServerPathRunner(IIcmpProbe icmp) : IServerPathRunner
{
    private readonly ServerPathTracer _tracer = new(icmp);

    public Task<ServerPathResult> TraceAsync(
        IPAddress destination,
        Action<ServerPathProbeHop>? onHop = null,
        CancellationToken ct = default) =>
        _tracer.TraceAsync(destination, onHop, ct: ct);
}

/// <summary>Разбор адреса живой сессии вида «85.140.1.2:27015» — только IPv4, без домыслов.</summary>
public static class ServerPathTarget
{
    public static bool TryParseEndpoint(string? endpoint, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        string text = endpoint.Trim();

        // Порт отрезается по последнему двоеточию: «адрес:порт» — формат службы.
        string host = text.Contains(':', StringComparison.Ordinal)
            ? text[..text.LastIndexOf(':')]
            : text;

        return IPAddress.TryParse(host, out address)
               && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }
}

/// <summary>
/// Строка таблицы «Путь до сервера». Числа и текст рядом: интерфейс показывает текст,
/// тесты и самопроверка проверяют числа — и там, где измерения нет, это <c>null</c> и «—»,
/// а не ноль.
/// </summary>
public sealed record ServerPathHopRow(
    int HopNumber,
    string AddressRu,
    bool HasReply,
    double? AvgRttMs,
    double? P95RttMs,
    double? LossPercent,
    double? GrowthMs,
    bool IsBottleneck)
{
    public string HopNumberRu => HopNumber.ToString(CultureInfo.InvariantCulture);

    public string AvgRttRu => FormatMs(AvgRttMs);

    public string P95RttRu => FormatMs(P95RttMs);

    public string LossRu => LossPercent is { } loss
        ? loss.ToString("0.#", CultureInfo.CurrentCulture) + " %"
        : "—";

    public string GrowthRu => GrowthMs is { } growth
        ? (growth >= 0 ? "+" : string.Empty) + growth.ToString("0.#", CultureInfo.CurrentCulture) + " мс"
        : "—";

    private static string FormatMs(double? value) =>
        value is { } ms ? ms.ToString("0.#", CultureInfo.CurrentCulture) + " мс" : "—";
}

/// <summary>Прирост задержки на конкретном хопе.</summary>
public sealed record ServerPathGrowthPoint(int HopNumber, double GrowthMs);

/// <summary>
/// «Где растёт задержка» — чистая логика, без сети и без окна.
///
/// Прирост считается только между соседними ОТВЕТИВШИМИ хопами: анонимный хоп ничего не измеряет,
/// и приписывать ему скачок нельзя. Первый ответивший хоп сравнивается с нулём — его задержка
/// и есть первый шаг из дома. Виновник не утверждается: текст говорит «похоже» и подсказывает,
/// что проверить.
/// </summary>
public static class ServerPathGrowth
{
    /// <summary>Прирост меньше миллисекунды — шум измерения, а не находка.</summary>
    public const double NoticeableGrowthMs = 1.0;

    public static IReadOnlyList<ServerPathGrowthPoint> Points(IReadOnlyList<ServerPathProbeHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);

        var points = new List<ServerPathGrowthPoint>(hops.Count);
        double? previous = null;

        foreach (ServerPathProbeHop hop in hops.OrderBy(h => h.HopNumber))
        {
            if (hop.AvgRttMs is not { } average)
            {
                continue;
            }

            double growth = previous is null ? average : average - previous.Value;
            points.Add(new ServerPathGrowthPoint(hop.HopNumber, Math.Round(growth, 2)));
            previous = average;
        }

        return points;
    }

    /// <summary>Хоп с самым большим заметным приростом; null — заметного прироста нет.</summary>
    public static ServerPathGrowthPoint? Bottleneck(IReadOnlyList<ServerPathProbeHop> hops) =>
        Points(hops)
            .Where(point => point.GrowthMs >= NoticeableGrowthMs)
            .OrderByDescending(point => point.GrowthMs)
            .FirstOrDefault();

    /// <summary>
    /// Честная подсказка по трассе. Дом — только тогда, когда скачок случился на первом шаге
    /// (роутер или Wi-Fi). Скачок дальше — это путь провайдера или магистрали, и там предлагается
    /// альтернативный маршрут: второй канал или бесплатный туннель с точечным /32 для игры.
    /// </summary>
    public static string DescribeRu(IReadOnlyList<ServerPathProbeHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);

        if (hops.Count == 0)
        {
            return "Трасса не построена — говорить о росте задержки не по чему.";
        }

        if (Points(hops).Count == 0)
        {
            return "Ни один хоп не ответил: где именно теряются миллисекунды, по этой трассе сказать нельзя.";
        }

        if (Bottleneck(hops) is not { } bottleneck)
        {
            return "Задержка по трассе ровная: заметного прироста между соседними ответившими хопами нет.";
        }

        string growth = bottleneck.GrowthMs.ToString("0.#", CultureInfo.CurrentCulture);

        if (bottleneck.HopNumber == 1)
        {
            return $"Наибольший прирост задержки — +{growth} мс уже на первом шаге (роутер или Wi-Fi). " +
                   "Похоже, причина в домашней сети: проверьте Wi-Fi-канал, загруженность роутера и кабель.";
        }

        return $"Наибольший прирост задержки — +{growth} мс на хопе {bottleneck.HopNumber}, дальше первого шага. " +
               "Похоже, дело не в домашней сети, а на пути у провайдера или на магистрали. " +
               "Проверьте альтернативный путь в блоке «Сервер маршрутизации» кнопкой «Измерить все пути»: " +
               "второй канал или бесплатный туннель вроде Cloudflare WARP / WireGuard с точечным маршрутом /32 " +
               "только для игры — без заворачивания всего компьютера.";
    }
}

/// <summary>
/// «Путь до сервера» (задача H) в разделе «Оптимизация».
///
/// Трасса строится только до адреса найденного игрового сервера и только при живой сессии: без игры
/// программа честно пишет «сначала запустите игру» и НЕ отправляет ни одного пакета. Каждый хоп
/// показывает адрес, средний и 95-й RTT, потери; анонимный хоп — одна строка «не отвечает», а
/// неизмеренные значения остаются пустыми («—»), не нулями.
/// </summary>
public sealed class ServerPathViewModel : ObservableObject
{
    /// <summary>Честный ответ на нажатие без живой игры — ровно эти слова ищет самопроверка.</summary>
    public const string NoGameRu =
        "Сначала запустите игру: путь строится только до адреса игрового сервера, к которому игра уже подключилась. " +
        "Пока живой сессии нет, программа не отправляет ни одного пакета.";

    private const string IdleRu =
        "Путь ещё не строился: нажмите «Показать путь» — трасса пойдёт только до адреса игрового сервера.";

    private const string ExplanationRu =
        "Трасса до адреса игрового сервера: до 30 хопов, по 3 пробы на хоп, не дольше 60 секунд. " +
        "Ничего не меняет — только эхо-запросы с TTL. Анонимный хоп показан строкой «не отвечает», " +
        "а неизмеренное время — прочерком, а не нулём.";

    private readonly IServerPathRunner _runner;
    private readonly Func<string?> _liveGameEndpoint;
    private readonly TimeSpan _timeLimit;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<ServerPathHopRow> _hops = [];
    private string _statusRu = IdleRu;
    private string _summaryRu = string.Empty;
    private string _growthNoteRu = string.Empty;
    private string _explanationRu = ExplanationRu;
    private bool _isRunning;
    private bool _hasResult;
    private int _sentPackets;

    public ServerPathViewModel(
        IServerPathRunner runner,
        Func<string?> liveGameEndpoint,
        TimeSpan? timeLimit = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _liveGameEndpoint = liveGameEndpoint ?? throw new ArgumentNullException(nameof(liveGameEndpoint));
        _timeLimit = timeLimit is { } custom && custom > TimeSpan.Zero ? custom : ServerPathTracer.DefaultTimeLimit;

        TraceCommand = new AsyncRelayCommand(TraceAsync);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    /// <summary>Сборка без подключённой трассы: кнопка есть, пакетов нет, причина названа.</summary>
    internal static ServerPathViewModel Unavailable() => new(new UnavailableRunner(), () => null);

    public AsyncRelayCommand TraceCommand { get; }

    public RelayCommand CancelCommand { get; }

    public IReadOnlyList<ServerPathHopRow> Hops
    {
        get => _hops;
        private set
        {
            if (Set(ref _hops, value))
            {
                OnPropertyChanged(nameof(HasHops));
            }
        }
    }

    public bool HasHops => Hops.Count > 0;

    public bool HasResult
    {
        get => _hasResult;
        private set => Set(ref _hasResult, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Сколько проб реально ушло в сеть. При отказе без игры — ровно ноль.</summary>
    public int SentPackets
    {
        get => _sentPackets;
        private set => Set(ref _sentPackets, value);
    }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public string SummaryRu
    {
        get => _summaryRu;
        private set => Set(ref _summaryRu, value);
    }

    /// <summary>Подсказка «где растёт задержка» и что с этим делать.</summary>
    public string GrowthNoteRu
    {
        get => _growthNoteRu;
        private set => Set(ref _growthNoteRu, value);
    }

    /// <summary>Пояснение к блоку — то же до и после запуска, живёт в одном месте.</summary>
    public string ExplanationRu
    {
        get => _explanationRu;
        private set => Set(ref _explanationRu, value);
    }

    /// <summary>Кнопка «Показать путь». Без живой игры — честный отказ и ноль пакетов.</summary>
    public async Task TraceAsync()
    {
        if (IsRunning)
        {
            return;
        }

        string? endpoint = _liveGameEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ResetReport();
            StatusRu = NoGameRu;
            return;
        }

        if (!ServerPathTarget.TryParseEndpoint(endpoint, out IPAddress? target) || target is null)
        {
            ResetReport();
            StatusRu = $"Адрес игрового сервера «{endpoint}» — не IPv4. Трассу по имени не строю: числа не выдумываю.";
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        cts.CancelAfter(_timeLimit);

        IsRunning = true;
        ResetReport();
        StatusRu = $"Строю трассу до {target}: до {ServerPathTracer.DefaultMaxHops} хопов по " +
                   $"{ServerPathTracer.DefaultProbesPerHop} пробы, не дольше {Math.Round(_timeLimit.TotalSeconds)} секунд. " +
                   "Ничего не меняется.";

        try
        {
            ServerPathResult result = await _runner
                .TraceAsync(target, null, cts.Token)
                .ConfigureAwait(true);

            SentPackets = result.ProbesSent;
            Hops = BuildRows(result.Hops);
            HasResult = result.Hops.Count > 0;
            SummaryRu = BuildSummary(result);
            GrowthNoteRu = ServerPathGrowth.DescribeRu(result.Hops);
            StatusRu = DescribeStop(result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusRu = SentPackets > 0
                ? $"Трасса отменена по кнопке. Успели отправить проб: {SentPackets}; строк в таблице: {Hops.Count}."
                : "Трасса отменена по кнопке: ни одной пробы в сеть не ушло.";
        }
        catch (Exception ex)
        {
            StatusRu = $"Трассу не удалось построить: {ex.Message}. Ничего не изменилось — трасса только читает и измеряет.";
        }
        finally
        {
            IsRunning = false;
            _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>Отмена кнопкой: результат — то, что успели измерить, а не выдуманная трасса.</summary>
    public void Cancel()
    {
        if (!IsRunning || _cts is null)
        {
            return;
        }

        StatusRu = "Отменяю трассу: покажу то, что успели измерить.";
        _cts.Cancel();
    }

    private void ResetReport()
    {
        Hops = [];
        HasResult = false;
        SummaryRu = string.Empty;
        GrowthNoteRu = string.Empty;
        SentPackets = 0;
    }

    private static IReadOnlyList<ServerPathHopRow> BuildRows(IReadOnlyList<ServerPathProbeHop> hops)
    {
        Dictionary<int, double> growth = ServerPathGrowth.Points(hops).ToDictionary(point => point.HopNumber, point => point.GrowthMs);
        ServerPathGrowthPoint? bottleneck = ServerPathGrowth.Bottleneck(hops);

        return hops
            .OrderBy(hop => hop.HopNumber)
            .Select(hop => new ServerPathHopRow(
                hop.HopNumber,
                hop.IsAnonymous ? "не отвечает" : hop.ResponderAddress!,
                !hop.IsAnonymous,
                hop.AvgRttMs,
                hop.P95RttMs,
                hop.LossPercent,
                growth.TryGetValue(hop.HopNumber, out double value) ? value : null,
                bottleneck?.HopNumber == hop.HopNumber))
            .ToList();
    }

    private static string BuildSummary(ServerPathResult result)
    {
        if (result.Hops.Count == 0)
        {
            return "В трассе нет ни одной строки: пробы не ушли или не вернулись. Числа не выдумываем.";
        }

        int answered = result.Hops.Count(hop => !hop.IsAnonymous);
        int anonymous = result.Hops.Count - answered;

        string summary = $"Хопов в трассе: {result.Hops.Count}, ответили: {answered}, не отвечают: {anonymous}. " +
                         $"Проб отправлено: {result.ProbesSent}, ответов: {result.ProbesAnswered}.";

        double? last = result.Hops
            .Where(hop => hop.AvgRttMs is not null)
            .OrderBy(hop => hop.HopNumber)
            .Select(hop => hop.AvgRttMs)
            .LastOrDefault();

        if (last is { } lastMs)
        {
            summary += $" Время до последнего ответившего хопа: {lastMs:0.#} мс — это не пинг в игре, а ответ этого узла.";
        }

        return summary;
    }

    private static string DescribeStop(ServerPathResult result) => result.StopReason switch
    {
        ServerPathStopReason.ReachedTarget =>
            "Трасса построена: последний ответивший хоп — адрес игрового сервера. Ничего не менялось.",
        ServerPathStopReason.MaxHops =>
            result.StopNoteRu ?? "Трасса дошла до предела хопов: цель так и не ответила.",
        ServerPathStopReason.SilenceLimit =>
            result.StopNoteRu ?? "Четыре хопа подряд не ответили — дальше строить нет смысла.",
        ServerPathStopReason.TimeLimit =>
            result.StopNoteRu ?? $"Трасса остановлена по лимиту {Math.Round(ServerPathTracer.DefaultTimeLimit.TotalSeconds)} секунд.",
        ServerPathStopReason.Cancelled =>
            "Трасса отменена: показываю то, что успели измерить.",
        ServerPathStopReason.PermissionDenied =>
            result.StopNoteRu ?? "Windows не дала отправить ICMP-пробы: измерять нечем, числа не выдумываем.",
        _ => "Трасса закончилась.",
    };

    /// <summary>Заглушка для сборок без настоящего транспорта: ни одного пакета.</summary>
    private sealed class UnavailableRunner : IServerPathRunner
    {
        public Task<ServerPathResult> TraceAsync(
            IPAddress destination,
            Action<ServerPathProbeHop>? onHop = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Трасса не подключена в этой сборке окна: измерять нечем, выдумывать не будем.");
    }
}
