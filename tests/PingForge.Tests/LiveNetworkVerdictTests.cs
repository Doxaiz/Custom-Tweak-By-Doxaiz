using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Живой замер на настоящей сети. Самого ICMP в Linux-тестах не проверить, поэтому проверяется
/// решение по настоящим ответам: числа → проверено, тишина → пропущено с причиной, ноль → поломка.
/// Ноль здесь — не «идеальная сеть», а подстановка, и он обязан называться поломкой.
/// </summary>
public sealed class LiveNetworkVerdictTests
{
    [Fact]
    public void Настоящие_ответы_дают_проверенный_замер_с_числами()
    {
        LiveProbeVerdict verdict = LiveProbeVerdict.FromReplies(
            "1.1.1.1:443 (ICMP)", "ICMP-эхо", [12.4, 13.8, 11.9, 12.7, 13.1]);

        Assert.True(verdict.Verified);
        Assert.False(verdict.Skipped);
        Assert.Equal(LiveProbeOutcome.Measured, verdict.Outcome);
        Assert.Contains("в среднем", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("проб: 5", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("мс", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("ICMP-эхо", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Тишина_в_сети_это_пропуск_с_причиной_а_не_зелёная_галочка()
    {
        LiveProbeVerdict verdict = LiveProbeVerdict.FromReplies(
            "1.1.1.1:443 (TCP)", "TCP-рукопожатие", [], "ICMP-эху не дали прав");

        Assert.True(verdict.Skipped);
        Assert.False(verdict.Verified);
        Assert.Contains("ICMP-эху не дали прав", verdict.TextRu, StringComparison.Ordinal);
        Assert.Contains("не поломка программы", verdict.TextRu, StringComparison.Ordinal);
        Assert.DoesNotContain("среднем", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Ноль_вместо_задержки_это_поломка_а_не_идеальная_сеть()
    {
        LiveProbeVerdict verdict = LiveProbeVerdict.FromReplies(
            "1.1.1.1:443 (ICMP)", "ICMP-эхо", [0, 12.4]);

        Assert.Equal(LiveProbeOutcome.Broken, verdict.Outcome);
        Assert.False(verdict.Verified);
        Assert.False(verdict.Skipped);
        Assert.Contains("подстановка", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Нечисло_тоже_не_замер()
    {
        LiveProbeVerdict verdict = LiveProbeVerdict.FromReplies(
            "1.1.1.1:443 (ICMP)", "ICMP-эхо", [double.NaN]);

        Assert.Equal(LiveProbeOutcome.Broken, verdict.Outcome);
    }

    [Fact]
    public void Задержка_больше_пяти_секунд_не_считается_замером()
    {
        LiveProbeVerdict verdict = LiveProbeVerdict.FromReplies(
            "1.1.1.1:443 (ICMP)", "ICMP-эхо", [9000]);

        Assert.Equal(LiveProbeOutcome.Broken, verdict.Outcome);
        Assert.Contains("не похожа на настоящий замер", verdict.TextRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Числа_печатаются_одинаково_при_любой_языковой_настройке_машины()
    {
        LiveProbeVerdict verdict = LiveProbeVerdict.FromReplies(
            "1.1.1.1:443 (ICMP)", "ICMP-эхо", [12.4]);

        // Десятичная точка, а не запятая: текст журнала не должен зависеть от культуры раннера.
        Assert.Contains("12.4 мс", verdict.TextRu, StringComparison.Ordinal);
    }
}
