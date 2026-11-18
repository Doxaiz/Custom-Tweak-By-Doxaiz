using PingForge.Core.Ui;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Контраст текста по WCAG 2.1 — числом, а не глазами.
/// </summary>
/// <remarks>
/// Разницу 4,39:1 и 4,5:1 никто не увидит на экране, зато первая — ниже требования AA. Именно так
/// и вышло с третьим уровнем текста: считали контраст на панели (4,69:1), а карточка — это градиент,
/// и на его светлой верхней кромке тот же текст давал 4,39:1.
/// </remarks>
public sealed class ContrastTests
{
    [Fact]
    public void Белое_на_чёрном_это_двадцать_один()
    {
        Assert.Equal(21.0, Contrast.Ratio("#FFFFFF", "#000000"), 2);
    }

    [Fact]
    public void Тот_же_цвет_это_единица()
    {
        Assert.Equal(1.0, Contrast.Ratio("#111A2A", "#111A2A"), 2);
    }

    [Fact]
    public void Порядок_цветов_не_важен()
    {
        Assert.Equal(
            Contrast.Ratio("#E9EFFB", "#111A2A"),
            Contrast.Ratio("#111A2A", "#E9EFFB"),
            6);
    }

    [Theory]
    [InlineData("#000000", 0.0)]
    [InlineData("#FFFFFF", 1.0)]
    public void Яркость_крайних_цветов(string hex, double expected)
    {
        Assert.Equal(expected, Contrast.Luminance(hex), 3);
    }

    [Fact]
    public void Крупному_тексту_достаточно_трёх_к_одному()
    {
        Assert.Equal(4.5, Contrast.Required(12, bold: false));
        Assert.Equal(4.5, Contrast.Required(18, bold: true));
        Assert.Equal(3.0, Contrast.Required(18.66, bold: true));
        Assert.Equal(3.0, Contrast.Required(24, bold: false));
    }

    [Fact]
    public void Третий_уровень_текста_читается_на_самой_светлой_поверхности()
    {
        // Верхняя кромка градиента карточки — самая светлая поверхность в теме.
        // Было #74869F: 4,39:1 — ниже AA. Стало #7B8FA8: 4,92:1.
        Assert.True(Contrast.IsReadable("#7B8FA8", "#152034", fontSize: 10.5, bold: false));
        Assert.False(Contrast.IsReadable("#74869F", "#152034", fontSize: 10.5, bold: false));
    }

    [Fact]
    public void Описание_для_отчёта_по_русски_и_с_числами()
    {
        string text = Contrast.DescribeRu("#74869F", "#152034", fontSize: 12, bold: false);

        Assert.Equal("4,39:1 (нужно 4,5)", text);
    }

    [Theory]
    [InlineData("74869F")]
    [InlineData("#7486")]
    [InlineData("#F5080D18")]
    [InlineData("#GGGGGG")]
    public void Полупрозрачный_или_битый_цвет_отвергается(string hex)
    {
        // Полупрозрачный цвет нельзя сравнить с плотным: контраст зависит от того, что под ним.
        Assert.Throws<ArgumentException>(() => Contrast.Ratio(hex, "#111A2A"));
    }
}
