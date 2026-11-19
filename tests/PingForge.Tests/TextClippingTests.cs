using PingForge.Core.Layout;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// «Текст целиком»: не влез и не сокращён многоточием — значит человек читает половину фразы.
/// </summary>
/// <remarks>
/// Это третий вид поломки раскладки после правого края окна и текста в горизонтальной панели без
/// предела. Здесь текст не выходит за окно: он обрезан внутри своей рамки, и проверка границ его
/// не видит. Правило решает по числам, поэтому проверяется без окна.
/// </remarks>
public sealed class TextClippingTests
{
    private static MeasuredText Text(
        double naturalWidth = 200,
        double naturalHeight = 18,
        double availableWidth = 200,
        double availableHeight = 18,
        bool wraps = true,
        bool trims = false) =>
        new("Caption", naturalWidth, naturalHeight, availableWidth, availableHeight, wraps, trims);

    [Fact]
    public void Поместившийся_текст_не_обрезан()
    {
        Assert.Equal(TextCut.None, TextClipping.Find(Text()));
    }

    [Fact]
    public void Строка_шире_рамки_обрезана_справа()
    {
        MeasuredText text = Text(naturalWidth: 420, availableWidth: 300, wraps: false);

        Assert.Equal(TextCut.Right, TextClipping.Find(text));
        Assert.Equal("обрезан справа: нужно 420 точек, есть 300", TextClipping.DescribeRu(text, TextCut.Right));
    }

    [Fact]
    public void Перенесённый_текст_не_влез_по_высоте_и_обрезан_снизу()
    {
        // Самая частая беда: перенос разрешён, но высота задана — хвост фразы не виден.
        MeasuredText text = Text(naturalHeight: 38, availableHeight: 20);

        Assert.Equal(TextCut.Bottom, TextClipping.Find(text));
        Assert.Equal("обрезан снизу: нужно 38 точек, есть 20", TextClipping.DescribeRu(text, TextCut.Bottom));
    }

    [Fact]
    public void Многоточие_это_не_обрез()
    {
        // Сокращение поставили нарочно, и оно честно говорит «текст длиннее».
        MeasuredText text = Text(naturalWidth: 420, availableWidth: 300, wraps: false, trims: true);

        Assert.Equal(TextCut.None, TextClipping.Find(text));
    }

    [Fact]
    public void Округление_раскладки_не_считается_обрезом()
    {
        Assert.Equal(TextCut.None, TextClipping.Find(Text(naturalWidth: 301.5, availableWidth: 300, wraps: false)));
        Assert.Equal(TextCut.Right, TextClipping.Find(Text(naturalWidth: 303, availableWidth: 300, wraps: false)));
    }

    [Fact]
    public void Ширина_не_проверяется_у_переносящегося_текста()
    {
        // Перенос — законный способ не влезть по ширине: проверять там ширину бессмысленно.
        Assert.Equal(TextCut.None, TextClipping.Find(Text(naturalWidth: 900, availableWidth: 300, wraps: true)));
    }

    [Fact]
    public void Высота_не_проверяется_у_однострочного_текста()
    {
        // Без переноса высота — это одна строка; если строка не влезла, виновата ширина.
        Assert.Equal(TextCut.None, TextClipping.Find(Text(naturalHeight: 90, availableHeight: 18, wraps: false)));
    }

    [Fact]
    public void Отчёт_о_целом_тексте_говорит_прямо()
    {
        Assert.Equal("не обрезан", TextClipping.DescribeRu(Text(), TextCut.None));
    }
}
