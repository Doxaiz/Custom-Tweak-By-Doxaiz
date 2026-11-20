using System.Text.RegularExpressions;
using System.Xml.Linq;
using PingForge.Core.Ui;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Тема программы целиком: каждый цвет текста обязан читаться на каждой поверхности.
/// </summary>
/// <remarks>
/// Проверка идёт по настоящей разметке темы (<c>App.xaml</c>), а не по списку чисел в тесте: цвет
/// поменяли — числа пересчитались сами. Раньше контраст считали только «на панели» и пропустили,
/// что карточка — градиент от <c>PanelTopColor</c> к <c>PanelColor</c>: на светлой верхней кромке
/// третий уровень текста давал 4,39:1 при требовании 4,5:1.
/// </remarks>
public sealed class AppPaletteContrastTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Цвета, которыми пишут текст.</summary>
    private static readonly string[] TextColors =
    [
        "TextColor", "MutedColor", "DimColor", "AccentColor", "Accent2Color",
        "WarnColor", "DangerColor", "VioletColor",
    ];

    /// <summary>Поверхности, на которых этот текст может оказаться.</summary>
    private static readonly string[] Surfaces =
    [
        "BgColor", "DeepColor", "PanelColor", "PanelTopColor",
        "SunkenColor", "RowColor", "ChipBgColor", "BadgeColor",
    ];

    [Fact]
    public void Каждый_цвет_текста_читается_на_каждой_поверхности()
    {
        Dictionary<string, string> theme = Theme();
        var problems = new List<string>();

        foreach (string text in TextColors)
        {
            Assert.True(theme.ContainsKey(text), $"в теме нет цвета {text}");
            foreach (string surface in Surfaces)
            {
                Assert.True(theme.ContainsKey(surface), $"в теме нет поверхности {surface}");

                // Подписи в интерфейсе — 10,5–13 точек: это обычный текст, требование 4,5:1.
                if (!Contrast.IsReadable(theme[text], theme[surface], fontSize: 12, bold: false))
                {
                    problems.Add($"{text} на {surface}: {Contrast.DescribeRu(theme[text], theme[surface], 12, false)}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Тёмный_текст_основной_кнопки_читается_на_обоих_концах_градиента()
    {
        string themePath = Path.Combine(AppContext.BaseDirectory, "checked-App.xaml");
        string xaml = File.ReadAllText(themePath);

        Match found = Regex.Match(
            xaml,
            "x:Key=\"PrimaryButton\"[\\s\\S]{0,400}?Property=\"Foreground\" Value=\"(#[0-9A-Fa-f]{6})\"");
        Assert.True(found.Success, "в теме не нашлась подпись основной кнопки (PrimaryButton → Foreground)");

        string label = found.Groups[1].Value;
        Dictionary<string, string> theme = Theme();

        foreach (string end in new[] { "AccentColor", "Accent2Color" })
        {
            Assert.True(
                Contrast.IsReadable(label, theme[end], fontSize: 15, bold: true),
                $"подпись кнопки {label} на {end}: {Contrast.DescribeRu(label, theme[end], 15, true)}");
        }
    }

    [Fact]
    public void Крупные_заголовки_тоже_проверяются_по_своему_требованию()
    {
        // У крупного текста требование ниже (3:1), но текст всё равно должен его выполнять.
        Dictionary<string, string> theme = Theme();

        Assert.True(Contrast.IsReadable(theme["TextColor"], theme["PanelTopColor"], fontSize: 23, bold: true));
        Assert.Equal(3.0, Contrast.Required(23, bold: true));
    }

    private static Dictionary<string, string> Theme()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-App.xaml");
        Assert.True(File.Exists(path), $"нет копии темы для проверки: {path}");

        var theme = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement element in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "Color"))
        {
            string? key = element.Attribute(Xaml + "Key")?.Value;
            if (!string.IsNullOrEmpty(key))
            {
                theme[key] = element.Value.Trim();
            }
        }

        return theme;
    }
}
