using System.Globalization;

namespace PingForge.Core.Layout;

/// <summary>
/// Прямоугольник элемента в координатах окна — ровно то, что нужно проверке правого края.
/// </summary>
/// <remarks>
/// Ширина берётся не из <c>ActualWidth</c> «сам по себе», а из границ, посчитанных от корня окна:
/// у элемента может быть поле, рамка и <c>LayoutTransform</c> масштаба, и тогда <paramref name="Left"/>
/// не ноль. Складывать <c>Margin + ActualWidth</c> вручную — способ разойтись с настоящей раскладкой.
/// </remarks>
/// <param name="Name">Имя элемента для отчёта: <c>x:Name</c>, а если его нет — тип и имя родителя.</param>
/// <param name="Left">Левый край в точках от левого края корня окна.</param>
/// <param name="Width">Ширина в точках.</param>
public readonly record struct LayoutBox(string Name, double Left, double Width)
{
    /// <summary>Правый край в точках от левого края корня окна.</summary>
    public double Right => Left + Width;
}

/// <summary>
/// Проверка «ничего не выходит за правый край окна»: содержимое разреза окна прокручивается только
/// вниз (<c>HorizontalScrollBarVisibility="Disabled"</c>), поэтому всё, что шире окна, человек
/// не увидит — оно просто обрезано.
/// </summary>
/// <remarks>
/// Логика намеренно отделена от WPF: размеры приходят снаружи, а решение «это дефект или нет»
/// проверяется тестами без окна и без Windows. Так же отделены «не сжата карточка» и «переносятся
/// абзацы» — те проверки живут в самопроверке, а решение о дефекте проверяется здесь.
/// </remarks>
public static class LayoutOverflow
{
    /// <summary>
    /// Допуск в точках. Раскладка WPF округляет до целых пикселей, а <c>ActualWidth</c> может
    /// отличаться на полточки: край, вышедший на полторы точки, — не дефект, а округление.
    /// </summary>
    public const double Tolerance = 2.0;

    /// <summary>Сколько элементов называть в отчёте прежде, чем написать «и ещё N».</summary>
    public const int NamesInReport = 5;

    /// <summary>
    /// Элементы, чей правый край вышел за предел больше, чем на <paramref name="tolerance"/>.
    /// Порядок сохраняется: отчёт должен начинаться с того элемента, который проверяли первым.
    /// </summary>
    /// <param name="boxes">Элементы раскладки с координатами от корня окна.</param>
    /// <param name="limit">Правый предел — ширина содержимого окна в точках.</param>
    /// <param name="tolerance">Допуск в точках.</param>
    public static IReadOnlyList<LayoutBox> Find(
        IEnumerable<LayoutBox> boxes,
        double limit,
        double tolerance = Tolerance)
    {
        var found = new List<LayoutBox>();
        if (double.IsNaN(limit) || double.IsInfinity(limit) || limit <= 0)
        {
            return found;   // нечего сравнивать: окно ещё не измерилось
        }

        foreach (LayoutBox box in boxes)
        {
            if (!IsMeasured(box))
            {
                continue;   // свёрнутый или ещё не разложенный элемент: ActualWidth = 0 — это не «шире окна»
            }

            if (box.Right > limit + Math.Max(0, tolerance))
            {
                found.Add(box);
            }
        }

        return found;
    }

    /// <summary>
    /// Отчёт по-русски: «панель «Твики» — правый край 1042 при окне 1024» и, если виноватых много,
    /// «и ещё 3». Имена важнее чисел: человек ищет в разметке имя, а не точку.
    /// </summary>
    public static string DescribeRu(IReadOnlyList<LayoutBox> overflowing, double limit)
    {
        ArgumentNullException.ThrowIfNull(overflowing);
        if (overflowing.Count == 0)
        {
            return string.Empty;
        }

        int shown = Math.Min(overflowing.Count, NamesInReport);
        var parts = new List<string>(shown);
        for (int i = 0; i < shown; i++)
        {
            LayoutBox box = overflowing[i];
            // Числа — без культуры: журнал читают на сборочной машине, и «1 042» там же превратится
            // в «1042» или наоборот. Точка в отчёте должна означать одну и ту же точку везде.
            string right = Math.Round(box.Right).ToString("0", CultureInfo.InvariantCulture);
            string edge = Math.Round(limit).ToString("0", CultureInfo.InvariantCulture);
            parts.Add($"{box.Name} — правый край {right} при окне {edge}");
        }

        string tail = overflowing.Count > shown ? $" и ещё {overflowing.Count - shown}" : string.Empty;
        return string.Join("; ", parts) + tail;
    }

    private static bool IsMeasured(in LayoutBox box) =>
        box.Width > 0 &&
        !double.IsNaN(box.Width) && !double.IsInfinity(box.Width) &&
        !double.IsNaN(box.Left) && !double.IsInfinity(box.Left);
}
