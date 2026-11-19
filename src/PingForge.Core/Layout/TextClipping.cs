namespace PingForge.Core.Layout;

/// <summary>Как обрезан текст в своей рамке.</summary>
public enum TextCut
{
    /// <summary>Не обрезан: поместился целиком.</summary>
    None = 0,

    /// <summary>Обрезан справа: строка не влезла по ширине.</summary>
    Right = 1,

    /// <summary>Обрезан снизу: строки не влезли по высоте, хвост фразы не виден.</summary>
    Bottom = 2,
}

/// <summary>
/// Замер одного текста: сколько ему нужно и сколько ему дали. Ширины и высоты — уже без полей.
/// </summary>
/// <param name="Name">Имя элемента или начало текста — для отчёта.</param>
/// <param name="NaturalWidth">Ширина, которая нужна тексту в одну строку.</param>
/// <param name="NaturalHeight">Высота, которая нужна тексту при доступной ширине.</param>
/// <param name="AvailableWidth">Ширина, которая есть.</param>
/// <param name="AvailableHeight">Высота, которая есть.</param>
/// <param name="Wraps">Текст переносится по словам.</param>
/// <param name="Trims">Текст сокращается многоточием — это сделано нарочно.</param>
public readonly record struct MeasuredText(
    string Name,
    double NaturalWidth,
    double NaturalHeight,
    double AvailableWidth,
    double AvailableHeight,
    bool Wraps,
    bool Trims);

/// <summary>
/// Правило «текст целиком»: то, что не влезло и не сокращено многоточием, человек не прочитает.
/// </summary>
/// <remarks>
/// Три поломки раскладки уже случились, и все три ловились по-разному: правый край окна (проверка
/// края на двух ширинах), текст в горизонтальной панели без предела (правило разметки) и вот эта —
/// текст шире или выше своей рамки. Здесь он не вылезает за окно: он обрезан внутри себя, и никакая
/// проверка границ его не видит. Человек читает «Готово: пакет собран, файлов…» и не знает, что дальше.
///
/// Правило простое и проверяемое без окна: сравниваются числа. Сокращение многоточием — не дефект:
/// его поставили нарочно, и оно честно говорит «текст длиннее».
/// </remarks>
public static class TextClipping
{
    /// <summary>Допуск в точках: раскладка округляет, а метрики шрифта в замере и в окне чуть расходятся.</summary>
    public const double Tolerance = 2.0;

    /// <summary>Обрезан ли текст и с какой стороны.</summary>
    /// <param name="text">Замер текста.</param>
    /// <param name="tolerance">Допуск в точках.</param>
    public static TextCut Find(MeasuredText text, double tolerance = Tolerance)
    {
        if (text.Trims)
        {
            return TextCut.None;   // многоточие — нарочно, это не обрез
        }

        double slack = Math.Max(0, tolerance);

        if (!text.Wraps)
        {
            bool cutRight = text.NaturalWidth > text.AvailableWidth + slack;
            return cutRight ? TextCut.Right : TextCut.None;
        }

        bool cutBottom = text.NaturalHeight > text.AvailableHeight + slack;
        return cutBottom ? TextCut.Bottom : TextCut.None;
    }

    /// <summary>
    /// Теряет ли текст смысл из-за многоточия: он сокращён, а прочитать его целиком негде.
    /// </summary>
    /// <remarks>
    /// Многоточие само по себе — не дефект: оно честно говорит «текст длиннее». Дефект начинается,
    /// когда полный текст недоступен ни в подсказке, ни где-то ещё. Тогда «Игра: Fortnite — …»
    /// остаётся загадкой: человек не узнает, обрезан адрес сервера или название игры.
    /// </remarks>
    /// <param name="text">Замер текста.</param>
    /// <param name="hasTooltip">Есть ли подсказка с полным текстом у надписи или у её рамки.</param>
    /// <param name="tolerance">Допуск в точках.</param>
    public static bool NeedsTooltip(MeasuredText text, bool hasTooltip, double tolerance = Tolerance) =>
        text.Trims
        && !hasTooltip
        && text.NaturalWidth > text.AvailableWidth + Math.Max(0, tolerance);

    /// <summary>Отчёт по-русски: «сокращён многоточием без подсказки: нужно 412 точек, есть 300».</summary>
    public static string DescribeTooltipRu(MeasuredText text) =>
        $"сокращён многоточием без подсказки: нужно {Round(text.NaturalWidth)} точек, есть {Round(text.AvailableWidth)}";

    /// <summary>Отчёт по-русски: «обрезан снизу: нужно 38 точек, есть 20».</summary>
    public static string DescribeRu(MeasuredText text, TextCut cut) => cut switch
    {
        TextCut.Right => $"обрезан справа: нужно {Round(text.NaturalWidth)} точек, есть {Round(text.AvailableWidth)}",
        TextCut.Bottom => $"обрезан снизу: нужно {Round(text.NaturalHeight)} точек, есть {Round(text.AvailableHeight)}",
        _ => "не обрезан",
    };

    private static string Round(double value) =>
        Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
