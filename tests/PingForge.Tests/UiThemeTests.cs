using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        "checked-JournalView.xaml"
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
        foreach (string target in new[] { "ScrollBar", "ComboBox", "ComboBoxItem", "TabItem", "TextBox", "ToolTip" })
            Assert.Contains($"TargetType=\"{target}\"", app, StringComparison.Ordinal);

        // И готовые кирпичи оформления, которыми пользуются разделы.
        foreach (string key in new[] { "HeroCard", "StatTile", "StatValue", "StatLabel", "Chip", "ListRow", "SunkenPanel", "Pill", "Divider" })
            Assert.Contains($"x:Key=\"{key}\"", app, StringComparison.Ordinal);
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
                     "TextColor", "MutedColor", "AccentColor", "Accent2Color", "WarnColor", "DangerColor"
                 })
            Assert.Contains($"x:Key=\"{key}\"", app, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"UiFont\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"NumberFont\"", app, StringComparison.Ordinal);
        Assert.Contains("FontFamily", app, StringComparison.Ordinal);
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
}
