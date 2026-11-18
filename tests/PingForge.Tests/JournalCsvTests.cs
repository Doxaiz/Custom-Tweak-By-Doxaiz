using PingForge.App.ViewModels;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// CSV журнала: экранирование и защита от формул.
///
/// Повод: поля журнала — это значения из реестра и тексты ошибок, то есть строки, которые пишет не
/// программа. Excel и LibreOffice выполняют поле, начинающееся с «=», «+», «@», «-», — а журнал
/// человек открывает в таблице. Это не теория: значение параметра вполне может начинаться с минуса
/// или с «=». Здесь проверяется, что формула становится текстом, а обычное число — нет.
/// </summary>
public sealed class JournalCsvTests
{
    private static JournalRowViewModel Row(
        string applied = "1",
        string previous = "2",
        string title = "Частота подтверждений TCP",
        string code = "TCP_ACK_FREQUENCY",
        string status = "применено и проверено") =>
        new(
            new DateTime(2026, 11, 18, 14, 2, 11, DateTimeKind.Utc),
            title,
            code,
            previous,
            applied,
            applied,
            Succeeded: true,
            RebootNeeded: false,
            ExplanationRu: status);

    [Fact]
    public void Заголовок_совпадает_с_колонками()
    {
        string csv = JournalCsv.Build([]);

        Assert.StartsWith(JournalCsv.Header, csv, StringComparison.Ordinal);
        Assert.Equal(7, JournalCsv.Header.Split(',').Length);
    }

    [Fact]
    public void Обычная_строка_пишется_без_лишних_кавычек()
    {
        string csv = JournalCsv.Build([Row()]);
        string line = csv.Split('\n')[1].TrimEnd('\r');

        Assert.Equal("2026-11-18 14:02:11,Частота подтверждений TCP,TCP_ACK_FREQUENCY,2,1,1,применено и проверено", line);
    }

    [Fact]
    public void Кавычки_запятые_и_переносы_экранируются()
    {
        Assert.Equal("\"он сказал \"\"да\"\"\"", JournalCsv.EscapeField("он сказал \"да\""));
        Assert.Equal("\"a,b\"", JournalCsv.EscapeField("a,b"));
        Assert.Equal("\"две\nстроки\"", JournalCsv.EscapeField("две\nстроки"));
        Assert.Equal(string.Empty, JournalCsv.EscapeField(null));
    }

    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+7", "'+7")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-1+1", "'-1+1")]
    [InlineData("=HYPERLINK(\"http://пример\",\"клик\")", "'\"=HYPERLINK(\"\"http://пример\"\",\"\"клик\"\")\"")]
    public void Формула_становится_текстом(string value, string expected)
    {
        Assert.Equal(expected, JournalCsv.EscapeField(value));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-3.5")]
    [InlineData("-0.25")]
    [InlineData("1")]
    [InlineData("enabled")]
    public void Обычное_значение_не_трогается(string value)
    {
        Assert.Equal(value, JournalCsv.EscapeField(value));
    }

    [Fact]
    public void Минус_с_запятой_остаётся_числом_но_берётся_в_кавычки()
    {
        // Русская десятичная запятая: это по-прежнему число, просто запятая — разделитель CSV.
        Assert.Equal("\"-1,5\"", JournalCsv.EscapeField("-1,5"));
    }

    [Fact]
    public void В_журнале_формула_из_реестра_не_выполнится()
    {
        string csv = JournalCsv.Build([Row(applied: "=cmd|' /c calc'!A0", previous: "-1")]);
        string line = csv.Split('\n')[1].TrimEnd('\r');

        Assert.Contains("'=cmd|' /c calc'!A0", line, StringComparison.Ordinal);
        Assert.Contains(",-1,", line, StringComparison.Ordinal);
        Assert.DoesNotContain(",=cmd", line, StringComparison.Ordinal);
    }
}
