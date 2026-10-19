using System.Net;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Probing;
using PingForge.Tweaks;

namespace PingForge.Analysis;

/// <summary>
/// Совет по MTU. Ключевая честность: значение считается DF-пробами до КОНКРЕТНОГО игрового сервера.
/// Если сервер не выбран или ICMP закрыт — совет недоступен, и твик MTU не применяется.
/// «Универсального оптимального MTU из интернета» не существует.
/// </summary>
public sealed class MtuAdviceProvider : IMtuAdviceProvider
{
    private readonly DfMtuDiscoveryService _discovery;
    private readonly IGameServerEndpointProvider _endpoints;
    private readonly INetworkContextReader _context;

    public MtuAdviceProvider(
        DfMtuDiscoveryService discovery,
        IGameServerEndpointProvider endpoints,
        INetworkContextReader context)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<ComputedTargetValue> GetRecommendedMtuAsync(TweakTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        IPAddress? server = await _endpoints.TryGetGameServerAddressAsync(ct).ConfigureAwait(false);
        if (server is null)
        {
            return ComputedTargetValue.Unavailable(
                "Игровой сервер не выбран: MTU подбирается под конкретный путь до конкретного сервера. " +
                "Запустите игру и нажмите «Оптимизировать соединение» — тогда мы сможем его измерить.");
        }

        NetworkContextSnapshot context = await _context.ReadAsync(ct).ConfigureAwait(false);
        if (context.ActiveAdapterMtuBytes is not { } currentMtu)
        {
            return ComputedTargetValue.Unavailable(
                "Текущий MTU активного адаптера прочитать не удалось. Менять значение вслепую нельзя: " +
                "слишком маленький MTU добавит лишнюю фрагментацию, слишком большой — пакеты будут теряться.");
        }

        PathMtuDiscoveryResult result = await _discovery
            .DiscoverAsync(server, currentMtu, minimumMtuBytes: DfMtuDiscoveryService.MinimumIpv4Mtu, ct: ct)
            .ConfigureAwait(false);

        if (result.Outcome != MtuDiscoveryOutcome.OptimalFound || result.OptimalMtuBytes is not { } optimal)
        {
            return ComputedTargetValue.Unavailable(result.ExplanationRu);
        }

        string note = optimal == currentMtu
            ? " Значение совпадает с текущим MTU — менять нечего, твик будет отмечен как «уже стоит»."
            : $" Сейчас стоит {currentMtu} байт, измеренный максимум — {optimal} байт.";

        return new ComputedTargetValue(
            optimal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TweakValueDataType.DWord,
            result.ExplanationRu + note);
    }
}

public interface IDnsBenchmarkRunner
{
    Task<IReadOnlyList<DnsResolverBenchmarkResult>> RunAsync(CancellationToken ct = default);
}

public sealed class DnsResolverBenchmarkRunner : IDnsBenchmarkRunner
{
    private readonly DnsResolverBenchmarker _benchmarker;
    private readonly IReadOnlyList<(string Name, string Primary, string Secondary)>? _candidates;

    /// <param name="candidates">
    /// Какие резолверы мерить. Продукт передаёт сюда GameRouteCatalog.FreeDnsRoutes: список
    /// резолверов один на «Твики», «Проверить сеть» и совет DNS. null — архивный набор бенчмаркера
    /// (нужен только старым вызовам и тестам).
    /// </param>
    public DnsResolverBenchmarkRunner(
        DnsResolverBenchmarker benchmarker,
        IReadOnlyList<(string Name, string Primary, string Secondary)>? candidates = null)
    {
        _benchmarker = benchmarker ?? throw new ArgumentNullException(nameof(benchmarker));
        _candidates = candidates;
    }

    public Task<IReadOnlyList<DnsResolverBenchmarkResult>> RunAsync(CancellationToken ct = default) =>
        _benchmarker.BenchmarkAsync(_candidates, ct);
}

/// <summary>
/// Совет по DNS-серверам. Логика выбора:
///   • резолвер, отвечающий на канареечный запрос в несуществующую зону, исключается даже если он «быстрее всех»;
///   • недостижимые резолверы не предлагаются;
///   • если все отвечающие резолверы подменяют ответы, совет недоступен — текущие настройки безопаснее;
///   • в тексте всегда напоминание, что DNS ускоряет поиск матча и загрузки, но не пинг внутри боя.
/// </summary>
public sealed class DnsAdviceProvider : IDnsAdviceProvider
{
    private readonly IDnsBenchmarkRunner _runner;
    private readonly double _minimalScoreGain;

    public DnsAdviceProvider(IDnsBenchmarkRunner runner, double minimalScoreGain = 0.0)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _minimalScoreGain = minimalScoreGain;
    }

    public async Task<ComputedTargetValue> GetRecommendedDnsAsync(TweakTarget target, CancellationToken ct = default)
    {
        IReadOnlyList<DnsResolverBenchmarkResult> results = await _runner.RunAsync(ct).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return ComputedTargetValue.Unavailable(
                "Бенчмарк DNS не выполнен: список серверов пуст. Сначала нажмите «Оптимизировать соединение».");
        }

        List<DnsResolverBenchmarkResult> answered = results
            .Where(r => r.IsReachable && r.QueriesAnswered > 0)
            .ToList();

        List<DnsResolverBenchmarkResult> hijacked = answered.Where(r => r.AnswersLookHijacked).ToList();
        List<DnsResolverBenchmarkResult> clean = answered
            .Where(r => !r.AnswersLookHijacked)
            .OrderBy(r => r.CompositeScore)
            .ToList();

        if (clean.Count == 0)
        {
            string reason = hijacked.Count > 0
                ? "Все ответившие DNS-серверы перехватывают ответы (отвечают на несуществующие имена). " +
                  "Оставляем текущие настройки: предлагать перехватчик было бы вредно."
                : "Ни один публичный DNS-сервер не ответил — менять настройки нечем.";

            return ComputedTargetValue.Unavailable(reason);
        }

        DnsResolverBenchmarkResult winner = clean[0];
        DnsResolverBenchmarkResult? runnerUp = clean.Count > 1 ? clean[1] : null;

        if (runnerUp is not null && winner.CompositeScore + _minimalScoreGain > runnerUp.CompositeScore)
        {
            return ComputedTargetValue.Unavailable(
                $"Выигрыша нет: лидер {winner.ResolverName} (балл {winner.CompositeScore:F2}) и второй " +
                $"{runnerUp.ResolverName} (балл {runnerUp.CompositeScore:F2}) идут вровень. Смена DNS ничего не даст.");
        }

        string value = string.IsNullOrWhiteSpace(winner.SecondaryIp)
            ? winner.PrimaryIp
            : $"{winner.PrimaryIp}|{winner.SecondaryIp}";

        string warm = winner.WarmCacheAvgMs is { } warmMs ? $"{warmMs:F1} мс" : "нет данных";
        string hijackNote = hijacked.Count > 0
            ? $" Отброшено резолверов с признаком перехвата: {hijacked.Count} ({string.Join(", ", hijacked.Select(h => h.ResolverName))})."
            : " Признаков перехвата DNS не найдено.";

        return new ComputedTargetValue(
            value,
            TweakValueDataType.MultiString,
            $"Бенчмарк DNS: победил {winner.ResolverName} — отклик {warm}, успешных ответов " +
            $"{winner.QueriesAnswered} из {winner.QueriesSent}, балл {winner.CompositeScore:F2}.{hijackNote} " +
            "Напоминание: DNS ускоряет поиск матча и загрузки, но не снижает пинг внутри боя.");
    }
}
