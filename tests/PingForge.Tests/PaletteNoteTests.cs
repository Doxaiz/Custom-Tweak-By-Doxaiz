using System.Xml.Linq;
using PingForge.App.ViewModels;
using PingForge.Core.Ui;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Подпись о контрасте в «Настройках»: числа считаются по цветам темы, а не печатаются по памяти.
/// </summary>
/// <remarks>
/// До этой волны в модели представления стояли готовые «17,1:1» и «7,5:1», а `UiThemeTests` сверял
/// их с цветами `App.xaml` — от вранья это спасало, но числа оставались вписанными руками, и вывод
/// об AA и AAA был отдельным утверждением. Теперь отношения считает <see cref="Contrast"/>, пороги
/// берутся из его констант, а вывод об AA и AAA следует из самих чисел: если палитра однажды
/// не пройдёт AA, подпись скажет это прямо.
/// </remarks>
public sealed class PaletteNoteTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Отношения_считаются_по_цветам()
    {
        Assert.Equal("17,1:1", Contrast.RatioRu(PaletteNote.TextColor, PaletteNote.BgColor));
        Assert.Equal("7,5:1", Contrast.RatioRu(PaletteNote.MutedColor, PaletteNote.BgColor));
    }

    [Fact]
    public void В_подписи_числа_с_запятой()
    {
        string sentence = PaletteNote.SentenceRu(PaletteNote.TextColor, PaletteNote.MutedColor, PaletteNote.BgColor);

        Assert.Contains("17,1:1", sentence, StringComparison.Ordinal);
        Assert.Contains("7,5:1", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("17.1", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("7.5", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Тема_проходит_AAA_и_подпись_говорит_об_этом()
    {
        string sentence = PaletteNote.SentenceRu(PaletteNote.TextColor, PaletteNote.MutedColor, PaletteNote.BgColor);

        Assert.Equal(PaletteVerdict.AboveAaa, PaletteNote.VerdictOf(PaletteNote.TextColor, PaletteNote.MutedColor, PaletteNote.BgColor));
        Assert.Contains("выше AAA", sentence, StringComparison.Ordinal);
        Assert.Contains("4,5:1", sentence, StringComparison.Ordinal);
        Assert.Contains("7,0:1", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Средний_контраст_называется_честно()
    {
        // 5,94:1 — выше AA, но ниже AAA. Подпись не имеет права говорить «выше AAA».
        string sentence = PaletteNote.SentenceRu("#7B8FA8", "#7B8FA8", PaletteNote.BgColor);

        Assert.Equal(PaletteVerdict.AboveAa, PaletteNote.VerdictOf("#7B8FA8", "#7B8FA8", PaletteNote.BgColor));
        Assert.Contains("ниже AAA", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("выше AAA", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Слабый_контраст_называется_провалом()
    {
        // 1,57:1 — сильно ниже AA. Такую палитру оставлять нельзя, и подпись говорит это прямо.
        string sentence = PaletteNote.SentenceRu("#2A3444", "#2A3444", PaletteNote.BgColor);

        Assert.Equal(PaletteVerdict.BelowAa, PaletteNote.VerdictOf("#2A3444", "#2A3444", PaletteNote.BgColor));
        Assert.Contains("ниже требования AA", sentence, StringComparison.Ordinal);
        Assert.Contains("нельзя", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("читаются без напряжения", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Пороги_берутся_из_констант_контраста()
    {
        string above = PaletteNote.SentenceRu("#FFFFFF", "#FFFFFF", "#000000");

        Assert.Contains("21,0:1", above, StringComparison.Ordinal);
        Assert.Contains(
            Contrast.NormalText.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ','),
            above,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Цвета_по_умолчанию_совпадают_с_темой()
    {
        // Главный сторож: если палитра в App.xaml изменится, а значения по умолчанию — нет,
        // тест упадёт здесь, а не в подписи на экране.
        XDocument theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "checked-App.xaml"));

        Assert.Equal(ColorOf(theme, "TextColor"), PaletteNote.TextColor, ignoreCase: true);
        Assert.Equal(ColorOf(theme, "MutedColor"), PaletteNote.MutedColor, ignoreCase: true);
        Assert.Equal(ColorOf(theme, "BgColor"), PaletteNote.BgColor, ignoreCase: true);
    }

    [Fact]
    public void Битый_цвет_отвергается()
    {
        Assert.Throws<ArgumentException>(
            () => PaletteNote.SentenceRu("белый", PaletteNote.MutedColor, PaletteNote.BgColor));
    }

    [Fact]
    public void Подпись_в_модели_считается_по_переданным_цветам()
    {
        var scale = new UiScaleViewModel();
        var changed = new List<string?>();
        scale.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Contains("17,1:1", scale.WcagNoteRu, StringComparison.Ordinal);

        scale.ApplyTheme("#FFFFFF", "#FFFFFF", "#000000");

        Assert.Contains("21,0:1", scale.WcagNoteRu, StringComparison.Ordinal);
        Assert.Contains(nameof(UiScaleViewModel.WcagNoteRu), changed);
    }

    private static string ColorOf(XDocument theme, string key)
    {
        XElement? element = theme
            .Descendants()
            .FirstOrDefault(candidate => (string?)candidate.Attribute(Xaml + "Key") == key);

        Assert.NotNull(element);
        return element!.Value.Trim();
    }
}
