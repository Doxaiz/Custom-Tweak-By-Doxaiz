using System.Xml;
using System.Xml.Linq;

namespace PingForge.Core.Layout;

/// <summary>Текст в разметке, который может вырасти шире контейнера.</summary>
/// <param name="Element">Тип элемента: <c>TextBlock</c>, <c>CheckBox</c>.</param>
/// <param name="File">Файл разметки, где это стоит.</param>
/// <param name="Text">Начало текста или привязка — чтобы найти место без запуска программы.</param>
/// <param name="Line">Строка в файле.</param>
public readonly record struct UnboundedText(string Element, string File, string Text, int Line)
{
    /// <summary>Строка отчёта: файл, строка, элемент и текст.</summary>
    public override string ToString() => $"{File}:{Line}: {Element} «{Text}»";
}

/// <summary>
/// Правило разметки: текст внутри горизонтального <c>StackPanel</c> обязан иметь предел ширины.
/// </summary>
/// <remarks>
/// Горизонтальный <c>StackPanel</c> меряет детей бесконечной шириной — это его свойство, а не ошибка.
/// Поэтому текст без <c>MaxWidth</c> (или <c>Width</c>) берёт всю желаемую ширину, строка становится
/// шире страницы, и в окне 860 правый край уезжает за границу. Полоса содержимого прокручивается
/// только вниз, так что до обрезанного не добраться вообще.
///
/// Так уже было трижды за одну волну: статус проверки обновлений, строка «окно графика» и пояснение
/// к мультипути. Каждый раз это ловила живая проверка на 860 — то есть после сборки и прогона.
/// Это правило ловит то же самое на этапе тестов, до сборки.
///
/// Короткие подписи («Окно:», «Сервер:») правило пропускает: они не растут ни от данных, ни от языка.
/// </remarks>
public static class HorizontalTextRule
{
    /// <summary>Длина подписи, до которой текст считается неизменным и предел ему не нужен.</summary>
    public const int FreeLiteralLength = 30;

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// Найти тексты в горизонтальных панелях без предела ширины.
    /// </summary>
    /// <param name="xaml">Содержимое файла разметки.</param>
    /// <param name="file">Имя файла для отчёта.</param>
    public static IReadOnlyList<UnboundedText> Find(string xaml, string file)
    {
        ArgumentNullException.ThrowIfNull(xaml);

        var found = new List<UnboundedText>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return found;   // битую разметку ловит не это правило
        }

        foreach (XElement panel in document.Descendants(Wpf + "StackPanel"))
        {
            if ((string?)panel.Attribute("Orientation") != "Horizontal")
            {
                continue;
            }

            foreach (XElement child in panel.Elements())
            {
                if (child.Name != Wpf + "TextBlock" && child.Name != Wpf + "CheckBox")
                {
                    continue;
                }

                if (child.Attribute("MaxWidth") is not null || child.Attribute("Width") is not null)
                {
                    continue;   // предел есть — текст не сможет растянуть строку
                }

                string text = TextOf(child);
                if (IsFixedLabel(text))
                {
                    continue;
                }

                found.Add(new UnboundedText(
                    child.Name.LocalName,
                    file,
                    Shorten(text),
                    LineOf(child)));
            }
        }

        return found;
    }

    /// <summary>Короткая неизменная подпись — не текст, который вырастет от данных.</summary>
    public static bool IsFixedLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;   // без текста ширины не добавляет
        }

        bool literal = !text.Contains('{');
        return literal && text.Trim().Length <= FreeLiteralLength;
    }

    private static string TextOf(XElement element)
    {
        XElement? holder = element.Name == Wpf + "TextBlock"
            ? element.Element(Wpf + "TextBlock.Text")
            : element.Element(Wpf + "CheckBox.Content");

        return ((string?)element.Attribute("Text")
                ?? (string?)element.Attribute("Content")
                ?? (string?)holder
                ?? string.Empty).Trim();
    }

    private static string Shorten(string text)
    {
        string oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 48 ? oneLine : oneLine[..48] + "…";
    }

    private static int LineOf(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
}
