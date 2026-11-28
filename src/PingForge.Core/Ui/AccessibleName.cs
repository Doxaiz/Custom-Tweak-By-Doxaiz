using System.Xml;
using System.Xml.Linq;

namespace PingForge.Core.Ui;

/// <summary>Элемент управления, который экранный диктор назовёт одним словом: «поле», «список».</summary>
/// <param name="Element">Тип элемента: <c>TextBox</c>, <c>ComboBox</c>, <c>CheckBox</c>.</param>
/// <param name="File">Файл разметки, где это стоит.</param>
/// <param name="Line">Строка в файле.</param>
/// <param name="Reason">Почему имени нет: своими словами, без «см. документацию».</param>
public readonly record struct UnnamedControl(string Element, string File, int Line, string Reason)
{
    /// <summary>Строка отчёта: файл, строка, тип и причина.</summary>
    public override string ToString() => $"{File}:{Line}: {Element} — {Reason}";
}

/// <summary>
/// Правило разметки: у элемента управления должно быть имя, которое прочитает экранный диктор.
/// </summary>
/// <remarks>
/// Диктор (Narrator, NVDA) объявляет элемент тем, что у него спросит через автоматизацию.
/// Кнопка с текстом «Применить» отдаёт свой текст сам — её имя появляется из содержимого.
/// А у поля ввода и у списка содержимого нет: без явного имени диктор скажет «поле редактирования»
/// и человек не узнает, что туда вписывать — адрес узла или подпись к нему.
///
/// Имя задаётся явно (<c>AutomationProperties.Name</c>), ссылкой на свою надпись
/// (<c>AutomationProperties.LabeledBy</c>) или через <c>Label Target="…"</c>. Все три способа
/// правило принимает: это честные имена, а не обход проверки.
///
/// Привязка в имени или в содержимом считается именем: во время работы там текст. Пустая привязка —
/// другая ошибка, и ловит её не разметка, а самопроверка, которая смотрит настоящее окно с настоящими
/// данными.
///
/// Что правило пропускает и почему:
///   • содержимое <c>ControlTemplate</c> и <c>Style</c> — это внутренности стиля (стрелки полосы
///     прокрутки, заголовок раскрывашки), имя им даёт шаблон, а не разметка раздела;
///   • свёрнутое (<c>Visibility="Collapsed"</c>) — диктор такого не читает: его нет в дереве
///     автоматизации, пока элемент не покажут.
/// </remarks>
public static class AccessibleName
{
    /// <summary>Атрибут, которым имя задаётся напрямую.</summary>
    public const string NameAttribute = "AutomationProperties.Name";

    /// <summary>Атрибут «имя берётся у этой надписи».</summary>
    public const string LabeledByAttribute = "AutomationProperties.LabeledBy";

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Типы, у которых имени в содержимом нет вовсе: его надо задавать явно.</summary>
    private static readonly HashSet<string> NeedsExplicit = new(StringComparer.Ordinal)
    {
        "TextBox", "PasswordBox", "ComboBox", "ListBox", "ListView", "DataGrid", "Slider", "RichTextBox",
    };

    /// <summary>Типы, чьё имя выводится из содержимого: текст кнопки и есть её имя.</summary>
    private static readonly HashSet<string> FromContent = new(StringComparer.Ordinal)
    {
        "Button", "CheckBox", "RadioButton", "ToggleButton", "TabItem", "MenuItem", "Expander", "Hyperlink",
    };

    /// <summary>Следит ли правило за этим типом элемента.</summary>
    public static bool IsSurveyed(string element) =>
        NeedsExplicit.Contains(element) || FromContent.Contains(element);

    /// <summary>Нужно ли этому типу явное имя: содержимое ему имени не даёт.</summary>
    public static bool NeedsExplicitName(string element) => NeedsExplicit.Contains(element);

    /// <summary>
    /// Найти элементы управления без имени для диктора.
    /// </summary>
    /// <param name="xaml">Содержимое файла разметки.</param>
    /// <param name="file">Имя файла для отчёта.</param>
    public static IReadOnlyList<UnnamedControl> Find(string xaml, string file)
    {
        ArgumentNullException.ThrowIfNull(xaml);

        var found = new List<UnnamedControl>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return found;   // битую разметку ловит не это правило
        }

        // Надписи, на которые ссылаются через Label Target: такая ссылка и есть имя.
        var labeled = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement label in document.Descendants(Wpf + "Label"))
        {
            string? target = TargetName(label.Attribute("Target")?.Value);
            if (!string.IsNullOrEmpty(target))
            {
                labeled.Add(target);
            }
        }

        foreach (XElement element in document.Descendants())
        {
            string type = element.Name.LocalName;
            if (!IsSurveyed(type) || InsideTemplate(element) || IsCollapsed(element) || HasName(element, type, labeled))
            {
                continue;
            }

            found.Add(new UnnamedControl(type, file, LineOf(element), ReasonFor(type)));
        }

        return found;
    }

    /// <summary>Почему элемент без имени — так, чтобы по строке было понятно, что делать.</summary>
    public static string ReasonFor(string element) => NeedsExplicit.Contains(element)
        ? "у этого типа имя не берётся из содержимого, а AutomationProperties.Name не задан"
        : "в содержимом нет текста, а AutomationProperties.Name не задан";

    /// <summary>Имя есть: задано явно, взято у надписи или выведено из содержимого.</summary>
    private static bool HasName(XElement element, string type, HashSet<string> labeled)
    {
        if (IsGiven(element.Attribute(NameAttribute)?.Value) ||
            IsGiven(element.Attribute(LabeledByAttribute)?.Value))
        {
            return true;
        }

        string? own = element.Attribute(Xaml + "Name")?.Value;
        if (!string.IsNullOrEmpty(own) && labeled.Contains(own))
        {
            return true;
        }

        if (!FromContent.Contains(type))
        {
            return false;
        }

        foreach (string attribute in new[] { "Content", "Header", "Text" })
        {
            if (IsGiven(element.Attribute(attribute)?.Value))
            {
                return true;
            }
        }

        // Содержимое может быть панелью с надписью внутри: «Открыть папку игры» и есть имя кнопки.
        foreach (XElement text in element.Descendants(Wpf + "TextBlock"))
        {
            if (IsGiven(text.Attribute("Text")?.Value) || IsGiven(text.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Значение задано: не пусто и не пробелы. Привязка тоже задана — в ней будет текст.</summary>
    private static bool IsGiven(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>Элемент лежит внутри шаблона стиля: имя ему даёт шаблон.</summary>
    private static bool InsideTemplate(XElement element)
    {
        for (XElement? parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            string name = parent.Name.LocalName;
            if (name is "ControlTemplate" or "Style")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Свёрнутый элемент (или внутри свёрнутого) диктор не читает.</summary>
    private static bool IsCollapsed(XElement element)
    {
        for (XElement? node = element; node is not null; node = node.Parent)
        {
            string? visibility = node.Attribute("Visibility")?.Value;
            if (visibility is "Collapsed" or "Hidden")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Имя элемента, на который ссылается <c>Label Target</c>: литерал или <c>ElementName</c>.</summary>
    private static string? TargetName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        target = target.Trim();
        if (!target.StartsWith('{'))
        {
            return target;
        }

        const string marker = "ElementName=";
        int at = target.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        string name = target[(at + marker.Length)..].TrimEnd('}', ' ');
        return name.Length == 0 ? null : name;
    }

    /// <summary>Номер строки в файле: без него отчёт «где-то в разметке» бесполезен.</summary>
    private static int LineOf(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
}
