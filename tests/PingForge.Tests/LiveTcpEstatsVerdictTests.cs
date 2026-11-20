using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Живая проверка iphlpapi. Сам вызов нельзя проверить на Linux, поэтому проверяется решение по тому,
/// что читатель вернул: есть задержка и измерения — проверено; соединения нет — пропущено с причиной;
/// ноль при нуле измерений — поломка, а не «нулевая задержка».
/// </summary>
public sealed class LiveTcpEstatsVerdictTests
{
    private static LiveTcpConnectionStats Stats(
        string address = "1.1.1.1",
        int port = 443,
        double rtt = 12.4,
        double variation = 1.2,
        int samples = 7,
        int retransmissions = 0) =>
        new(address, port, rtt, variation, samples, retransmissions);

    [Fact]
    public void Числа_стека_дают_проверенный_живой_замер()
    {
        LiveTcpEstatsVerdict verdict = LiveTcpEstatsVerdict.Evaluate("1.1.1.1", 443, [Stats()]);

        Assert.True(verdict.Verified);
        Assert.False(verdict.Skipped);
        Assert.Contains("iphlpapi", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("12.4 мс", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("измерений стека 7", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("ни одного пакета не отправлено", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Чужое_соединение_не_выдаётся_за_проверенное()
    {
        LiveTcpEstatsVerdict verdict = LiveTcpEstatsVerdict.Evaluate(
            "1.1.1.1", 443, [Stats(address: "198.51.100.7", port: 443), Stats(port: 8443)]);

        Assert.True(verdict.Skipped);
        Assert.False(verdict.Verified);
        Assert.Contains("нет в таблице соединений", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Причина_от_читателя_попадает_в_текст_как_есть()
    {
        LiveTcpEstatsVerdict verdict = LiveTcpEstatsVerdict.Evaluate(
            "1.1.1.1", 443, [], "Сбор статистики TCP-соединения запрещён: нужны права администратора.");

        Assert.True(verdict.Skipped);
        Assert.Contains("нужны права администратора", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("не поломка программы", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Ноль_при_нуле_измерений_это_поломка()
    {
        LiveTcpEstatsVerdict verdict = LiveTcpEstatsVerdict.Evaluate(
            "1.1.1.1", 443, [Stats(rtt: 0, samples: 0)]);

        Assert.Equal(LiveProbeOutcome.Broken, verdict.Outcome);
        Assert.False(verdict.Verified);
        Assert.Contains("подстановка нуля", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Измерения_есть_а_задержки_нет_тоже_поломка()
    {
        LiveTcpEstatsVerdict verdict = LiveTcpEstatsVerdict.Evaluate(
            "1.1.1.1", 443, [Stats(rtt: 0, samples: 12)]);

        Assert.Equal(LiveProbeOutcome.Broken, verdict.Outcome);
    }

    [Fact]
    public void Невозможная_задержка_не_считается_замером()
    {
        LiveTcpEstatsVerdict verdict = LiveTcpEstatsVerdict.Evaluate(
            "1.1.1.1", 443, [Stats(rtt: 6000)]);

        Assert.Equal(LiveProbeOutcome.Broken, verdict.Outcome);
        Assert.Contains("не похожа на настоящую", verdict.TextRu, StringComparison.Ordinal);
    }
}
