using System.Net;

namespace PingForge.Probing;

/// <summary>
/// Источник нагрузки для теста bufferbloat: скачивает данные и честно считает принятые байты.
/// Один вызов — один поток скачивания; тестер запускает несколько параллельно, чтобы занять канал.
/// Контракт отмены: когда токен срабатывает, вернуть уже принятые байты, а не бросать —
/// иначе честно скачанный объём потерялся бы и замер врал бы «нагрузить не удалось».
/// </summary>
public interface ILoadStreamSource
{
    /// <summary>Качать, пока не отменят. Возвращает число реально принятых байт (включая случай отмены).</summary>
    Task<long> DownloadAsync(CancellationToken ct);
}

/// <summary>
/// Настоящая нагрузка: потоковое скачивание с публичной точки замера скорости Cloudflare
/// (speed.cloudflare.com — она для таких замеров и существует). Тело ответа не разбирается,
/// а только считается: каналу важен поток байтов, а не содержимое. Если поток закончился
/// раньше отмены — начинается следующий: короткий ответ сервера не выдаётся за «канал нагружен».
/// </summary>
public sealed class HttpDownloadLoadSource : ILoadStreamSource
{
    /// <summary>Размер одного ответа точки замера: 100 МБ хватает, чтобы поток не упирался в конец тела.</summary>
    public const string DefaultEndpoint = "https://speed.cloudflare.com/__down?bytes=100000000";

    private static readonly HttpClient Client = CreateClient();
    private readonly Uri _endpoint;

    public HttpDownloadLoadSource(Uri? endpoint = null) => _endpoint = endpoint ?? new Uri(DefaultEndpoint);

    public async Task<long> DownloadAsync(CancellationToken ct)
    {
        long total = 0;
        byte[] buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using HttpResponseMessage response = await Client
                    .GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Срок нагрузки вышел посреди чтения: принятые байты — уже факт, отдаём их.
        }

        return total;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        string version = typeof(HttpDownloadLoadSource).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CustomTweakByDoxaiz/{version} (bufferbloat check by button)");
        return client;
    }
}

/// <summary>Исход замера bufferbloat. Числа есть только у Measured — остальные исходы цифр не выдумывают.</summary>
public enum BufferbloatOutcome
{
    /// <summary>Замер выполнен: есть медианы простоя и нагрузки и число скачанных байт.</summary>
    Measured,

    /// <summary>ICMP-проб не хватило: часть пакетов не ответила, медиану считать нечестно.</summary>
    NotEnoughSamples,

    /// <summary>Канал нагрузить не удалось: точка замера недоступна или скачалось ничтожно мало.</summary>
    LoadFailed,

    /// <summary>ICMP запрещён на этой машине: замер задержки недоступен в принципе.</summary>
    IcmpBlocked,
}

/// <summary>Результат замера bufferbloat: медианы RTT в простое и под нагрузкой, объём и время нагрузки.</summary>
public sealed record BufferbloatResult(
    BufferbloatOutcome Outcome,
    double? IdleMedianMs,
    double? LoadedMedianMs,
    long BytesDownloaded,
    double SecondsLoaded,
    string NoteRu)
{
    /// <summary>Рост задержки под нагрузкой — разница медиан. null, пока замер не Measured.</summary>
    public double? GrowthMs => IdleMedianMs is { } idle && LoadedMedianMs is { } loaded ? loaded - idle : null;
}

/// <summary>Бюджеты замера. По умолчанию — боевые (3 с простоя, 10 с нагрузки); тестам нужны короче.</summary>
public sealed record BufferbloatOptions(
    TimeSpan? IdleDuration = null,
    TimeSpan? LoadDuration = null,
    TimeSpan? SampleInterval = null,
    int ParallelStreams = 4)
{
    public TimeSpan Idle => IdleDuration ?? TimeSpan.FromSeconds(3);

    public TimeSpan Load => LoadDuration ?? TimeSpan.FromSeconds(10);

    public TimeSpan Interval => SampleInterval ?? TimeSpan.FromMilliseconds(150);

    /// <summary>Минимальное число живых проб на фазу: ниже него медиана была бы вымыслом.</summary>
    public int MinIdleSamples => 5;

    public int MinLoadedSamples => 10;

    /// <summary>Меньше мегабайта за всю нагрузку — канал не нагружен, рост задержки ничего не доказывает.</summary>
    public long MinBytesDownloaded => 1024 * 1024;
}

/// <summary>
/// Замер bufferbloat: сначала RTT в простое, затем те же пробы под собственной загрузкой канала.
/// Разница медиан — сколько задержки добавляет очередь, когда канал занят (скачивание, облачная
/// синхронизация, чужой стрим в той же сети).
///
/// Честность замера:
///   • нагрузка создаётся только собственными потоками скачивания и только на заданное время —
///     согласие человека спрашивает интерфейс ДО вызова этого класса;
///   • байты считаются по факту; мало байт — замер не засчитан, а не «всё в порядке»;
///   • медианы считаются только по полученным ответам; молчащие пробы не превращаются в нули.
/// </summary>
public sealed class BufferbloatLoadTester
{
    private readonly IIcmpProbe _icmp;
    private readonly ILoadStreamSource _load;
    private readonly BufferbloatOptions _options;

    public BufferbloatLoadTester(IIcmpProbe icmp, ILoadStreamSource load, BufferbloatOptions? options = null)
    {
        _icmp = icmp ?? throw new ArgumentNullException(nameof(icmp));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _options = options ?? new BufferbloatOptions();
    }

    /// <summary>Замерить рост задержки до якоря <paramref name="anchor"/> под нагрузкой.</summary>
    public async Task<BufferbloatResult> MeasureAsync(IPAddress anchor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        // Фаза простоя: пробы до загрузки канала.
        (List<double> idleSamples, bool idleBlocked) = await SamplePhaseAsync(anchor, _options.Idle, ct).ConfigureAwait(false);
        if (idleBlocked)
        {
            return new BufferbloatResult(BufferbloatOutcome.IcmpBlocked, null, null, 0, 0,
                "ICMP-пробы запрещены на этой машине — задержку измерить нечем, нагрузку даже не начинали.");
        }

        if (idleSamples.Count < _options.MinIdleSamples)
        {
            return new BufferbloatResult(BufferbloatOutcome.NotEnoughSamples, null, null, 0, 0,
                $"В простое ответили только {idleSamples.Count} проб из ожидаемых — медиану считать нечестно, нагрузку не начинали.");
        }

        // Фаза нагрузки: потоки скачивания делят один срок, пробы идут рядом.
        using var loadDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loadDeadline.CancelAfter(_options.Load);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task<long>[] streams = Enumerable
            .Range(0, Math.Max(1, _options.ParallelStreams))
            .Select(_ => SafeDownloadAsync(loadDeadline.Token))
            .ToArray();

        (List<double> loadedSamples, bool loadedBlocked) =
            await SamplePhaseAsync(anchor, _options.Load, ct).ConfigureAwait(false);

        loadDeadline.Cancel();
        long[] perStream = await Task.WhenAll(streams).ConfigureAwait(false);
        watch.Stop();

        long bytes = perStream.Sum();
        double seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);

        if (loadedBlocked)
        {
            return new BufferbloatResult(BufferbloatOutcome.IcmpBlocked, Median(idleSamples), null, bytes, seconds,
                "Под нагрузкой ICMP-пробы перестали проходить — рост задержки не измерен.");
        }

        if (bytes < _options.MinBytesDownloaded)
        {
            return new BufferbloatResult(BufferbloatOutcome.LoadFailed, Median(idleSamples), null, bytes, seconds,
                $"Канал нагрузить не удалось: за фазу нагрузки скачалось только {bytes} байт — точка замера недоступна, выводы делать не на чем.");
        }

        if (loadedSamples.Count < _options.MinLoadedSamples)
        {
            return new BufferbloatResult(BufferbloatOutcome.NotEnoughSamples, Median(idleSamples), null, bytes, seconds,
                $"Под нагрузкой ответили только {loadedSamples.Count} проб — медиану нагрузки считать нечестно.");
        }

        return new BufferbloatResult(
            BufferbloatOutcome.Measured,
            Median(idleSamples),
            Median(loadedSamples),
            bytes,
            seconds,
            $"Простой: {idleSamples.Count} проб; нагрузка: {loadedSamples.Count} проб за {seconds:0.#} с.");
    }

    /// <summary>
    /// Один поток нагрузки: его ошибка (обрыв, недоступная точка) — это 0 его байт, а не падение
    /// всего замера, пока остальные потоки несут данные. Сколько байт успело прийти, знает только
    /// источник (см. контракт <see cref="ILoadStreamSource"/>); брошенное им исключение означает,
    /// что его вклад неизвестен — и честно записывается нулём, а не выдумывается.
    /// </summary>
    private async Task<long> SafeDownloadAsync(CancellationToken ct)
    {
        try
        {
            return await _load.DownloadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Пробы через равные интервалы на протяжении фазы. Молчание — просто нет сэмпла.</summary>
    private async Task<(List<double> Samples, bool PermissionDenied)> SamplePhaseAsync(
        IPAddress anchor, TimeSpan duration, CancellationToken ct)
    {
        var samples = new List<double>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            IcmpProbeReply reply = await _icmp
                .SendAsync(anchor, payloadBytes: 32, dontFragment: false, ttl: 128, timeout: TimeSpan.FromSeconds(1), ct)
                .ConfigureAwait(false);
            sw.Stop();

            if (reply.Status == IcmpProbeStatus.PermissionDenied)
            {
                return (samples, true);
            }

            if (reply.Status == IcmpProbeStatus.Success)
            {
                // Транспорт может не заполнить RoundTripMs — тогда время честно берётся секундомером.
                samples.Add(reply.RoundTripMs > 0 ? reply.RoundTripMs : sw.Elapsed.TotalMilliseconds);
            }

            TimeSpan left = _options.Interval - sw.Elapsed;
            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left, ct).ConfigureAwait(false);
            }
        }

        return (samples, false);
    }

    private static double? Median(List<double> samples) => samples.Count == 0
        ? null
        : Math.Round(StatisticalMetricsCalculator.Percentile(samples.OrderBy(x => x).ToList(), 0.5), 1);
}
