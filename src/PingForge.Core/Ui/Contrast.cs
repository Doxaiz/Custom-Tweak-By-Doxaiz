using System.Globalization;

namespace PingForge.Core.Ui;

/// <summary>
/// Контраст по WCAG 2.1: (L1 + 0,05) / (L2 + 0,05), где L — относительная яркость.
/// </summary>
/// <remarks>
/// Зачем это в коде, а не в глазах: цвет текста на панели проверяли глазами и арифметикой «на панели»,
/// а карточка — это градиент от <c>PanelTopColor</c> к <c>PanelColor</c>. Верхняя кромка светлее, и
/// третий уровень текста давал на ней 4,39:1 при требовании 4,5:1 для обычного текста. Никакой снимок
/// этого не покажет — разницу в 0,11 никто не увидит. Видно её только числом.
/// </remarks>
public static class Contrast
{
    /// <summary>Требование AA для обычного текста.</summary>
    public const double NormalText = 4.5;

    /// <summary>Требование AA для крупного текста (24 px и больше, либо 18,66 px полужирным).</summary>
    public const double LargeText = 3.0;

    /// <summary>Порог крупного текста в точках.</summary>
    public const double LargeFontSize = 24.0;

    /// <summary>Порог крупного полужирного текста в точках.</summary>
    public const double LargeBoldFontSize = 18.66;

    /// <summary>
    /// Отношение контраста двух цветов: от 1 (тот же цвет) до 21 (чёрный на белом).
    /// Порядок аргументов не важен.
    /// </summary>
    /// <param name="first">Цвет в виде <c>#RRGGBB</c>.</param>
    /// <param name="second">Цвет в виде <c>#RRGGBB</c>.</param>
    public static double Ratio(string first, string second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        double high = Math.Max(a, b);
        double low = Math.Min(a, b);
        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>Относительная яркость цвета по WCAG.</summary>
    /// <param name="hex">Цвет в виде <c>#RRGGBB</c>.</param>
    public static double Luminance(string hex)
    {
        (int r, int g, int b) = Parse(hex);
        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    /// <summary>Какое отношение требуется для текста такого кегля.</summary>
    /// <param name="fontSize">Кегль в точках (в WPF это и есть пиксели при 96 DPI).</param>
    /// <param name="bold">Полужирный или жирный.</param>
    public static double Required(double fontSize, bool bold) =>
        fontSize >= LargeFontSize || (bold && fontSize >= LargeBoldFontSize) ? LargeText : NormalText;

    /// <summary>Читается ли текст таких цветов и кегля.</summary>
    public static bool IsReadable(string foreground, string background, double fontSize, bool bold) =>
        Ratio(foreground, background) >= Required(fontSize, bold);

    /// <summary>Подпись для отчёта: «4,92:1 (нужно 4,5)» — с запятой, как в русском тексте.</summary>
    public static string DescribeRu(string foreground, string background, double fontSize, bool bold)
    {
        double ratio = Ratio(foreground, background);
        string need = Required(fontSize, bold).ToString("0.0", CultureInfo.InvariantCulture);
        return $"{ratio.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',')}:1 (нужно {need.Replace('.', ',')})";
    }

    private static double Channel(int value)
    {
        double part = value / 255.0;
        return part <= 0.03928 ? part / 12.92 : Math.Pow((part + 0.055) / 1.055, 2.4);
    }

    private static (int R, int G, int B) Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        string value = hex.Trim();
        if (!value.StartsWith('#') || value.Length != 7)
        {
            throw new ArgumentException($"цвет должен быть в виде #RRGGBB: «{hex}»", nameof(hex));
        }

        value = value[1..];

        // #AARRGGBB (например затемнение Scrim) не поддерживается: полупрозрачный цвет нельзя
        // сравнить с плотным — контраст зависит от того, что под ним.
        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException($"цвет должен быть в виде #RRGGBB: «{hex}»", nameof(hex));
        }

        return (
            int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
