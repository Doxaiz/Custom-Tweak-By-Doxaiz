using PingForge.Core.Layout;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Правило разметки: надпись, которая может сократиться многоточием, обязана иметь подсказку
/// с полным текстом.
/// </summary>
/// <remarks>
/// Проверка «текст целиком» отличает нарочное многоточие от обрезки, но многоточие без подсказки —
/// это тот же потерянный текст, только вежливо оформленный. Правило нашло одно настоящее место:
/// строка об игре в шапке окна сокращалась на 300 точках без подсказки. Рядом, у фразы о правах,
/// подсказка была — там её поставили раньше.
///
/// Правило смотрит на разметку и не знает, сократится ли текст на самом деле: короткая строка
/// с многоточием на экране не сократится никогда. Поэтому оно требует подсказку у каждой надписи,
/// которая МОЖЕТ сократиться, — запас в подсказку дешевле потерянного текста. Что текст действительно
/// сократился, проверяет живое окно: там сравниваются числа.
/// </remarks>
public sealed class TrimHintTests
{
    private const string Head =
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    private static string Wrap(string body) => Head + body + "</UserControl>";

    [Fact]
    public void Надпись_с_многоточием_без_подсказки_находится()
    {
        string xaml = Wrap("""
            <TextBlock Text="{Binding GameLineRu}" TextTrimming="CharacterEllipsis" MaxWidth="300" />
            """);

        TrimWithoutHint found = Assert.Single(TrimHint.Find(xaml, "Test.xaml"));
        Assert.Equal("{Binding GameLineRu}", found.Text);
        Assert.Contains("Test.xaml:", found.ToString(), StringComparison.Ordinal);
        Assert.Contains("подсказки", found.ToString(), StringComparison.Ordinal);
        Assert.True(found.Line > 0, "в отчёте нет строки — искать в разметке нечего");
    }

    [Fact]
    public void Подсказка_на_самой_надписи_снимает_замечание()
    {
        string xaml = Wrap("""
            <TextBlock Text="{Binding GameLineRu}" TextTrimming="CharacterEllipsis"
                       ToolTip="{Binding GameLineRu}" />
            """);

        Assert.Empty(TrimHint.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Подсказка_на_рамке_вокруг_надписи_считается()
    {
        // Так сделано у фразы о правах: подсказка на Border, а сокращается надпись внутри.
        string xaml = Wrap("""
            <Border ToolTip="{Binding RightsNoteRu}">
              <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding RightsNoteRu}" TextTrimming="CharacterEllipsis" MaxWidth="320" />
              </StackPanel>
            </Border>
            """);

        Assert.Empty(TrimHint.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Подсказка_на_всей_карточке_не_считается()
    {
        // Подсказка карточки — уже не про эту надпись: до неё четыре уровня вверх.
        string xaml = Wrap("""
            <Border ToolTip="Карточка целиком">
              <StackPanel>
                <Grid>
                  <StackPanel>
                    <TextBlock Text="{Binding GameLineRu}" TextTrimming="CharacterEllipsis" />
                  </StackPanel>
                </Grid>
              </StackPanel>
            </Border>
            """);

        Assert.Single(TrimHint.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Без_многоточия_подсказка_не_требуется()
    {
        string xaml = Wrap("""
            <StackPanel>
              <TextBlock Text="{Binding SummaryRu}" />
              <TextBlock Text="{Binding StatusRu}" TextTrimming="None" />
            </StackPanel>
            """);

        Assert.Empty(TrimHint.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Пустая_подсказка_не_считается_подсказкой()
    {
        string xaml = Wrap("""
            <TextBlock Text="Игра: Fortnite — …" TextTrimming="CharacterEllipsis" ToolTip="" />
            """);

        Assert.Single(TrimHint.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Битая_разметка_не_роняет_правило()
    {
        Assert.Empty(TrimHint.Find("<TextBlock", "Test.xaml"));
    }

    [Fact]
    public void В_настоящей_разметке_у_сокращаемых_надписей_есть_подсказка()
    {
        string[] files =
        [
            "checked-App.xaml", "checked-MainWindow.xaml", "checked-SplashWindow.xaml",
            "checked-AboutView.xaml", "checked-OptimizeView.xaml", "checked-TweaksView.xaml",
            "checked-GamesView.xaml", "checked-SettingsView.xaml", "checked-JournalView.xaml",
            "checked-OnboardingView.xaml",
        ];

        var problems = new List<string>();
        foreach (string file in files)
        {
            string path = Path.Combine(AppContext.BaseDirectory, file);
            Assert.True(File.Exists(path), $"нет копии {file} для проверки: {path}");

            problems.AddRange(TrimHint
                .Find(File.ReadAllText(path), file.Replace("checked-", string.Empty, StringComparison.Ordinal))
                .Select(problem => problem.ToString()));
        }

        Assert.Empty(problems);
    }
}
