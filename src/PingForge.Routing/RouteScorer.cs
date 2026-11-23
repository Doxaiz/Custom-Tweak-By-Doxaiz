using PingForge.Core.Models;

namespace PingForge.Routing;

/// <summary>
/// Скоринг маршрутов. Меньше баллов — лучше. Формула открытая и объяснимая:
///
///   score = RTT(avg)      × w1
///         + jitter(MAD)   × w2
///         + loss(%)²      × w3      ← потери штрафуются квадратично: 5 % потерь вреднее, чем +5 мс
///         + (хопы − 1)    × w4
///         + max(0, нагрузка POP − 50 %) × w5
///         + доп. трафик(%) × w6      ← для режима дублирования пакетов
///
/// Все веса — в единицах «баллов на единицу измерения» и доступны настройке.
/// Маршрут, не прошедший пороги пригодности, до сравнения не допускается.
/// </summary>
public sealed class RouteScorer
{
    private readonly RouteScoreWeights _weights;
    private readonly RouteEligibilityThresholds _thresholds;

    public RouteScorer(RouteScoreWeights? weights = null, RouteEligibilityThresholds? thresholds = null)
    {
        _weights = weights ?? RouteScoreWeights.Default;
        _thresholds = thresholds ?? new RouteEligibilityThresholds();
    }

    public RouteScoreResult Score(RouteCandidate candidate, RelayNodeHealth? relayHealth = null, double extraTrafficPercent = 0.0)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var penalties = new List<string>();
        ProbeStatistics probe = candidate.LatestProbe;

        if (!probe.IsMeasurable)
        {
            return RouteScoreResult.NotEligible(
                candidate.PathId,
                $"Маршрут «{candidate.DisplayName}» исключён: измерить задержку не удалось ({probe.StatusMessageRu})",
                penalties);
        }

        double rtt = probe.RttAvgMs ?? double.NaN;

        // Неизмеренное не становится нулём: ноль — это измеренное отсутствие разброса и потерь,
        // и подставлять его значило бы дать маршруту балл за то, чего никто не мерил.
        double? jitter = probe.JitterMadMs;
        double? loss = probe.PacketLossPercent;

        if (double.IsNaN(rtt))
        {
            return RouteScoreResult.NotEligible(
                candidate.PathId,
                $"Маршрут «{candidate.DisplayName}» исключён: нет среднего RTT.",
                penalties);
        }

        if (loss is { } measuredLoss && measuredLoss > _thresholds.MaxLossPercent)
        {
            penalties.Add($"потери {measuredLoss:F1} % выше порога {_thresholds.MaxLossPercent:F1} %");
            return RouteScoreResult.NotEligible(
                candidate.PathId,
                $"Маршрут «{candidate.DisplayName}» исключён: потери {measuredLoss:F1} % превышают допустимые {_thresholds.MaxLossPercent:F1} %.",
                penalties);
        }

        if (loss is null)
        {
            penalties.Add("потери не измерены: в балл не вошли");
        }

        if (jitter is { } measuredJitter && measuredJitter > _thresholds.MaxJitterMs)
        {
            penalties.Add($"джиттер {measuredJitter:F1} мс выше порога {_thresholds.MaxJitterMs:F1} мс");
            return RouteScoreResult.NotEligible(
                candidate.PathId,
                $"Маршрут «{candidate.DisplayName}» исключён: джиттер {measuredJitter:F1} мс выше допустимого {_thresholds.MaxJitterMs:F1} мс.",
                penalties);
        }

        if (jitter is null)
        {
            penalties.Add("джиттер не измерен: в балл не вошёл");
        }

        if (rtt > _thresholds.MaxRttMs)
        {
            penalties.Add($"RTT {rtt:F1} мс выше порога {_thresholds.MaxRttMs:F1} мс");
            return RouteScoreResult.NotEligible(
                candidate.PathId,
                $"Маршрут «{candidate.DisplayName}» исключён: задержка {rtt:F1} мс выше допустимой {_thresholds.MaxRttMs:F1} мс.",
                penalties);
        }

        int hops = probe.Hops.Count(h => !h.IsTimedOut);
        double hopPenalty = Math.Max(0, hops - 1) * _weights.PerHop;
        double lossPenalty = loss is { } scoredLoss ? scoredLoss * scoredLoss * _weights.PerLossPercentSquared : 0.0;
        double nodeLoadPenalty = 0.0;

        if (candidate.IsRelay && relayHealth is not null)
        {
            double loadOver = Math.Max(0.0, relayHealth.EstimatedLoadPercent - 50.0);
            nodeLoadPenalty = loadOver * _weights.PerNodeLoadPercentOver50;
            if (loadOver > 0)
            {
                penalties.Add($"загрузка POP-узла {relayHealth.EstimatedLoadPercent:F0} %");
            }
        }

        double trafficPenalty = Math.Max(0.0, extraTrafficPercent) * _weights.PerExtraTrafficPercent;
        if (trafficPenalty > 0)
        {
            penalties.Add($"дополнительный трафик +{extraTrafficPercent:F0} %");
        }

        // Неизмеренный джиттер не подставляется нулём в формулу: слагаемого просто нет, и в объяснении
        // балла стоит «джиттер не измерен — в балл не вошёл». Ноль и «не измерено» — разные вещи.
        double jitterTerm = jitter is { } measuredJitter ? measuredJitter * _weights.PerJitterMs : 0.0;

        double score =
            (rtt * _weights.PerRttMs)
            + jitterTerm
            + lossPenalty
            + hopPenalty
            + nodeLoadPenalty
            + trafficPenalty;

        // В объяснении балла неизмеренное подписано словами, а не нулём: иначе человек прочитает
        // «джиттер 0,0 мс» и решит, что разброс мерили.
        var parts = new List<string>
        {
            $"RTT {rtt:F1} мс × {_weights.PerRttMs:F2}",
            jitter is { } shownJitter
                ? $"джиттер {shownJitter:F1} мс × {_weights.PerJitterMs:F2}"
                : "джиттер не измерен — в балл не вошёл",
            loss is { } shownLoss
                ? $"потери {shownLoss:F1} %² × {_weights.PerLossPercentSquared:F2}"
                : "потери не измерены — в балл не вошли"
        };

        if (hops > 1)
        {
            parts.Add($"хопы {hops} × {_weights.PerHop:F2}");
        }

        if (nodeLoadPenalty > 0)
        {
            parts.Add($"нагрузка узла × {_weights.PerNodeLoadPercentOver50:F3}");
        }

        if (trafficPenalty > 0)
        {
            parts.Add($"доп. трафик × {_weights.PerExtraTrafficPercent:F2}");
        }

        string explanation = $"{candidate.DisplayName}: {score:F2} балла ({string.Join(" + ", parts)}).";

        return new RouteScoreResult(candidate.PathId, Math.Round(score, 2), true, explanation, penalties);
    }
}
