using System.Globalization;

namespace PingForge.Probing;

/// <summary>
/// Вердикт живой проверки iphlpapi: соединение нашлось и стек назвал задержку — проверено; соединения
/// нет или сбор статистики не включён — пропущено с причиной; стек вернул ноль при нуле измерений —
/// поломка, а не «нулевая задержка».
///
/// Правило то же, что во всей отчётности: ноль — это измеренное отсутствие, а не отсутствие замера.
/// </summary>
public sealed record LiveTcpEstatsVerdict(LiveProbeOutcome Outcome, string TextRu)
{
    /// <summary>Предел разумного для задержки соединения: больше пяти секунд — это не задержка.</summary>
    public const double ImplausibleRttMs = 5000;

    public bool Verified => Outcome == LiveProbeOutcome.Measured;

    public bool Skipped => Outcome == LiveProbeOutcome.NoNetwork;

    public static LiveTcpEstatsVerdict Evaluate(
        string host,
        int port,
        IReadOnlyList<LiveTcpConnectionStats> connections,
        string? problemRu = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(connections);

        LiveTcpConnectionStats? match = connections
            .FirstOrDefault(item => item.RemoteAddress == host && item.RemotePort == port);

        if (match is null)
        {
            string why = string.IsNullOrWhiteSpace(problemRu)
                ? $"соединения с {host}:{port} нет в таблице соединений"
                : problemRu!;

            return new LiveTcpEstatsVerdict(
                LiveProbeOutcome.NoNetwork,
                $"{host}:{port}: {why} Это не поломка программы: сбор статистики соединения " +
                "включают только администраторы, и система может его не поддерживать.");
        }

        if (match.Samples <= 0)
        {
            return new LiveTcpEstatsVerdict(
                LiveProbeOutcome.Broken,
                $"{host}:{port}: стек назвал задержку {Milliseconds(match.SmoothedRttMs)} при " +
                "0 измерениях — это не замер, а подстановка нуля.");
        }

        if (match.SmoothedRttMs <= 0)
        {
            // Снимок пути считает время целыми миллисекундами: ноль при живых измерениях значит «меньше
            // миллисекунды». Подставлять вместо него красивое число нельзя, но и поломкой это не является.
            return new LiveTcpEstatsVerdict(
                LiveProbeOutcome.Measured,
                $"{host}:{port}: задержка меньше 1 мс (стек считает целыми миллисекундами), измерений стека " +
                $"{match.Samples}, повторных пакетов {match.Retransmissions} — числа взяты у iphlpapi, " +
                "ни одного пакета не отправлено.");
        }

        if (match.SmoothedRttMs > ImplausibleRttMs)
        {
            return new LiveTcpEstatsVerdict(
                LiveProbeOutcome.Broken,
                $"{host}:{port}: задержка {Milliseconds(match.SmoothedRttMs)} не похожа на настоящую " +
                $"(предел разумного — {Milliseconds(ImplausibleRttMs)}).");
        }

        return new LiveTcpEstatsVerdict(
            LiveProbeOutcome.Measured,
            $"{host}:{port}: задержка {Milliseconds(match.SmoothedRttMs)}, разброс " +
            $"{Milliseconds(match.RttVariationMs)}, измерений стека {match.Samples}, " +
            $"повторных пакетов {match.Retransmissions} — числа взяты у iphlpapi, ни одного пакета не отправлено.");
    }

    private static string Milliseconds(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture) + " мс";
}
