using PingForge.Core.Models;

namespace PingForge.Probing;

/// <summary>
/// Сырые измерения одного маршрута.
/// <paramref name="Attempts"/> — сколько проб отправили, <paramref name="Samples"/> — только те, что вернулись:
/// потери считаются как разница, а не как «100 %, если ничего не пришло».
/// </summary>
public sealed record ProbeMeasurementInput(
    string TargetEndpoint,
    string RouteLabel,
    ProbeMethodUsed MethodUsed,
    int Attempts,
    IReadOnlyList<double> Samples,
    ProbeFailureCause FailureCause = ProbeFailureCause.None,
    string? NoteRu = null,
    IReadOnlyList<RouteHopMetric>? Hops = null);

/// <summary>
/// Статистика замеров по правилам, которые не дают отчёту врать:
///   • нет ответов → метрики остаются null, а не превращаются в «0 мс / 100 % потерь»;
///   • джиттер считается по последовательным разницам (устойчиво к одиночному выбросу),
///     а не как стандартное отклонение по всему массиву;
///   • при числе замеров меньше <see cref="MinimumSamplesForFullReport"/> отчёт помечается предварительным;
///   • стабильность — из потерь, джиттера и доли выбросов, с явными весами в модели.
/// </summary>
public static class StatisticalMetricsCalculator
{
    /// <summary>Меньше этого числа замеров — вывод предварительный (значение из политики UI: 20–120).</summary>
    public const int MinimumSamplesForFullReport = 30;

    /// <summary>Порог выброса: медиана + 3 × MAD, но не меньше 3 мс (иначе дрожание таймера даёт «выбросы»).</summary>
    private const double OutlierMadMultiplier = 3.0;

    private const double OutlierMinimumThresholdMs = 3.0;

    public static ProbeStatistics Compute(ProbeMeasurementInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<double> samples = input.Samples
            .Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0)
            .OrderBy(v => v)
            .ToList();

        int received = samples.Count;
        int attempts = Math.Max(input.Attempts, 0);

        if (received == 0)
        {
            ProbeFailureCause cause = input.FailureCause == ProbeFailureCause.None
                ? ProbeFailureCause.AllProbesTimedOut
                : input.FailureCause;

            string noAnswer = attempts == 0
                ? "Замеры не проводились: не выбрано ни одного направления."
                : $"Ни один из {attempts} проб не вернулся ({Describe(cause)}). Потери не пишем как «100 %» — маршрут просто не измеряется. " +
                  "Попробуйте включить активный режим замера или запустить игру: живой поток измеряется напрямую.";

            return ProbeStatistics.Unmeasurable(
                input.TargetEndpoint, input.RouteLabel, cause, attempts,
                AppendNote(noAnswer, input.NoteRu), input.Hops);
        }

        double median = Percentile(samples, 0.5);
        double mad = Mad(samples, median);
        double jitter = NeighbourMeanAbsoluteDeviation(input.Samples);

        double outlierThreshold = Math.Max(median + (OutlierMadMultiplier * mad), median + OutlierMinimumThresholdMs);
        int outliers = samples.Count(v => v > outlierThreshold);
        double outlierShare = outliers * 100.0 / received;

        double lossPercent = attempts == 0 ? 0 : Math.Max(0, attempts - received) * 100.0 / attempts;
        double stability = ComputeStability(lossPercent, jitter, outlierShare);

        bool preliminary = attempts < MinimumSamplesForFullReport || received < MinimumSamplesForFullReport;

        string baseMessage = string.IsNullOrWhiteSpace(input.NoteRu)
            ? Describe(input.MethodUsed)
            : input.NoteRu!;

        string message = preliminary
            ? $"Предварительная оценка: замеров {attempts} из {MinimumSamplesForFullReport}. {baseMessage}"
            : baseMessage;

        return new ProbeStatistics(
            TargetEndpoint: input.TargetEndpoint,
            RouteLabel: input.RouteLabel,
            IsMeasurable: true,
            MethodUsed: input.MethodUsed,
            FailureCause: input.FailureCause,
            StatusMessageRu: message,
            SentCount: attempts == 0 ? received : attempts,
            ReceivedCount: received,
            RttMinMs: samples[0],
            RttAvgMs: samples.Average(),
            RttMaxMs: samples[^1],
            RttMedianMs: median,
            RttP95Ms: Percentile(samples, 0.95),
            RttP99Ms: Percentile(samples, 0.99),
            JitterMadMs: jitter,
            PacketLossPercent: lossPercent,
            OutlierSpikesCount: outliers,
            StabilityScore0To100: stability,
            IsPreliminary: preliminary,
            Hops: input.Hops ?? Array.Empty<RouteHopMetric>(),
            MeasuredAtUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Стабильность 0–100: потери весят больше всего, затем джиттер, затем доля выбросов.
    /// Веса вынесены в <see cref="ProbeStatistics.StabilityWeights"/>, чтобы их можно было показать пользователю.
    /// </summary>
    public static double ComputeStability(double lossPercent, double jitterMs, double outlierSharePercent)
    {
        double raw = 100.0
            - (Math.Max(0, lossPercent) * ProbeStatistics.StabilityWeights.PerLossPercent)
            - (Math.Max(0, jitterMs) * ProbeStatistics.StabilityWeights.PerJitterMs)
            - (Math.Max(0, outlierSharePercent) * ProbeStatistics.StabilityWeights.PerOutlierPercent);

        return Math.Round(Math.Clamp(raw, 0, 100), 1);
    }

    /// <summary>
    /// Джиттер как среднее абсолютное отклонение соседних замеров.
    /// Так одиночный выброс не раздувает оценку, как это делает стандартное отклонение.
    /// Порядок замеров сохраняется — соседние значения берутся из исходной последовательности.
    /// </summary>
    public static double NeighbourMeanAbsoluteDeviation(IReadOnlyList<double> samplesInOrder)
    {
        if (samplesInOrder.Count < 2)
        {
            return 0;
        }

        double sum = 0;
        int pairs = 0;

        for (int i = 1; i < samplesInOrder.Count; i++)
        {
            double previous = samplesInOrder[i - 1];
            double current = samplesInOrder[i];

            if (double.IsNaN(previous) || double.IsNaN(current))
            {
                continue;
            }

            sum += Math.Abs(current - previous);
            pairs++;
        }

        return pairs == 0 ? 0 : Math.Round(sum / pairs, 2);
    }

    /// <summary>Медианное абсолютное отклонение (устойчивая мера разброса).</summary>
    public static double Mad(IReadOnlyList<double> sortedSamples, double median)
    {
        if (sortedSamples.Count == 0)
        {
            return 0;
        }

        var deviations = sortedSamples.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
        return Percentile(deviations, 0.5);
    }

    /// <summary>Процентиль с линейной интерполяцией. <paramref name="fraction"/> — доля от 0 до 1.</summary>
    public static double Percentile(IReadOnlyList<double> sortedSamples, double fraction)
    {
        ArgumentNullException.ThrowIfNull(sortedSamples);

        if (sortedSamples.Count == 0)
        {
            return 0;
        }

        if (sortedSamples.Count == 1)
        {
            return sortedSamples[0];
        }

        double rank = Math.Clamp(fraction, 0, 1) * (sortedSamples.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);

        if (lower == upper)
        {
            return sortedSamples[lower];
        }

        double weight = rank - lower;
        double value = (sortedSamples[lower] * (1 - weight)) + (sortedSamples[upper] * weight);
        return Math.Round(value, 2);
    }

    /// <summary>Человеческое описание метода замера — всегда попадает в отчёт рядом с числами.</summary>
    public static string Describe(ProbeMethodUsed method) =>
        method switch
        {
            ProbeMethodUsed.LiveTcpEstats =>
                "Метод замера: живой TCP-поток игры (статистика уже открытого соединения). Дополнительные пакеты не отправляются.",
            ProbeMethodUsed.LiveUdpPassiveFlow =>
                "Метод замера: живой UDP-поток игры по ответам сервера. Оценка приблизительная: окно наблюдения даёт средние значения.",
            ProbeMethodUsed.ActiveIcmpEcho =>
                "Метод замера: ICMP-эхо (активные пробы). Точность зависит от приоритета ICMP на маршруте.",
            ProbeMethodUsed.ActiveTcpHandshake =>
                "Метод замера: TCP-рукопожатие (ICMP недоступен). Это не замер игрового потока, а оценка пути.",
            _ => "Метод замера: не удалось измерить."
        };

    private static string Describe(ProbeFailureCause cause) =>
        cause switch
        {
            ProbeFailureCause.ResolutionFailed => "имя не разрешилось в IP",
            ProbeFailureCause.PermissionDenied => "пробы запрещены правами доступа",
            ProbeFailureCause.TargetUnreachable => "узел недоступен из этой сети",
            _ => "все пробы истекли по таймауту"
        };

    private static string AppendNote(string message, string? note) =>
        string.IsNullOrWhiteSpace(note) ? message : $"{message} {note}";
}
