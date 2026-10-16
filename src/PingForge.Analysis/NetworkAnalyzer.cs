using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;

namespace PingForge.Analysis;

public sealed record NetworkAnalysisReport(
    IReadOnlyList<InterferenceWarning> Warnings,
    IReadOnlyList<TweakRecommendationItem> Recommendations,
    IReadOnlyList<string> SkippedChecks,
    IReadOnlyList<string> Notes,
    string SummaryRu)
{
    public static IReadOnlyList<string> WarningCodes(params InterferenceWarning[] warnings) =>
        warnings.Select(w => w.Code).ToList();
}

/// <summary>
/// Сам анализ сети: находит то, что мешает, и решает, какие твики имеет смысл предложить.
/// Правила: ни одного предложения без замера, ни одного предупреждения без объяснения,
/// и обязательная пометка о том, чего эти правки НЕ сделают (пинг внутри боя они не снижают).
/// </summary>
public sealed class NetworkAnalyzer
{
    public const string WarningTunnelActive = "TUNNEL_ACTIVE";
    public const string WarningMultiplePaths = "MULTIPLE_ACTIVE_PATHS";
    public const string WarningContextIncomplete = "CONTEXT_INCOMPLETE";

    private readonly IMtuAdviceProvider _mtuAdvice;
    private readonly IDnsAdviceProvider _dnsAdvice;
    private readonly INetworkContextReader _context;
    private readonly ITweakCatalog? _catalog;

    public NetworkAnalyzer(
        IMtuAdviceProvider mtuAdvice,
        IDnsAdviceProvider dnsAdvice,
        INetworkContextReader context,
        ITweakCatalog? catalog = null)
    {
        _mtuAdvice = mtuAdvice ?? throw new ArgumentNullException(nameof(mtuAdvice));
        _dnsAdvice = dnsAdvice ?? throw new ArgumentNullException(nameof(dnsAdvice));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _catalog = catalog;
    }

    public async Task<NetworkAnalysisReport> AnalyzeAsync(TweakTarget activeAdapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activeAdapter);

        NetworkContextSnapshot context = await _context.ReadAsync(ct).ConfigureAwait(false);

        var warnings = new List<InterferenceWarning>();
        var recommendations = new List<TweakRecommendationItem>();
        var skipped = new List<string>();
        var notes = new List<string>();

        IReadOnlyList<AdapterInventoryItem> physical = context.PhysicalUpAdapters;
        IReadOnlyList<AdapterInventoryItem> tunnels = context.ActiveVpnAdapters;

        if (tunnels.Count > 0 && physical.Count > 0)
        {
            warnings.Add(new InterferenceWarning(
                WarningTunnelActive,
                "Активен VPN или туннель",
                $"Подняты туннельные адаптеры: {string.Join(", ", tunnels.Select(t => t.Name))}. " +
                "Если игровой трафик идёт через них, маршрут получается длиннее, и оптимизация внутри туннеля " +
                "почти ничего не даст.",
                "Если VPN не нужен для самой игры — выключите его на время матча. Если нужен (например, из-за блокировок), " +
                "учтите: пинг будет определяться туннелем, а не настройками Windows.",
                TweakRiskLevel.Medium));
        }

        if (physical.Count > 1)
        {
            warnings.Add(new InterferenceWarning(
                WarningMultiplePaths,
                "Несколько активных физических подключений",
                $"Подняты и имеют шлюз: {string.Join(", ", physical.Select(p => p.Name))}. " +
                "Приложение оптимизирует только тот адаптер, через который реально идёт трафик игры.",
                "Проверьте, какое подключение сейчас основное: маршрут по умолчанию определяет, куда пойдут пакеты. " +
                "Если игровой трафик идёт через Wi-Fi вместо кабеля — лучше переключиться на кабель.",
                TweakRiskLevel.Low));
        }

        if (context.UnresolvedFields.Count > 0)
        {
            warnings.Add(new InterferenceWarning(
                WarningContextIncomplete,
                "Часть данных о сети прочитать не удалось",
                string.Join(" ", context.UnresolvedFields),
                "Эти пункты анализа пропущены. Приложение не подставляет значения по умолчанию вместо непрочитанных: " +
                "пропуск честнее выдумки.",
                TweakRiskLevel.Low));
        }

        ComputedTargetValue mtu = await _mtuAdvice.GetRecommendedMtuAsync(activeAdapter, ct).ConfigureAwait(false);
        AddRecommendationOrSkip(
            mtu,
            "MTU_SUBINTERFACE_OPTIMAL",
            mtu.Value,
            $"Сейчас: {(context.ActiveAdapterMtuBytes is { } currentMtu ? currentMtu + " байт" : "не прочитано")}",
            recommendations,
            skipped,
            notes);

        ComputedTargetValue dns = await _dnsAdvice.GetRecommendedDnsAsync(activeAdapter, ct).ConfigureAwait(false);
        string currentDns = context.ConfiguredDnsServers.Count > 0
            ? string.Join("|", context.ConfiguredDnsServers)
            : "не прочитано";

        if (dns.IsAvailable
            && context.ConfiguredDnsServers.Count > 0
            && TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.MultiString, dns.Value, currentDns))
        {
            skipped.Add("DNS-серверы: уже используются рекомендованные значения — менять нечего.");
        }
        else
        {
            AddRecommendationOrSkip(
                dns,
                "DNS_SERVERS_FASTEST",
                dns.Value,
                $"Сейчас: {currentDns}",
                recommendations,
                skipped,
                notes);
        }

        notes.Add("Эти правки убирают лишние задержки и потери на вашей стороне. Пинг внутри боя они не снижают: " +
                  "скорость света и маршрут провайдера от настроек Windows не зависят.");

        string summary =
            $"Проверок выполнено: 4. Предупреждений: {warnings.Count}. Предлагается изменить: {recommendations.Count}. " +
            $"Пропущено проверок: {skipped.Count}.";

        return new NetworkAnalysisReport(warnings, recommendations, skipped, notes, summary);
    }

    /// <summary>
    /// Совет попадает в предложения только если значение реально посчитано. Иначе он честно уходит
    /// в раздел «пропущено» вместе с причиной — без этого пользователь считал бы, что «всё проверено».
    /// </summary>
    private void AddRecommendationOrSkip(
        ComputedTargetValue advice,
        string tweakCode,
        string recommendedValue,
        string currentValueText,
        List<TweakRecommendationItem> recommendations,
        List<string> skipped,
        List<string> notes)
    {
        if (!advice.IsAvailable)
        {
            skipped.Add($"{tweakCode}: {advice.ExplanationRu}");
            return;
        }

        ITweak? tweak = _catalog?.GetByCode(tweakCode);
        if (tweak is null)
        {
            notes.Add($"{tweakCode}: твик отсутствует в каталоге этой сборки — предложение пропущено.");
            return;
        }

        TweakDescriptor descriptor = tweak.Descriptor;

        recommendations.Add(new TweakRecommendationItem(
            TweakCode: descriptor.Code,
            TitleRu: descriptor.TitleRu,
            Badge: descriptor.Badge,
            RequiresManualVerification: descriptor.RequiresManualVerification,
            CurrentValueDisplay: currentValueText,
            RecommendedValueDisplay: recommendedValue,
            ReasonRu: advice.ExplanationRu,
            Risk: descriptor.Risk));
    }
}
