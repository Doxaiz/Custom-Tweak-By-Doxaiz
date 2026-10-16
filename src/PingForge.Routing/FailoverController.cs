using PingForge.Core.Models;

namespace PingForge.Routing;

/// <summary>
/// Автофейловер с гистерезисом и защитой от флаппинга.
///
/// Правила:
///   • переключение только после N подряд «плохих» замеров активного маршрута
///     (одиночный скачок пинга — не повод ломать соединение);
///   • кандидат обязан быть лучше минимум на SwitchMarginPoints;
///   • после переключения — период покоя MinimumTimeOnRoute: за это время обратные
///     переключения запрещены, иначе маршрут начнёт «дёргаться»;
///   • возврат на прямой путь выполняется после GoodSamplesBeforeReturn замеров подряд,
///     где прямой путь лучше активного;
///   • если релей стабильно проигрывает прямому пути, выдаётся RecommendDisableRelay —
///     честная рекомендация выключить оптимизацию, а не держать её ради отчёта.
/// </summary>
public sealed class FailoverController
{
    private readonly FailoverOptions _options;

    private string? _activePathId;
    private bool _activeIsRelay;
    private DateTime? _lastSwitchUtc;
    private int _consecutiveBad;
    private int _consecutiveBetterDirect;

    public FailoverController(FailoverOptions? options = null) => _options = options ?? new FailoverOptions();

    public FailoverOptions Options => _options;

    public string? ActivePathId => _activePathId;

    /// <summary>Сообщить контроллеру, что переключение реально выполнено (для отсчёта периода покоя).</summary>
    public void NotifySwitchApplied(string pathId, bool isRelay, DateTime appliedUtc)
    {
        _activePathId = pathId;
        _activeIsRelay = isRelay;
        _lastSwitchUtc = appliedUtc;
        _consecutiveBad = 0;
        _consecutiveBetterDirect = 0;
    }

    public FailoverEvaluation Evaluate(FailoverSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.Candidates.Count == 0)
        {
            return new FailoverEvaluation(FailoverDecisionKind.InsufficientData, null,
                "Нет данных о кандидатах для сравнения.", _consecutiveBad, false);
        }

        FailoverCandidate best = sample.Candidates.OrderBy(c => c.Score).First();
        double improvement = sample.ActiveScore - best.Score;

        bool activeDegraded =
            sample.ActiveJitterMs > _options.DegradationJitterMs ||
            sample.ActiveLossPercent > _options.DegradationLossPercent;

        _consecutiveBad = activeDegraded ? _consecutiveBad + 1 : 0;

        // Период покоя: не переключаемся чаще, чем раз в MinimumTimeOnRoute.
        if (_lastSwitchUtc is not null && sample.SampledUtc - _lastSwitchUtc.Value < _options.MinimumTimeOnRoute)
        {
            return new FailoverEvaluation(
                FailoverDecisionKind.StayOnActive,
                null,
                $"Переключение заблокировано периодом покоя ({_options.MinimumTimeOnRoute.TotalSeconds:F0} с с момента последнего перехода).",
                _consecutiveBad,
                WasBlockedByCooldown: true);
        }

        if (_activeIsRelay && !best.IsRelay)
        {
            // Активен релей, а прямой путь лучше: считаем подряд идущие замеры.
            _consecutiveBetterDirect = improvement > 0 ? _consecutiveBetterDirect + 1 : 0;

            if (_consecutiveBetterDirect >= _options.GoodSamplesBeforeReturn)
            {
                return new FailoverEvaluation(
                    FailoverDecisionKind.RecommendDisableRelay,
                    best.PathId,
                    $"Прямое подключение лучше активного релея на {improvement:F2} балла в течение " +
                    $"{_consecutiveBetterDirect} замеров подряд. Возвращаемся на прямой путь и рекомендуем " +
                    "отключить оптимизацию: релей на этом маршруте не даёт выигрыша.",
                    _consecutiveBad,
                    false);
            }
        }
        else
        {
            _consecutiveBetterDirect = 0;
        }

        // Переключение на лучший маршрут: нужна деградация активного пути и запас по баллам.
        bool degradationConfirmed = _consecutiveBad >= _options.BadSamplesBeforeSwitch;
        bool marginOk = improvement >= _options.SwitchMarginPoints;

        if (degradationConfirmed && marginOk)
        {
            return new FailoverEvaluation(
                FailoverDecisionKind.SwitchToCandidate,
                best.PathId,
                $"Активный маршрут деградировал {_consecutiveBad} замеров подряд " +
                $"(джиттер {sample.ActiveJitterMs:F1} мс, потери {sample.ActiveLossPercent:F1} %). " +
                $"Кандидат «{best.PathId}» лучше на {improvement:F2} балла — выполняем переключение.",
                _consecutiveBad,
                false);
        }

        string reason = degradationConfirmed
            ? $"Деградация подтверждена, но выигрыш кандидата всего {improvement:F2} балла при пороге {_options.SwitchMarginPoints:F2} — остаёмся на текущем маршруте."
            : $"Активный маршрут в норме (плохих замеров подряд: {_consecutiveBad} из {_options.BadSamplesBeforeSwitch}).";

        return new FailoverEvaluation(FailoverDecisionKind.StayOnActive, null, reason, _consecutiveBad, false);
    }
}
