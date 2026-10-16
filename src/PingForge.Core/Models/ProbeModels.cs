namespace PingForge.Core.Models;

/// <summary>Хоп маршрута. ASN заполняется только если доступна офлайн-база, иначе — «не определён».</summary>
public sealed record RouteHopMetric(
    int HopNumber,
    string IpAddress,
    double? RttMs,
    bool IsTimedOut,
    string AsnLabel = AsnLabels.Unknown,
    string OrganisationLabel = "")
{
    public static RouteHopMetric Timeout(int hop) =>
        new(hop, "*", null, true, AsnLabels.Unknown, "Хоп не ответил");
}

public static class AsnLabels
{
    /// <summary>
    /// Честная метка: у нас нет базы ASN, поэтому мы НЕ придумываем «магистральный узел».
    /// </summary>
    public const string Unknown = "н/д";

    public const string Private = "приватный диапазон";
}

/// <summary>
/// Статистика замеров. Все метрики nullable: если измерить не удалось, числа остаются null,
/// а не превращаются в «100 % потерь» и «стабильность 0».
/// </summary>
public sealed record ProbeStatistics(
    string TargetEndpoint,
    string RouteLabel,
    bool IsMeasurable,
    ProbeMethodUsed MethodUsed,
    ProbeFailureCause FailureCause,
    string StatusMessageRu,
    int SentCount,
    int ReceivedCount,
    double? RttMinMs,
    double? RttAvgMs,
    double? RttMaxMs,
    double? RttMedianMs,
    double? RttP95Ms,
    double? RttP99Ms,
    double? JitterMadMs,
    double? PacketLossPercent,
    int OutlierSpikesCount,
    double? StabilityScore0To100,
    bool IsPreliminary,
    IReadOnlyList<RouteHopMetric> Hops,
    DateTime MeasuredAtUtc)
{
    public static ProbeStatistics Unmeasurable(
        string target, string routeLabel, ProbeFailureCause cause, int sentCount, string message, IReadOnlyList<RouteHopMetric>? hops = null) =>
        new(target, routeLabel, false, ProbeMethodUsed.Unmeasurable, cause, message,
            sentCount, 0, null, null, null, null, null, null, null, null, 0, null, false,
            hops ?? Array.Empty<RouteHopMetric>(), DateTime.UtcNow);

    /// <summary>Веса формулы стабильности — вынесены, чтобы UI и отчёт могли их показать.</summary>
    public static class StabilityWeights
    {
        public const double PerLossPercent = 4.5;
        public const double PerJitterMs = 2.2;
        public const double PerOutlierPercent = 1.5;
    }
}

public sealed record DnsResolverBenchmarkResult(
    string ResolverName,
    string PrimaryIp,
    string SecondaryIp,
    bool IsReachable,
    int QueriesSent,
    int QueriesAnswered,
    double? WarmCacheAvgMs,
    double? ColdCacheAvgMs,
    double? P95Ms,
    double SuccessRatePercent,
    double CompositeScore,
    bool AnswersLookHijacked,
    string NoteRu);

public sealed record PathMtuDiscoveryResult(
    MtuDiscoveryOutcome Outcome,
    int TestedFromMtuBytes,
    int TestedToMtuBytes,
    int? OptimalMtuBytes,
    int? MaxUnfragmentedPayloadBytes,
    string ExplanationRu)
{
    /// <summary>MTU, который можно применить. null — применять нечего (и это честный ответ).</summary>
    public int? ApplicableMtuBytes => Outcome == MtuDiscoveryOutcome.OptimalFound ? OptimalMtuBytes : null;
}

public sealed record InterferenceWarning(
    string Code,
    string TitleRu,
    string DescriptionRu,
    string RemediationRu,
    TweakRiskLevel Severity);

public sealed record TweakRecommendationItem(
    string TweakCode,
    string TitleRu,
    GameRecommendationBadge Badge,
    bool RequiresManualVerification,
    string CurrentValueDisplay,
    string RecommendedValueDisplay,
    string ReasonRu,
    TweakRiskLevel Risk);

public sealed record NetworkAndOsSnapshot(
    string WindowsEditionAndBuild,
    bool IsRunningAsAdministrator,
    string ActiveAdapterName,
    string ActiveAdapterInterfaceGuid,
    string ActiveAdapterDescription,
    string? ActiveAdapterDriverVersion,
    string ConnectionMedium,
    int? LinkSpeedMbps,
    int? CurrentMtuBytes,
    string? ActivePowerSchemeGuid,
    string ActivePowerSchemeName,
    string? TcpAutoTuningLevel,
    string? CongestionProvider,
    bool? IsRssEnabled,
    bool? IsRscEnabled,
    bool? IsEcnEnabled,
    bool? IsNagleDisabledOnActiveNic,
    bool? IsGameModeEnabled,
    bool? IsDeliveryOptimizationP2pEnabled,
    bool? IsSystemRestoreEnabledOnSystemDrive,
    IReadOnlyList<string> ConfiguredDnsServers,
    IReadOnlyList<string> AttachedNdisFilters,
    IReadOnlyList<string> UnresolvedFields,
    IReadOnlyList<InterferenceWarning> Warnings,
    IReadOnlyList<TweakRecommendationItem> AlreadyOptimalItems,
    IReadOnlyList<TweakRecommendationItem> RecommendedToChangeItems,
    IReadOnlyList<TweakRecommendationItem> DoNotTouchItems,
    IReadOnlyList<string> AnalysisNotes)
{
    /// <summary>
    /// Признак того, что сканер где-то не смог получить данные. UI обязан показать это серым,
    /// а отчёт — перечислить поля в разделе «не удалось определить».
    /// </summary>
    public bool IsPartiallyIncomplete => UnresolvedFields.Count > 0;
}
