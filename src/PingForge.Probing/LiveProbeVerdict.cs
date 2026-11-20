using System.Globalization;

namespace PingForge.Probing;

/// <summary>Чем кончилась настоящая проба в настоящую сеть.</summary>
public enum LiveProbeOutcome
{
    /// <summary>Проба вернула настоящие числа.</summary>
    Measured,

    /// <summary>Сети нет или пробы запрещены — это не поломка программы, а условие машины.</summary>
    NoNetwork,

    /// <summary>Проба отчиталась об успехе, но числа не настоящие: ноль, нечисло или заведомая чушь.</summary>
    Broken
}

/// <summary>
/// Вердикт живой пробы: числа есть — проверено; ответа нет — пропущено с причиной; ноль вместо
/// задержки — поломка. Ноль никогда не считается замером: именно на этом правиле держится вся
/// отчётность программы, и проверять его нужно той же меркой.
/// </summary>
public sealed record LiveProbeVerdict(LiveProbeOutcome Outcome, string TextRu)
{
    /// <summary>Предел разумного для одного замера. Задержка больше пяти секунд — это не задержка.</summary>
    public const double ImplausibleRttMs = 5000;

    public bool Verified => Outcome == LiveProbeOutcome.Measured;

    public bool Skipped => Outcome == LiveProbeOutcome.NoNetwork;

    /// <summary>
    /// Собрать вердикт по настоящим ответам. <paramref name="rttMs"/> — только те ответы, за которые
    /// сеть действительно ответила; <paramref name="refusalRu"/> — почему ответов нет (если известно).
    /// </summary>
    public static LiveProbeVerdict FromReplies(
        string targetRu,
        string methodRu,
        IReadOnlyList<double> rttMs,
        string? refusalRu = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRu);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodRu);
        ArgumentNullException.ThrowIfNull(rttMs);

        if (rttMs.Count == 0)
        {
            string why = string.IsNullOrWhiteSpace(refusalRu) ? "ни одна проба не вернулась" : refusalRu!;

            return new LiveProbeVerdict(
                LiveProbeOutcome.NoNetwork,
                $"{targetRu}: {why}. Это не поломка программы: сеть могла быть закрыта, а пробы — запрещены политикой.");
        }

        if (rttMs.Any(value => !double.IsFinite(value) || value <= 0))
        {
            return new LiveProbeVerdict(
                LiveProbeOutcome.Broken,
                $"{targetRu}: проба вернула ноль или нечисло вместо задержки — это подстановка, а не замер.");
        }

        double average = rttMs.Average();
        if (average > ImplausibleRttMs)
        {
            return new LiveProbeVerdict(
                LiveProbeOutcome.Broken,
                $"{targetRu}: средняя задержка {Milliseconds(average)} не похожа на настоящий замер " +
                $"(предел разумного — {Milliseconds(ImplausibleRttMs)}).");
        }

        double min = rttMs.Min();
        double max = rttMs.Max();

        return new LiveProbeVerdict(
            LiveProbeOutcome.Measured,
            $"{targetRu}: {Milliseconds(average)} в среднем (проб: {rttMs.Count}, " +
            $"от {Milliseconds(min)} до {Milliseconds(max)}), способ «{methodRu}».");
    }

    private static string Milliseconds(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture) + " мс";
}
