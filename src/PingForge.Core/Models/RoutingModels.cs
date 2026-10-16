namespace PingForge.Core.Models;

/// <summary>Кандидат маршрута: прямой путь или POP-узел релея.</summary>
public sealed record RouteCandidate(
    string PathId,
    string DisplayName,
    bool IsRelay,
    string? RelayNodeId,
    ProbeStatistics LatestProbe);

/// <summary>
/// Веса формулы скоринга. Меньший score = лучший маршрут. Единицы измерения указаны явно,
/// чтобы значения можно было показать пользователю и объяснить, из чего сложился балл.
/// </summary>
public sealed record RouteScoreWeights(
    double PerRttMs = 1.0,
    double PerJitterMs = 1.5,
    double PerLossPercentSquared = 0.5,
    double PerHop = 0.4,
    double PerNodeLoadPercentOver50 = 0.05,
    double PerExtraTrafficPercent = 0.20)
{
    public static RouteScoreWeights Default { get; } = new();
}

/// <summary>Пороговые значения, после которых маршрут считается непригодным.</summary>
public sealed record RouteEligibilityThresholds(
    double MaxLossPercent = 5.0,
    double MaxJitterMs = 40.0,
    double MaxRttMs = 250.0);

public sealed record RouteScoreResult(
    string PathId,
    double Score,
    bool IsEligible,
    string ExplanationRu,
    IReadOnlyList<string> Penalties)
{
    public static RouteScoreResult NotEligible(string pathId, string reason, IReadOnlyList<string> penalties) =>
        new(pathId, double.PositiveInfinity, false, reason, penalties);
}

/// <summary>Состояние нагрузки POP-узла — приходит из health-истории релея.</summary>
public sealed record RelayNodeHealth(double RttAverageMs, double JitterMs, double LossPercent, double EstimatedLoadPercent);

/// <summary>Что именно нужно изменить в таблице маршрутизации, чтобы поднять выбранный путь.</summary>
public sealed record RouteChangePlan(
    string SessionId,
    string Prefix,
    int InterfaceIndex,
    string NextHop,
    int Metric,
    RouteChangeKind Kind,
    TweakTarget Target)
{
    /// <summary>
    /// Описание обратной операции. Приложение удаляет при аварийном восстановлении ТОЛЬКО те
    /// маршруты, которые само создало (флаг OwnerTag).
    /// </summary>
    public RouteUndoDescriptor ToUndoDescriptor() =>
        new(SessionId, Prefix, InterfaceIndex, NextHop, Metric, Kind, OwnerTag);

    public const string OwnerTag = "PingForge";
}

public sealed record RouteUndoDescriptor(
    string SessionId,
    string Prefix,
    int InterfaceIndex,
    string NextHop,
    int Metric,
    RouteChangeKind Kind,
    string OwnerTag);

/// <summary>Вход для выбора маршрута.</summary>
public sealed record RouteSelectionInput(
    IReadOnlyList<RouteCandidate> Candidates,
    RoutingMode Mode,
    double MaxAcceptedExtraTrafficPercent);

public sealed record RouteSelectionResult(
    bool HasUsableRoute,
    string? SelectedPathId,
    RoutingMode Mode,
    IReadOnlyList<RouteScoreResult> Scores,
    string ExplanationRu)
{
    public static RouteSelectionResult None(RoutingMode mode, IReadOnlyList<RouteScoreResult> scores, string explanation) =>
        new(false, null, mode, scores, explanation);
}

/// <summary>Параметры гистерезиса автофейловера: защита от «дёрганья» маршрута.</summary>
public sealed record FailoverOptions(
    int BadSamplesBeforeSwitch = 5,
    double SwitchMarginPoints = 4.0,
    int GoodSamplesBeforeReturn = 20,
    double DegradationJitterMs = 12.0,
    double DegradationLossPercent = 3.0,
    double RelayWorthwhileThresholdPoints = 1.0)
{
    /// <summary>Период покоя после переключения: за это время обратные переходы запрещены.</summary>
    public TimeSpan MinimumTimeOnRoute { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Кандидат для фейловера. IsRelay нужен, чтобы отличить «переключиться на другой узел»
/// от «вернуться на прямой путь и признать, что релей не помогает».</summary>
public sealed record FailoverCandidate(string PathId, bool IsRelay, double Score);

public sealed record FailoverSample(
    DateTime SampledUtc,
    double ActiveScore,
    double ActiveJitterMs,
    double ActiveLossPercent,
    IReadOnlyList<FailoverCandidate> Candidates);

public sealed record FailoverEvaluation(
    FailoverDecisionKind Decision,
    string? SwitchToPathId,
    string ReasonRu,
    int ConsecutiveBadSamples,
    bool WasBlockedByCooldown);
