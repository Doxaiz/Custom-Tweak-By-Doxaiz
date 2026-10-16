using PingForge.Core.Models;
using PingForge.Routing;
using Xunit;

namespace PingForge.Tests;

internal static class ProbeFactory
{
    public static ProbeStatistics Measurable(
        double rttAvg,
        double jitter = 0,
        double loss = 0,
        int hopCount = 1,
        ProbeMethodUsed method = ProbeMethodUsed.ActiveTcpHandshake)
    {
        var hops = Enumerable.Range(1, hopCount).Select(i => new RouteHopMetric(i, $"10.0.0.{i}", 1.0, false)).ToList();

        return new ProbeStatistics(
            "1.2.3.4:27015", "test", true, method, ProbeFailureCause.None, "ok",
            30, 30, rttAvg, rttAvg, rttAvg, rttAvg, rttAvg, rttAvg, jitter, loss, 0, 95.0, false, hops, DateTime.UtcNow);
    }

    public static ProbeStatistics Unmeasurable() =>
        ProbeStatistics.Unmeasurable("1.2.3.4:27015", "test", ProbeFailureCause.AllProbesTimedOut, 30, "нет ответа");
}

public sealed class RouteScorerTests
{
    [Fact]
    public void Score_IsSumOfWeightedComponents()
    {
        var candidate = new RouteCandidate("direct", "Прямое подключение", false, null, ProbeFactory.Measurable(rttAvg: 20, jitter: 2, hopCount: 3));

        RouteScoreResult result = new RouteScorer().Score(candidate);

        // 20 × 1.0 + 2 × 1.5 + 0² × 0.5 + (3−1) × 0.4 = 23.8
        Assert.True(result.IsEligible);
        Assert.Equal(23.8, result.Score, 2);
        Assert.Contains("RTT", result.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public void LossIsPenalisedQuadratically()
    {
        var lowLoss = new RouteCandidate("a", "A", false, null, ProbeFactory.Measurable(rttAvg: 20, loss: 2));
        var highLoss = new RouteCandidate("b", "B", false, null, ProbeFactory.Measurable(rttAvg: 20, loss: 4));

        double low = new RouteScorer().Score(lowLoss).Score;
        double high = new RouteScorer().Score(highLoss).Score;

        // Δ = (16 − 4) × 0.5 = 6 баллов, а не 2 × 0.5 = 1
        Assert.Equal(6.0, high - low, 2);
    }

    [Fact]
    public void RouteWithExcessiveLoss_IsNotEligible()
    {
        var candidate = new RouteCandidate("bad", "Плохой путь", true, "node-1", ProbeFactory.Measurable(rttAvg: 25, loss: 12));

        RouteScoreResult result = new RouteScorer().Score(candidate);

        Assert.False(result.IsEligible);
        Assert.Contains("потери", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnmeasurableRoute_IsNotEligible()
    {
        var candidate = new RouteCandidate("unknown", "Неизмеримый путь", false, null, ProbeFactory.Unmeasurable());

        RouteScoreResult result = new RouteScorer().Score(candidate);

        Assert.False(result.IsEligible);
        Assert.Contains("измерить", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelayLoadAbove50Percent_AddsPenalty()
    {
        var candidate = new RouteCandidate("relay", "Релей", true, "node-1", ProbeFactory.Measurable(rttAvg: 20));

        double withoutLoad = new RouteScorer().Score(candidate).Score;
        double withLoad = new RouteScorer().Score(candidate, new RelayNodeHealth(20, 1, 0, 70)).Score;

        Assert.Equal(1.0, withLoad - withoutLoad, 2); // (70 − 50) × 0.05
    }
}

public sealed class RouteSelectorTests
{
    private static RouteCandidate Direct(double rtt) =>
        new("direct", "Прямое подключение", false, null, ProbeFactory.Measurable(rtt));

    private static RouteCandidate Relay(double rtt, string id = "node-1") =>
        new($"relay:{id}", $"Релей {id}", true, id, ProbeFactory.Measurable(rtt));

    [Fact]
    public void PicksRelayWhenItClearlyWins()
    {
        var input = new RouteSelectionInput(new[] { Direct(60), Relay(20) }, RoutingMode.AutoFailover, 100);

        RouteSelectionResult result = new RouteSelector().Select(input);

        Assert.True(result.HasUsableRoute);
        Assert.Equal("relay:node-1", result.SelectedPathId);
        Assert.Contains("выигрыш", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsDirectWhenRelayGainIsWithinMeasurementNoise()
    {
        var input = new RouteSelectionInput(new[] { Direct(20), Relay(19.5) }, RoutingMode.AutoFailover, 100);

        RouteSelectionResult result = new RouteSelector().Select(input);

        Assert.Equal("direct", result.SelectedPathId);
        Assert.Contains("погрешности", result.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectOnlyMode_IgnoresRelayEvenWhenFaster()
    {
        var input = new RouteSelectionInput(new[] { Direct(60), Relay(20) }, RoutingMode.DirectOnly, 100);

        RouteSelectionResult result = new RouteSelector().Select(input);

        Assert.Equal("direct", result.SelectedPathId);
    }

    [Fact]
    public void WhenAllRoutesFailThresholds_ReportsHonestly()
    {
        var input = new RouteSelectionInput(
            new[]
            {
                new RouteCandidate("direct", "Прямое", false, null, ProbeFactory.Measurable(20, loss: 30)),
                new RouteCandidate("relay:1", "Релей", true, "1", ProbeFactory.Measurable(20, jitter: 90))
            },
            RoutingMode.AutoFailover, 100);

        RouteSelectionResult result = new RouteSelector().Select(input);

        Assert.False(result.HasUsableRoute);
        Assert.Null(result.SelectedPathId);
    }

    [Fact]
    public void FecMode_AppliesTrafficPenaltyOnlyWhenWithinUserLimit()
    {
        var candidates = new[] { Direct(25), Relay(20) };

        RouteSelectionResult allowed = new RouteSelector().Select(
            new RouteSelectionInput(candidates, RoutingMode.MultipathRedundantFec, MaxAcceptedExtraTrafficPercent: 100));

        RouteSelectionResult denied = new RouteSelector().Select(
            new RouteSelectionInput(candidates, RoutingMode.MultipathRedundantFec, MaxAcceptedExtraTrafficPercent: 50));

        // Дублирование стоит 100 × 0.2 = 20 баллов, поэтому релей при запрете выгоднее.
        Assert.True(allowed.Scores.First(s => s.PathId == "relay:node-1").Score > denied.Scores.First(s => s.PathId == "relay:node-1").Score);
        Assert.Contains("увеличит трафик", allowed.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RouteChangePlannerTests
{
    [Fact]
    public void RemovePlan_RefusesRoutesNotCreatedByPingForge()
    {
        var foreign = new RouteUndoDescriptor("s1", "10.0.0.0/8", 5, "0.0.0.0", 1, RouteChangeKind.Add, "OtherApp");

        Assert.Throws<InvalidOperationException>(() => new RouteChangePlanner().BuildRemovePlan(foreign));
    }

    [Fact]
    public void ResidualRoutes_DetectsLeaksAfterStop()
    {
        var planner = new RouteChangePlanner();
        IReadOnlyList<RouteChangePlan> plans = planner.BuildAddPlans("s1",
            new[] { ("203.0.113.7/32", 12, "10.0.0.1", 5), ("203.0.113.8/32", 12, "10.0.0.1", 5) });

        var undo = plans.Select(p => p.ToUndoDescriptor()).ToList();

        // Маршрут 203.0.113.7 остался в таблице — это утечка.
        IReadOnlyList<string> residual = planner.FindResidualRoutes(undo, new[] { "0.0.0.0/0", "203.0.113.7/32" });

        Assert.Single(residual);
        Assert.Equal("203.0.113.7/32", residual[0]);
    }

    [Fact]
    public void UndoDescriptor_KeepsOwnerTagAndExactTarget()
    {
        RouteChangePlan plan = new RouteChangePlanner()
            .BuildAddPlans("session-42", new[] { ("198.51.100.5/32", 7, "192.168.1.1", 3) })
            .Single();

        RouteUndoDescriptor undo = plan.ToUndoDescriptor();

        Assert.Equal(RouteChangePlan.OwnerTag, undo.OwnerTag);
        Assert.Equal(7, undo.InterfaceIndex);
        Assert.Equal("192.168.1.1", undo.NextHop);
        Assert.Equal(3, undo.Metric);
    }
}

public sealed class FailoverControllerTests
{
    private static FailoverCandidate Relay(string nodeId, double score) => new($"relay:{nodeId}", true, score);

    private static FailoverCandidate Direct(double score) => new("direct", false, score);

    // Внимание: позиционные аргументы после именованного в C# биндятся по порядку параметров,
    // поэтому кандидаты идут сразу после активного score, а jitter/loss — именованными.
    private static FailoverSample Sample(double activeScore, FailoverCandidate candidate, double jitter = 1, double loss = 0) =>
        new(DateTime.UtcNow, activeScore, jitter, loss, new[] { candidate });

    private static FailoverSample Sample(double activeScore, FailoverCandidate first, FailoverCandidate second, double jitter = 1, double loss = 0) =>
        new(DateTime.UtcNow, activeScore, jitter, loss, new[] { first, second });

    [Fact]
    public void SingleBadSample_DoesNotTriggerSwitch()
    {
        var controller = new FailoverController();
        FailoverEvaluation evaluation = controller.Evaluate(Sample(30, Relay("1", 10), jitter: 20));

        Assert.Equal(FailoverDecisionKind.StayOnActive, evaluation.Decision);
        Assert.Null(evaluation.SwitchToPathId);
    }

    [Fact]
    public void FiveConsecutiveBadSamples_WithSufficientMargin_TriggerSwitch()
    {
        var controller = new FailoverController();

        FailoverEvaluation last = default!;
        for (int i = 0; i < 5; i++)
        {
            last = controller.Evaluate(Sample(30, Relay("1", 10), jitter: 20, loss: 4));
        }

        Assert.Equal(FailoverDecisionKind.SwitchToCandidate, last.Decision);
        Assert.Equal("relay:1", last.SwitchToPathId);
    }

    [Fact]
    public void DegradationWithoutMargin_DoesNotSwitch()
    {
        var controller = new FailoverController();

        FailoverEvaluation last = default!;
        for (int i = 0; i < 10; i++)
        {
            last = controller.Evaluate(Sample(30, Relay("1", 28), jitter: 20, loss: 4));
        }

        Assert.Equal(FailoverDecisionKind.StayOnActive, last.Decision);
        Assert.Contains("пороге", last.ReasonRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OscillatingMetrics_NeverTriggerSwitch()
    {
        var controller = new FailoverController();

        for (int i = 0; i < 200; i++)
        {
            double jitter = i % 2 == 0 ? 1.0 : 25.0;
            FailoverEvaluation evaluation = controller.Evaluate(
                Sample(30, Relay("1", 10), jitter: jitter, loss: i % 2 == 0 ? 0 : 6));

            Assert.NotEqual(FailoverDecisionKind.SwitchToCandidate, evaluation.Decision);
        }
    }

    [Fact]
    public void CooldownBlocksSwitchRightAfterFailover()
    {
        var controller = new FailoverController();
        controller.NotifySwitchApplied("relay:1", isRelay: true, DateTime.UtcNow);

        FailoverEvaluation evaluation = controller.Evaluate(Sample(40, Direct(5), jitter: 30, loss: 8));

        Assert.True(evaluation.WasBlockedByCooldown);
        Assert.Equal(FailoverDecisionKind.StayOnActive, evaluation.Decision);
    }

    [Fact]
    public void RelayBetterThanDirect_SwitchesToAnotherRelayNode()
    {
        var options = new FailoverOptions(BadSamplesBeforeSwitch: 5) { MinimumTimeOnRoute = TimeSpan.FromMinutes(1) };
        var controller = new FailoverController(options);
        controller.NotifySwitchApplied("relay:1", isRelay: true, DateTime.UtcNow.AddMinutes(-5));

        FailoverEvaluation last = default!;
        for (int i = 0; i < 5; i++)
        {
            last = controller.Evaluate(Sample(60, Relay("2", 20), Direct(35), jitter: 25, loss: 6));
        }

        // Активный узел релея деградировал, другой узел заметно лучше -> переключаемся, а не выключаем релей.
        Assert.Equal(FailoverDecisionKind.SwitchToCandidate, last.Decision);
        Assert.Equal("relay:2", last.SwitchToPathId);
    }

    [Fact]
    public void RelayConsistentlyWorseThanDirect_RecommendsDisablingRelay()
    {
        var options = new FailoverOptions(BadSamplesBeforeSwitch: 5, GoodSamplesBeforeReturn: 3) { MinimumTimeOnRoute = TimeSpan.Zero };
        var controller = new FailoverController(options);
        controller.NotifySwitchApplied("relay:1", isRelay: true, DateTime.UtcNow);

        FailoverEvaluation last = default!;
        for (int i = 0; i < 3; i++)
        {
            last = controller.Evaluate(Sample(30, Direct(20)));
        }

        Assert.Equal(FailoverDecisionKind.RecommendDisableRelay, last.Decision);
        Assert.Equal("direct", last.SwitchToPathId);
        Assert.Contains("отключить", last.ReasonRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoCandidates_ReportsInsufficientData()
    {
        var controller = new FailoverController();
        FailoverEvaluation evaluation = controller.Evaluate(
            new FailoverSample(DateTime.UtcNow, 20, 1, 0, Array.Empty<FailoverCandidate>()));

        Assert.Equal(FailoverDecisionKind.InsufficientData, evaluation.Decision);
    }
}
