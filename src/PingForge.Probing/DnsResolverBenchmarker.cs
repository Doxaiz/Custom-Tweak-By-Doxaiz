using System.Net;
using PingForge.Core.Models;

namespace PingForge.Probing;

/// <summary>Сырой ответ DNS — валидация выполняется бенчмаркером, а не транспортом.</summary>
public sealed record DnsRawResponse(
    IPEndPoint Source,
    ushort TransactionId,
    bool IsResponse,
    string QuestionName,
    int ResponseCode,
    int AnswerCount);

public interface IDnsQueryTransport
{
    Task<DnsRawResponse?> QueryAsync(IPAddress server, string domain, ushort transactionId, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Бенчмарк DNS-резолверов.
///
/// Отличия от наивной версии:
///   • транзакционный ID рандомизируется на каждый запрос;
///   • ответ валидируется по ID, признаку ответа, имени вопроса и адресу источника;
///   • доля успеха считается от фактического числа отправленных запросов, а не от константы;
///   • канареечный запрос в зарезервированную зону .invalid выявляет перехват DNS провайдером
///     (иначе «быстрый» ответ прокси-сервера обманет пользователя).
/// </summary>
public sealed class DnsResolverBenchmarker
{
    public static readonly IReadOnlyList<(string Name, string Primary, string Secondary)> DefaultCandidates = new[]
    {
        ("Cloudflare DNS", "1.1.1.1", "1.0.0.1"),
        ("Google Public DNS", "8.8.8.8", "8.8.4.4"),
        ("Quad9 DNS", "9.9.9.9", "149.112.112.112"),
        ("OpenDNS", "208.67.222.222", "208.67.220.220")
    };

    public static readonly IReadOnlyList<string> DefaultProbeDomains = new[]
    {
        "steampowered.com",
        "riotgames.com",
        "blizzard.com",
        "epicgames.com",
        "cloudflare.com"
    };

    private readonly IDnsQueryTransport _transport;
    private readonly IReadOnlyList<string> _domains;
    private readonly Func<ushort> _idGenerator;
    private readonly TimeSpan _timeout;

    public DnsResolverBenchmarker(
        IDnsQueryTransport transport,
        IReadOnlyList<string>? probeDomains = null,
        Func<ushort>? transactionIdGenerator = null,
        TimeSpan? perQueryTimeout = null)
    {
        _transport = transport;
        _domains = probeDomains ?? DefaultProbeDomains;
        _idGenerator = transactionIdGenerator ?? (() => (ushort)Random.Shared.Next(1, ushort.MaxValue));
        _timeout = perQueryTimeout ?? TimeSpan.FromMilliseconds(800);
    }

    public async Task<IReadOnlyList<DnsResolverBenchmarkResult>> BenchmarkAsync(
        IReadOnlyList<(string Name, string Primary, string Secondary)>? candidates = null,
        CancellationToken ct = default)
    {
        var list = candidates ?? DefaultCandidates;
        var results = new List<DnsResolverBenchmarkResult>(list.Count);

        foreach ((string name, string primary, string secondary) in list)
        {
            results.Add(await BenchmarkSingleAsync(name, primary, secondary, ct).ConfigureAwait(false));
        }

        // Недостижимые резолверы уходят в конец, остальные — по возрастанию score (меньше = лучше).
        return results
            .OrderBy(r => r.IsReachable ? 0 : 1)
            .ThenBy(r => r.CompositeScore)
            .ToList();
    }

    private async Task<DnsResolverBenchmarkResult> BenchmarkSingleAsync(string name, string primary, string secondary, CancellationToken ct)
    {
        if (!IPAddress.TryParse(primary, out IPAddress? server))
        {
            return Unreachable(name, primary, secondary, "Некорректный IP-адрес резолвера.");
        }

        int sent = 0;
        var warm = new List<double>();
        var cold = new List<double>();
        var all = new List<double>();
        bool hijacked = false;

        // Канареечная проверка: имя в зарезервированной зоне .invalid существовать не может.
        // Если резолвер отвечает на него с записями — трафик перехвачен (провайдерский прокси).
        sent++;
        string canary = $"pingforge-canary-{Guid.NewGuid():N}.invalid";
        DnsRawResponse? canaryResponse = await SendValidatedAsync(server, canary, ct).ConfigureAwait(false);
        if (canaryResponse is not null && canaryResponse.AnswerCount > 0)
        {
            hijacked = true;
        }

        foreach (string domain in _domains)
        {
            ct.ThrowIfCancellationRequested();

            // Прогретый кэш: повторный запрос уже известного имени.
            sent++;
            double warmMs = await MeasureAsync(server, domain, ct).ConfigureAwait(false);
            if (warmMs >= 0)
            {
                warm.Add(warmMs);
                all.Add(warmMs);
            }

            // Холодный кэш: случайное поддоменное имя, которого заведомо нет в кэше.
            sent++;
            string randomName = $"pf-{Guid.NewGuid():N}"[..12] + "." + domain;
            double coldMs = await MeasureAsync(server, randomName, ct).ConfigureAwait(false);
            if (coldMs >= 0)
            {
                cold.Add(coldMs);
                all.Add(coldMs);
            }
        }

        if (all.Count == 0)
        {
            return Unreachable(name, primary, secondary, "Резолвер не ответил ни на один валидный запрос.");
        }

        double? warmAvg = warm.Count > 0 ? warm.Average() : null;
        double? coldAvg = cold.Count > 0 ? cold.Average() : null;
        double[] sorted = all.OrderBy(x => x).ToArray();
        double p95 = StatisticalMetricsCalculator.Percentile(sorted, 0.95);

        // Доля успеха — от фактически отправленных запросов.
        double successRate = sent == 0 ? 0 : Math.Clamp((double)all.Count / sent * 100.0, 0.0, 100.0);

        double composite = ComputeCompositeScore(warmAvg, coldAvg, p95, successRate, hijacked);

        string note = hijacked
            ? "Обнаружен перехват DNS: резолвер отвечает на несуществующие имена. Результаты бенчмарка могут быть некорректны."
            : "Ответы валидированы по ID запроса, имени вопроса и адресу источника.";

        return new DnsResolverBenchmarkResult(
            name, primary, secondary, true, sent, all.Count,
            Round(warmAvg), Round(coldAvg), Round(p95),
            Math.Round(successRate, 1, MidpointRounding.AwayFromZero), Math.Round(composite, 2, MidpointRounding.AwayFromZero), hijacked, note);
    }

    /// <summary>Веса вынесены явно: меньше score — лучше. Штраф за перехват — 500 очков.</summary>
    public static double ComputeCompositeScore(double? warmAvgMs, double? coldAvgMs, double p95Ms, double successRatePercent, bool answersLookHijacked)
    {
        double warm = warmAvgMs ?? 250.0;
        double cold = coldAvgMs ?? 250.0;
        double score = (0.55 * warm) + (0.30 * cold) + (0.15 * p95Ms) + ((100.0 - successRatePercent) * 5.0);

        return answersLookHijacked ? score + 500.0 : score;
    }

    private async Task<double> MeasureAsync(IPAddress server, string domain, CancellationToken ct)
    {
        ushort id = _idGenerator();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DnsRawResponse? response = await _transport.QueryAsync(server, domain, id, _timeout, ct).ConfigureAwait(false);
        sw.Stop();

        return IsValid(response, server, id, domain) ? sw.Elapsed.TotalMilliseconds : -1.0;
    }

    private async Task<DnsRawResponse?> SendValidatedAsync(IPAddress server, string domain, CancellationToken ct)
    {
        ushort id = _idGenerator();
        DnsRawResponse? response = await _transport.QueryAsync(server, domain, id, _timeout, ct).ConfigureAwait(false);
        return IsValid(response, server, id, domain) ? response : null;
    }

    /// <summary>Ответ принимается только при совпадении ID, имени вопроса и адреса источника.</summary>
    public static bool IsValid(DnsRawResponse? response, IPAddress expectedServer, ushort expectedTransactionId, string expectedQuestion)
    {
        if (response is null || !response.IsResponse)
        {
            return false;
        }

        if (response.TransactionId != expectedTransactionId)
        {
            return false;
        }

        if (!response.Source.Address.Equals(expectedServer))
        {
            return false;
        }

        string expected = expectedQuestion.TrimEnd('.');
        string actual = (response.QuestionName ?? string.Empty).TrimEnd('.');

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static DnsResolverBenchmarkResult Unreachable(string name, string primary, string secondary, string reason) =>
        new(name, primary, secondary, false, 0, 0, null, null, null, 0, double.PositiveInfinity, false, reason);

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);

}
