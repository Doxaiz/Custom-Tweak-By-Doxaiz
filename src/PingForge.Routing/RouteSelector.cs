using PingForge.Core.Models;

namespace PingForge.Routing;

/// <summary>
/// Выбор маршрута из кандидатов с честным выводом: если релей не даёт выигрыша,
/// приложение обязано это сказать, а не «оптимизировать» ради красивой надписи.
/// </summary>
public sealed class RouteSelector
{
    private readonly RouteScorer _scorer;
    private readonly double _relayWorthwhileThresholdPoints;

    public RouteSelector(RouteScorer? scorer = null, double relayWorthwhileThresholdPoints = 1.0)
    {
        _scorer = scorer ?? new RouteScorer();
        _relayWorthwhileThresholdPoints = relayWorthwhileThresholdPoints;
    }

    public RouteSelectionResult Select(
        RouteSelectionInput input,
        IReadOnlyDictionary<string, RelayNodeHealth>? relayHealth = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Candidates.Count == 0)
        {
            return RouteSelectionResult.None(input.Mode, Array.Empty<RouteScoreResult>(), "Нет ни одного кандидата маршрута.");
        }

        double extraTraffic = input.Mode == RoutingMode.MultipathRedundantFec ? 100.0 : 0.0;
        if (extraTraffic > input.MaxAcceptedExtraTrafficPercent)
        {
            extraTraffic = 0.0;
        }

        var scores = new List<RouteScoreResult>(input.Candidates.Count);
        foreach (RouteCandidate candidate in input.Candidates)
        {
            RelayNodeHealth? health = null;
            if (candidate.IsRelay && candidate.RelayNodeId is not null && relayHealth is not null)
            {
                relayHealth.TryGetValue(candidate.RelayNodeId, out health);
            }

            scores.Add(_scorer.Score(candidate, health, candidate.IsRelay ? extraTraffic : 0.0));
        }

        RouteCandidate? direct = input.Candidates.FirstOrDefault(c => !c.IsRelay);
        RouteScoreResult? directScore = direct is null ? null : scores.First(s => s.PathId == direct.PathId);

        if (input.Mode == RoutingMode.DirectOnly)
        {
            return direct is not null && directScore is { IsEligible: true }
                ? new RouteSelectionResult(true, direct.PathId, input.Mode, scores, "Режим «только напрямую»: используется прямое подключение.")
                : RouteSelectionResult.None(input.Mode, scores, "Прямой путь не прошёл проверку пригодности, а режим «только напрямую» запрещает релей.");
        }

        RouteScoreResult? best = scores
            .Where(s => s.IsEligible)
            .OrderBy(s => s.Score)
            .FirstOrDefault();

        if (best is null)
        {
            return RouteSelectionResult.None(input.Mode, scores,
                "Ни один маршрут не прошёл проверку пригодности: все кандидаты превысили допустимые задержки или потери.");
        }

        RouteCandidate bestCandidate = input.Candidates.First(c => c.PathId == best.PathId);

        // Прямой путь уже лучший — релей не нужен.
        if (!bestCandidate.IsRelay)
        {
            return new RouteSelectionResult(true, best.PathId, input.Mode, scores,
                extraTraffic > 0
                    ? $"Прямое подключение оказалось лучшим даже с учётом дублирования пакетов. " +
                      $"Дублирование (FEC) увеличит трафик примерно на {extraTraffic:F0} %, поэтому релей не используется."
                    : "Прямое подключение оказалось лучшим. Релей не даёт выигрыша на этом маршруте.");
        }

        if (directScore is { IsEligible: true })
        {
            double improvement = directScore.Score - best.Score;
            if (improvement < _relayWorthwhileThresholdPoints)
            {
                return new RouteSelectionResult(true, direct!.PathId, input.Mode, scores,
                    $"Релей даёт выигрыш всего {improvement:F2} балла (порог {_relayWorthwhileThresholdPoints:F2}). " +
                    "Рекомендуем остаться на прямом подключении: выигрыш в пределах погрешности измерения.");
            }

            return new RouteSelectionResult(true, best.PathId, input.Mode, scores,
                $"Выбран релей «{bestCandidate.DisplayName}»: выигрыш {improvement:F2} балла относительно прямого пути. " +
                $"Разбор: {best.ExplanationRu}" +
                (extraTraffic > 0 ? $" Включено дублирование пакетов: трафик вырастет примерно на {extraTraffic:F0} %." : string.Empty));
        }

        return new RouteSelectionResult(true, best.PathId, input.Mode, scores,
            $"Прямой путь непригоден, выбран релей «{bestCandidate.DisplayName}». {best.ExplanationRu}");
    }
}

/// <summary>
/// Планировщик изменений таблицы маршрутизации. Каждое изменение снабжается undo-дескриптором,
/// поэтому при аварийном восстановлении приложение удалит ТОЛЬКО свои маршруты.
/// </summary>
public sealed class RouteChangePlanner
{
    public IReadOnlyList<RouteChangePlan> BuildAddPlans(string sessionId, IEnumerable<(string Prefix, int InterfaceIndex, string NextHop, int Metric)> routes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(routes);

        return routes
            .Select(r => new RouteChangePlan(
                sessionId,
                r.Prefix,
                r.InterfaceIndex,
                r.NextHop,
                r.Metric,
                RouteChangeKind.Add,
                TweakTarget.Global($"route:{r.Prefix}")))
            .ToList();
    }

    /// <summary>Планы снятия маршрута на основе ранее сохранённого undo-дескриптора.</summary>
    public RouteChangePlan BuildRemovePlan(RouteUndoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!string.Equals(descriptor.OwnerTag, RouteChangePlan.OwnerTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Маршрут {descriptor.Prefix} создан не PingForge (метка «{descriptor.OwnerTag}»). Удалять его запрещено.");
        }

        return new RouteChangePlan(
            descriptor.SessionId,
            descriptor.Prefix,
            descriptor.InterfaceIndex,
            descriptor.NextHop,
            descriptor.Metric,
            RouteChangeKind.Delete,
            TweakTarget.Global($"route:{descriptor.Prefix}"));
    }

    /// <summary>
    /// Проверка «чистого выхода»: возвращает маршруты, которые PingForge добавил, но не снял.
    /// Используется тестом «после нажатия Стоп разница route print пустая».
    /// </summary>
    public IReadOnlyList<string> FindResidualRoutes(
        IEnumerable<RouteUndoDescriptor> createdByPingForge,
        IEnumerable<string> currentRoutePrefixes)
    {
        ArgumentNullException.ThrowIfNull(createdByPingForge);
        ArgumentNullException.ThrowIfNull(currentRoutePrefixes);

        var current = currentRoutePrefixes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return createdByPingForge
            .Where(d => string.Equals(d.OwnerTag, RouteChangePlan.OwnerTag, StringComparison.Ordinal)
                        && d.Kind == RouteChangeKind.Add
                        && current.Contains(d.Prefix))
            .Select(d => d.Prefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
