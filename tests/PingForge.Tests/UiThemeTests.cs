using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PingForge.App.ViewModels;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Оформление как проверяемый контракт. Внешний вид нельзя проверить вкусом, но можно проверить
/// то, на чём он ломается молча: ссылка на несуществующий ресурс (окно падает уже у человека,
/// а не в тестах), возврат системных светлых контролов и потеря видимого фокуса с клавиатуры.
/// Снимок живой проверки тоже проверяется: он обязан сниматься из окна, а не с экрана, иначе
/// правый край окна 1180×820 теряется на маленьком экране раннера.
/// </summary>
public sealed class UiThemeTests
{
    private static readonly Regex StaticResourceRef =
        new(@"\{StaticResource\s+([^\}]+)\}", RegexOptions.Compiled);

    /// <summary>Файлы оформления, скопированные в выход рядом с тестами (префикс checked-).</summary>
    private static readonly string[] XamlFiles =
    {
        "checked-App.xaml",
        "checked-MainWindow.xaml",
        "checked-SplashWindow.xaml",
        "checked-AboutView.xaml",
        "checked-OptimizeView.xaml",
        "checked-TweaksView.xaml",
        "checked-GamesView.xaml",
        "checked-SettingsView.xaml",
        "checked-JournalView.xaml",
        "checked-OnboardingView.xaml"
    };

    private static string ReadChecked(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"нет копии {name} для проверки: {path}");
        return File.ReadAllText(path);
    }

    private static HashSet<string> KeysOf(string xaml)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (XAttribute attribute in XDocument.Parse(xaml).Descendants().Attributes())
            if (attribute.Name.LocalName == "Key") keys.Add(attribute.Value);
        return keys;
    }

    [Fact]
    public void Все_ключи_ресурсов_в_XAML_определены()
    {
        // StaticResource разрешается при загрузке окна: опечатка в ключе — не предупреждение
        // сборки, а падение приложения у человека. Здесь ловится именно это.
        HashSet<string> global = KeysOf(ReadChecked("checked-App.xaml"));
        var missing = new List<string>();

        foreach (string file in XamlFiles)
        {
            string xaml = ReadChecked(file);
            HashSet<string> local = KeysOf(xaml);
            foreach (Match match in StaticResourceRef.Matches(xaml))
            {
                string key = match.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(key)) continue;
                if (!global.Contains(key) && !local.Contains(key))
                    missing.Add($"{file}: «{key}»");
            }
        }

        Assert.Empty(missing.Distinct());
    }

    [Fact]
    public void Тёмная_тема_оформляет_свои_контролы()
    {
        // Системные светлые полосы прокрутки, выпадающие списки и вкладки на тёмной панели
        // читаются как чужие обломки. Эти контролы обязаны иметь свой шаблон.
        string app = ReadChecked("checked-App.xaml");
        foreach (string target in new[] { "ScrollBar", "ComboBox", "ComboBoxItem", "TabItem", "TextBox", "ToolTip", "Expander" })
            Assert.Contains($"TargetType=\"{target}\"", app, StringComparison.Ordinal);

        // И готовые кирпичи оформления, которыми пользуются разделы.
        foreach (string key in new[] { "HeroCard", "StatTile", "StatValue", "StatLabel", "Chip", "ListRow", "SunkenPanel", "Pill", "Divider" })
            Assert.Contains($"x:Key=\"{key}\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Пилюли_масштаба_показывают_выбранную_ступень()
    {
        // Три одинаковые кнопки не говорили, какой масштаб включён: человек искал ответ в подписи.
        // Теперь это переключатели-пилюли, отметка которых привязана к самому числу масштаба.
        string settings = ReadChecked("checked-SettingsView.xaml");
        Assert.Contains("GroupName=\"UiScale\"", settings, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Chip}\"", settings, StringComparison.Ordinal);

        int bound = Regex.Matches(settings, Regex.Escape("Converter={StaticResource ScaleOption}")).Count;
        Assert.Equal(3, bound);

        // И сам конвертер объявлен в теме: иначе окно упало бы при загрузке «Настроек».
        Assert.Contains("x:Key=\"ScaleOption\"", ReadChecked("checked-App.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Ни_одного_системного_контрола_без_темы()
    {
        // Так нашлась раскрывашка «Подробнее» в «Твиках»: у Expander не было своего шаблона, и его
        // заголовок рисовала Windows — светлый кружок с серой стрелкой на тёмной панели. Сторож
        // перебирает разметку окон и требует, чтобы у каждого элемента, оформление которого задаёт
        // система, был свой стиль в App.xaml — без ключа, то есть применяемый сам по себе.
        string app = ReadChecked("checked-App.xaml");
        var implicitStyles = Regex.Matches(app, "<Style TargetType=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Типы, у которых Windows рисует светлую рамку, кружок, стрелку или фон.
        var chrome = new HashSet<string>(StringComparer.Ordinal)
        {
            "Button", "CheckBox", "ComboBox", "ComboBoxItem", "Expander", "RadioButton",
            "TabControl", "TabItem", "TextBox", "ToolTip", "Slider", "ProgressBar",
            "ListBox", "DataGrid", "TreeView", "PasswordBox", "GroupBox", "Label",
            "StatusBar", "Menu", "ContextMenu", "RichTextBox"
        };

        var missing = new List<string>();
        foreach (string file in XamlFiles.Where(name => name != "checked-App.xaml"))
        {
            // Комментарии не разметка: в них встречаются те же имена.
            string xaml = Regex.Replace(ReadChecked(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            foreach (Match tag in Regex.Matches(xaml, "<([A-Z][A-Za-z0-9]*)\\s([^>]*)>", RegexOptions.Singleline))
            {
                string type = tag.Groups[1].Value;
                string attributes = tag.Groups[2].Value;

                // Свой стиль на месте (Style="…") — тоже тема, а не система.
                if (!chrome.Contains(type) || attributes.Contains("Style=", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!implicitStyles.Contains(type))
                {
                    missing.Add($"{file}: <{type}>");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "элементы, которые рисует Windows (добавьте им стиль в App.xaml): " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void Фокус_с_клавиатуры_виден()
    {
        // Тёмная тема легко теряет фокус: у своих шаблонов кнопок и разделов его надо рисовать руками.
        string app = ReadChecked("checked-App.xaml");
        int triggers = Regex.Matches(app, "IsKeyboardFocused").Count;
        Assert.True(triggers >= 4, $"видимый фокус с клавиатуры задан только {triggers} раз(а), ожидалось не меньше 4");
    }

    [Fact]
    public void Палитра_задана_ключами_а_не_цветами_по_месту()
    {
        // Один набор ключей — одна тема. Иначе через месяц в окне окажется пять разных «тёмных».
        string app = ReadChecked("checked-App.xaml");
        foreach (string key in new[]
                 {
                     "BgColor", "PanelColor", "PanelTopColor", "SunkenColor", "LineColor", "LineSoftColor",
                     "TextColor", "MutedColor", "AccentColor", "Accent2Color", "WarnColor", "DangerColor",
                     // производные тона: второй уровень панелей и третий уровень текста
                     "DeepColor", "BadgeColor", "LineStrongColor", "DimColor", "RowColor", "ChipBgColor", "ScrimColor"
                 })
            Assert.Contains($"x:Key=\"{key}\"", app, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"UiFont\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"NumberFont\"", app, StringComparison.Ordinal);
        Assert.Contains("FontFamily", app, StringComparison.Ordinal);

        // И то же правило для окон и разделов: цвет по месту — это часть темы, которой не видно
        // в App.xaml. Так в разметке жили девять почти одинаковых тёмных, и «одна тема» была
        // неправдой. Исключение одно — заставка: там нарисован градиент, это картинка,
        // а не элементы управления.
        var inline = new List<string>();
        foreach (string file in XamlFiles.Where(name =>
                     name != "checked-App.xaml" && name != "checked-SplashWindow.xaml"))
        {
            string xaml = Regex.Replace(ReadChecked(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            foreach (Match color in Regex.Matches(xaml, "#[0-9A-Fa-f]{6,8}"))
            {
                inline.Add($"{file}: {color.Value}");
            }
        }

        Assert.True(
            inline.Count == 0,
            "цвета по месту (добавьте ключ в App.xaml и сошлитесь на него): " + string.Join(", ", inline.Distinct()));
    }

    [Fact]
    public void Текст_оформлен_цветами_темы()
    {
        // Заставка — исключение для РИСУНКА: там градиент и его оттенки живут по месту. Но текст
        // исключением не был: подсказка внизу карточки красилась литералом #61748F и давала
        // 3,8:1 на своей подложке — ниже требования AA (4,5:1). Поэтому цвета текста проверяются
        // во всех файлах без исключений: либо кисть темы, либо именованный цвет в App.xaml.
        var inline = new List<string>();
        foreach (string file in XamlFiles.Where(name => name != "checked-App.xaml"))
        {
            string xaml = Regex.Replace(ReadChecked(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(xaml, "Foreground=\"(#[0-9A-Fa-f]{6,8})\""))
            {
                inline.Add($"{file}: {match.Groups[1].Value}");
            }
        }

        Assert.True(
            inline.Count == 0,
            "цвет текста по месту (возьмите кисть из App.xaml): " + string.Join(", ", inline.Distinct()));
    }

    [Fact]
    public void Главное_действие_выделено_на_экране_оптимизации()
    {
        string optimize = ReadChecked("checked-OptimizeView.xaml");
        Assert.Contains("HeroCard", optimize, StringComparison.Ordinal);
        Assert.Contains("SunkenPanel", optimize, StringComparison.Ordinal);
    }

    [Fact]
    public void Снимок_живой_проверки_берётся_из_окна_а_не_с_экрана()
    {
        // CopyFromScreen по прямоугольнику окна теряет правый край, если окно больше экрана
        // раннера: снимок 1180×820 обрезался, и по нему нельзя судить о вёрстке.
        string walkthrough = ReadChecked("checked-ui-walkthrough.ps1");
        Assert.Contains("PrintWindow", walkthrough, StringComparison.Ordinal);
        Assert.Contains("PW_RENDERFULLCONTENT", walkthrough, StringComparison.Ordinal);
        Assert.Contains("SetProcessDPIAware", walkthrough, StringComparison.Ordinal);

        // Тот же снимок делает и сборка — иначе доказательство расходится с проверкой.
        string build = ReadChecked("checked-build.yml");
        Assert.Contains("PrintWindow", build, StringComparison.Ordinal);
        Assert.Contains("SetProcessDPIAware", build, StringComparison.Ordinal);
    }
    [Fact]
    public void Переключатели_и_поля_ввода_оформлены_под_тёмную_тему()
    {
        // Системные переключатели и флажки светлые, их подпись берёт системный чёрный цвет и на
        // тёмной панели пропадает: в мастере первого запуска вопросы было не прочитать.
        // Выделение в поле ввода по умолчанию тоже светлое — светлый текст на нём не виден.
        string app = ReadChecked("checked-App.xaml");

        foreach (string target in new[] { "RadioButton", "CheckBox" })
        {
            Assert.Contains($"TargetType=\"{target}\"", app, StringComparison.Ordinal);
        }

        Assert.Contains("SelectionBrush", app, StringComparison.Ordinal);
        Assert.Contains("SelectionTextBrush", app, StringComparison.Ordinal);
        Assert.Contains("CaretBrush", app, StringComparison.Ordinal);

        // Ползунок прокрутки не должен сжиматься в точку в длинном списке.
        Assert.Contains("MinHeight=\"24\"", app, StringComparison.Ordinal);

        // Абзац для чтения: ограниченная длина строки.
        Assert.Contains("x:Key=\"ReadableText\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Контраст_в_подписи_совпадает_с_настоящей_палитрой()
    {
        // Подпись про контраст когда-то осталась от прежней палитры (0.2): цвета назывались не те,
        // и «15.3:1» относилось к цветам, которых на экране уже не было. Здесь числа считаются
        // по цветам из App.xaml и сверяются с тем, что видит человек в «Настройках».
        string app = ReadChecked("checked-App.xaml");
        string text = ColorOf(app, "TextColor");
        string muted = ColorOf(app, "MutedColor");
        string bg = ColorOf(app, "BgColor");

        string note = new UiScaleViewModel().WcagNoteRu;

        Assert.Contains(text, note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(muted, note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(bg, note, StringComparison.OrdinalIgnoreCase);

        string textRatio = RatioText(ContrastRatio(text, bg));
        string mutedRatio = RatioText(ContrastRatio(muted, bg));
        Assert.Contains(textRatio, note, StringComparison.Ordinal);
        Assert.Contains(mutedRatio, note, StringComparison.Ordinal);

        // И сами числа обязаны проходить AAA для обычного текста — иначе подпись была бы ложью.
        Assert.True(ContrastRatio(text, bg) >= 7.0);
        Assert.True(ContrastRatio(muted, bg) >= 7.0);
    }

    [Fact]
    public void Минимум_окна_в_подписи_масштаба_совпадает_с_разметкой()
    {
        // В подписи стояло 800×500 при настоящем минимуме 860×560 — человек читал неправду.
        string window = ReadChecked("checked-MainWindow.xaml");
        int width = int.Parse(Required(window, "MinWidth=\"(\\d+)\""));
        int height = int.Parse(Required(window, "MinHeight=\"(\\d+)\""));

        Assert.Equal((int)UiScaleViewModel.MinWidthAt100, width);
        Assert.Equal((int)UiScaleViewModel.MinHeightAt100, height);

        string note = new UiScaleViewModel().ScaleLabelRu;
        Assert.Contains($"{width}×{height}", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Масштаб_интерфейса_действительно_масштабирует_а_не_только_пишет_проценты()
    {
        // Кнопки «100 / 125 / 150 %» раньше меняли только подпись: окно оставалось прежним.
        string window = ReadChecked("checked-MainWindow.xaml");
        Assert.Contains("LayoutTransform", window, StringComparison.Ordinal);
        Assert.Contains("ScaleTransform", window, StringComparison.Ordinal);
        Assert.Contains("Settings.UiScale.Scale", window, StringComparison.Ordinal);

        string code = ReadChecked("checked-MainWindow.xaml.cs");
        Assert.Contains("ApplyUiScale", code, StringComparison.Ordinal);
        Assert.Contains("MinWidthAt100", code, StringComparison.Ordinal);
    }

    private static string Required(string text, string pattern)
    {
        Match found = Regex.Match(text, pattern);
        Assert.True(found.Success, $"в разметке не нашлось «{pattern}»");
        return found.Groups[1].Value;
    }

    private static string ColorOf(string app, string key)
    {
        Match found = Regex.Match(app, $"<Color x:Key=\"{key}\"[^>]*>(#[0-9A-Fa-f]{{6}})</Color>");
        Assert.True(found.Success, $"в теме нет цвета «{key}»");
        return found.Groups[1].Value.ToUpperInvariant();
    }

    /// <summary>Отношение контраста по WCAG 2.1: (L1 + 0,05) / (L2 + 0,05).</summary>
    private static double ContrastRatio(string first, string second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        static double Channel(string part)
        {
            double value = Convert.ToInt32(part, 16) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex.Substring(1, 2))) +
               (0.7152 * Channel(hex.Substring(3, 2))) +
               (0.0722 * Channel(hex.Substring(5, 2)));
    }

    /// <summary>Число так, как оно написано по-русски: «17,1:1».</summary>
    private static string RatioText(double ratio) =>
        ratio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') + ":1";
}
