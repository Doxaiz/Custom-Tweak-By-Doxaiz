using System.Xml;
using System.Xml.Linq;

namespace PingForge.Core.Layout;

/// <summary>Надпись, которая сокращается многоточием, а полного текста прочитать негде.</summary>
/// <param name="File">Файл разметки, где это стоит.</param>
/// <param name="Line">Строка в файле.</param>
/// <param name="Text">Текст или привязка — чтобы найти место без запуска программы.</param>
public readonly record struct TrimWithoutHint(string File, int Line, string Text)
{
    /// <summary>Строка отчёта: файл, строка и текст.</summary>
    public override string ToString() =>
        $"{File}:{Line}: надпись «{Text}» сокращается многоточием, а подсказки с полным текстом нет";
}

/// <summary>
/// Правило разметки: если надпись может сократиться многоточием, у неё (или у её рамки) должна быть
/// подсказка с полным текстом.
/// </summary>
/// <remarks>
/// Проверка «текст целиком» на живом окне уже отличает нарочное многоточие от обрезки. Но многоточие
/// без подсказки — это тот же потерянный текст, только вежливо оформленный: человек видит
/// «Игра: Fortnite — …» и не знает, что там дальше — адрес сервера, число или название игры.
///
/// Правило смотрит только на разметку и потому не знает, сократится ли текст на самом деле: короткая
/// строка с многоточием не сократится никогда. Поэтому оно требует подсказку у каждой надписи,
/// которая <c>МОЖЕТ</c> сократиться — запас в одну подсказку дешевле потерянного текста. Что текст
/// действительно сократился, проверяет живое окно: там сравниваются числа (см. <see cref="TextClipping"/>).
///
/// Подсказка принимается у самой надписи и у рамки вокруг неё — до трёх уровней вверх: <c>ToolTip</c>
/// на <c>Border</c> или <c>StackPanel</c> вокруг надписи человек видит, наведя на неё мышью, и это
/// честная подсказка. Глубже не смотрим: подсказка всей карточки — уже не про эту надпись.
/// </remarks>
public static class TrimHint
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// Найти надписи, которые могут сократиться многоточием, но подсказки у них нет.
    /// </summary>
    /// <param name="xaml">Содержимое файла разметки.</param>
    /// <param name="file">Имя файла для отчёта.</param>
    public static IReadOnlyList<TrimWithoutHint> Find(string xaml, string file)
    {
        ArgumentNullException.ThrowIfNull(xaml);

        var found = new List<TrimWithoutHint>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return found;   // битую разметку ловит не это правило
        }

        foreach (XElement block in document.Descendants(Wpf + "TextBlock"))
        {
            string? trimming = block.Attribute("TextTrimming")?.Value;
            if (string.IsNullOrWhiteSpace(trimming) || trimming == "None")
            {
                continue;   // текст не сокращается — подсказка не нужна
            }

            if (HasHintNear(block))
            {
                continue;
            }

            found.Add(new TrimWithoutHint(file, LineOf(block), TextOf(block)));
        }

        return found;
    }

    /// <summary>Глубина, до которой подсказка считается «у этой надписи».</summary>
    public const int HintDepth = 3;

    /// <summary>Есть ли подсказка у элемента или у рамки вокруг него.</summary>
    private static bool HasHintNear(XElement block)
    {
        XElement? element = block;
        for (int level = 0; element is not null && level <= HintDepth; level++, element = element.Parent)
        {
            if (Given(element.Attribute("ToolTip")?.Value)
                || Given(element.Attribute("ToolTipService.ToolTip")?.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Значение задано: не пусто и не пробелы. Привязка тоже задана — в ней будет текст.</summary>
    private static bool Given(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>Текст надписи или привязка — по нему место находится в разметке сразу.</summary>
    private static string TextOf(XElement block)
    {
        string? text = block.Attribute("Text")?.Value;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        string inner = block.Value.Trim().Replace("\r", " ").Replace("\n", " ");
        return inner.Length == 0 ? "(пустая надпись)" : inner;
    }

    /// <summary>Номер строки в файле: без него отчёт «где-то в разметке» бесполезен.</summary>
    private static int LineOf(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
}
