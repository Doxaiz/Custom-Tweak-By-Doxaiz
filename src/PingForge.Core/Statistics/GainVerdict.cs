namespace PingForge.Core.Statistics;

/// <summary>Чем кончилось сравнение «до» и «после».</summary>
public enum GainOutcome
{
    /// <summary>Замеров слишком мало, чтобы что-то утверждать. Так и говорим.</summary>
    NotMeasured,

    /// <summary>Разница больше шума: выигрыш измерен.</summary>
    Confirmed,

    /// <summary>Разница внутри шума: выигрышем это не считаем.</summary>
    WithinNoise,

    /// <summary>Разница больше шума, но в худшую сторону.</summary>
    Worse,
}

/// <summary>
/// Выигрыш засчитывается только тогда, когда он выше шума.
///
/// Раньше программа сравнивала две медианы и при разнице больше 0,5 мс писала «стало лучше на N мс».
/// При разбросе замера в ±3 мс это значит засчитывать шум как результат — та же зелёная галочка, только
/// в цифрах. Здесь разница медиан получает 95-процентный доверительный интервал (бутстрэп), и вывод
/// делается по интервалу: если он накрывает ноль — это шум, и программа так и говорит.
///
/// Разброс (джиттер) сравнивается тем же способом: «стабильнее» — это тоже утверждение, и оно тоже
/// требует, чтобы интервал разницы не накрывал ноль.
///
/// Никакой магии и никаких внешних библиотек: медиана, бутстрэп с фиксированным зерном (вывод не зависит
/// от запуска к запуску) и процентили. Всё проверяется тестами без сети.
/// </summary>
public sealed record GainVerdict(
    GainOutcome Outcome,
    int BeforeSamples,
    int AfterSamples,
    double? MedianBeforeMs,
    double? MedianAfterMs,
    double? GainMs,
    double? CiLowMs,
    double? CiHighMs,
    double? JitterBeforeMs,
    double? JitterAfterMs,
    double? JitterCiLowMs,
    double? JitterCiHighMs,
    string SummaryRu,
    string StabilityRu)
{
    /// <summary>Меньше этого числа окон — вывод не делаем: «два замера» не статистика.</summary>
    public const int MinimumSamples = 8;

    /// <summary>Меньше половины миллисекунды разницы не считаем выигрышем даже при узком интервале.</summary>
    public const double MeaningfulMs = 0.5;

    /// <summary>Зерно бутстрэпа: один и тот же набор данных даёт один и тот же интервал.</summary>
    private const int BootstrapSeed = 20261116;

    private const int BootstrapResamples = 1000;

    public bool IsMeasured => Outcome != GainOutcome.NotMeasured;

    public bool IsConfirmedGain => Outcome == GainOutcome.Confirmed;

    /// <summary>
    /// Сравнить два набора окон. <paramref name="jitterBefore"/> и <paramref name="jitterAfter"/> —
    /// разброс в тех же окнах; без них вывод о стабильности честно говорит «не сравнить.
    /// </summary>
    public static GainVerdict Evaluate(
        IReadOnlyList<double> before,
        IReadOnlyList<double> after,
        IReadOnlyList<double>? jitterBefore = null,
        IReadOnlyList<double>? jitterAfter = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<double> cleanBefore = Clean(before);
        List<double> cleanAfter = Clean(after);

        string stability = DescribeStability(jitterBefore, jitterAfter);

        if (cleanBefore.Count < MinimumSamples || cleanAfter.Count < MinimumSamples)
        {
            string why = $"Выигрыш не заявлен: для вывода нужно не меньше {MinimumSamples} окон «до» и «после», " +
                $"а есть {cleanBefore.Count} и {cleanAfter.Count}. Двух-трёх замеров для такого утверждения мало.";
            return new GainVerdict(
                GainOutcome.NotMeasured,
                cleanBefore.Count,
                cleanAfter.Count,
                cleanBefore.Count > 0 ? Median(cleanBefore) : null,
                cleanAfter.Count > 0 ? Median(cleanAfter) : null,
                null, null, null,
                null, null, null, null,
                why,
                stability);
        }

        double medianBefore = Median(cleanBefore);
        double medianAfter = Median(cleanAfter);
        double gain = medianBefore - medianAfter;
        (double low, double high) = MedianDifferenceCi(cleanBefore, cleanAfter);

        GainOutcome outcome;
        string summary;
        if (low > MeaningfulMs)
        {
            outcome = GainOutcome.Confirmed;
            summary = $"Выигрыш подтверждён: медиана {medianBefore:0.#} → {medianAfter:0.#} мс, разница {gain:+0.#;-0.#;0} мс, " +
                $"95% ДИ [{low:+0.#;-0.#;0}; {high:+0.#;-0.#;0}] целиком выше нуля.";
        }
        else if (high < -MeaningfulMs)
        {
            outcome = GainOutcome.Worse;
            summary = $"Стало хуже: медиана {medianBefore:0.#} → {medianAfter:0.#} мс, разница {gain:+0.#;-0.#;0} мс, " +
                $"95% ДИ [{low:+0.#;-0.#;0}; {high:+0.#;-0.#;0}] целиком ниже нуля.";
        }
        else
        {
            outcome = GainOutcome.WithinNoise;
            summary = $"Разница в пределах шума: медиана {medianBefore:0.#} → {medianAfter:0.#} мс, " +
                $"разница {gain:+0.#;-0.#;0} мс, 95% ДИ [{low:+0.#;-0.#;0}; {high:+0.#;-0.#;0}] накрывает ноль — " +
                "выигрышем это не считаем.";
        }

        return new GainVerdict(
            outcome,
            cleanBefore.Count,
            cleanAfter.Count,
            medianBefore,
            medianAfter,
            gain,
            low,
            high,
            null, null, null, null,
            summary,
            stability);
    }

    /// <summary>Текст для человека: одна строка, без обещаний, с числами и интервалом.</summary>
    public string DescribeRu() => StabilityRu.Length == 0 ? SummaryRu : SummaryRu + " " + StabilityRu;

    private static string DescribeStability(IReadOnlyList<double>? before, IReadOnlyList<double>? after)
    {
        if (before is null || after is null)
        {
            return "Стабильность не сравнить: разброс «до» или «после» не измерялся.";
        }

        List<double> cleanBefore = Clean(before);
        List<double> cleanAfter = Clean(after);
        if (cleanBefore.Count < MinimumSamples || cleanAfter.Count < MinimumSamples)
        {
            return $"Стабильность не сравнить: нужно не меньше {MinimumSamples} окон с измеренным разбросом, " +
                $"а есть {cleanBefore.Count} и {cleanAfter.Count}.";
        }

        double jitterBefore = Median(cleanBefore);
        double jitterAfter = Median(cleanAfter);
        (double low, double high) = MedianDifferenceCi(cleanBefore, cleanAfter);

        if (low > MeaningfulMs)
        {
            return $"Стабильность лучше: разброс {jitterBefore:0.#} → {jitterAfter:0.#} мс, " +
                $"95% ДИ разницы [{low:0.#}; {high:0.#}] целиком выше нуля. Меньше разброс — ровнее пинг.";
        }

        if (high < -MeaningfulMs)
        {
            return $"Стабильность хуже: разброс {jitterBefore:0.#} → {jitterAfter:0.#} мс, " +
                $"95% ДИ разницы [{low:0.#}; {high:0.#}] целиком ниже нуля.";
        }

        return $"Стабильность: разброс {jitterBefore:0.#} → {jitterAfter:0.#} мс — изменение в пределах шума " +
            $"95% ДИ [{low:0.#}; {high:0.#}], за улучшение не считаем.";
    }

    private static List<double> Clean(IReadOnlyList<double> values) =>
        values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0).ToList();

    /// <summary>Медиана выборки (для чётного числа — среднее двух центральных).</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("медиана пустого набора не определена", nameof(values));
        }

        double[] sorted = values.OrderBy(v => v).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>
    /// 95-процентный интервал разницы медиан: бутстрэп с фиксированным зерном.
    /// Возвращает (низ, верх) в миллисекундах; знак — как у «до минус после».
    /// </summary>
    public static (double Low, double High) MedianDifferenceCi(IReadOnlyList<double> before, IReadOnlyList<double> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        double[] b = before.ToArray();
        double[] a = after.ToArray();
        var random = new Random(BootstrapSeed);
        var differences = new double[BootstrapResamples];

        for (int i = 0; i < BootstrapResamples; i++)
        {
            differences[i] = MedianResample(b, random) - MedianResample(a, random);
        }

        Array.Sort(differences);
        return (Percentile(differences, 0.025), Percentile(differences, 0.975));
    }

    private static double MedianResample(double[] source, Random random)
    {
        var sample = new double[source.Length];
        for (int i = 0; i < sample.Length; i++)
        {
            sample[i] = source[random.Next(source.Length)];
        }

        return Median(sample);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        double position = fraction * (sorted.Length - 1);
        int index = (int)Math.Floor(position);
        if (index >= sorted.Length - 1)
        {
            return sorted[^1];
        }

        double weight = position - index;
        return sorted[index] * (1 - weight) + sorted[index + 1] * weight;
    }
}
