using PingForge.Core.Layout;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Правило разметки: текст в горизонтальном <c>StackPanel</c> обязан иметь предел ширины.
/// </summary>
/// <remarks>
/// Правило появилось после трёх настоящих дефектов за одну волну: статус проверки обновлений,
/// строка «окно графика» и пояснение к мультипути уезжали за правый край окна 860. Каждый раз это
/// ловила живая проверка — то есть после сборки и прогона на Windows. Здесь то же самое ловится
/// тестами, до сборки.
///
/// Границы правила честные: проверяются прямые дети горизонтальной панели — именно им панель даёт
/// бесконечную ширину. Вложенные контейнеры со своей раскладкой (Grid с колонкой «*») ширину уже
/// ограничивают, и такие строки чинят переводом на Grid — так и было сделано в разметке.
/// </remarks>
public sealed class HorizontalTextRuleTests
{
    private const string Head =
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    private static string Wrap(string body) => Head + body + "</UserControl>";

    [Fact]
    public void Текст_привязки_без_предела_находится()
    {
        string xaml = Wrap("""
            <StackPanel Orientation="Horizontal">
              <Button Content="Обновить" />
              <TextBlock Text="{Binding SummaryRu}" />
            </StackPanel>
            """);

        UnboundedText found = Assert.Single(HorizontalTextRule.Find(xaml, "Test.xaml"));
        Assert.Equal("TextBlock", found.Element);
        Assert.Equal("{Binding SummaryRu}", found.Text);
        Assert.Contains("Test.xaml:", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Предел_ширины_снимает_замечание()
    {
        string xaml = Wrap("""
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="{Binding SummaryRu}" MaxWidth="420" />
              <TextBlock Text="{Binding StatusRu}" Width="260" />
            </StackPanel>
            """);

        Assert.Empty(HorizontalTextRule.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Короткая_подпись_пропускается()
    {
        string xaml = Wrap("""
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="Окно:" />
              <TextBlock Text="Сервер:" />
            </StackPanel>
            """);

        Assert.Empty(HorizontalTextRule.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Длинная_подпись_находится()
    {
        // Подпись, которая не меняется, но длинна: она тоже растягивает строку.
        string xaml = Wrap("""
            <StackPanel Orientation="Horizontal">
              <TextBlock Text="резерв отмечается в списке, основной остаётся /32 на сервер игры" />
            </StackPanel>
            """);

        Assert.Single(HorizontalTextRule.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Флажок_с_длинной_подписью_находится()
    {
        string xaml = Wrap("""
            <StackPanel Orientation="Horizontal">
              <CheckBox Content="выбирать лучший сервер автоматически и не спрашивать" />
            </StackPanel>
            """);

        Assert.Equal("CheckBox", Assert.Single(HorizontalTextRule.Find(xaml, "Test.xaml")).Element);
    }

    [Fact]
    public void Вертикальная_панель_не_проверяется()
    {
        // Вертикальная панель меряет детей доступной шириной — текст там переносится как обычно.
        string xaml = Wrap("""
            <StackPanel>
              <TextBlock Text="{Binding SummaryRu}" />
            </StackPanel>
            """);

        Assert.Empty(HorizontalTextRule.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Текст_внутри_Grid_считается_ограниченным()
    {
        // Так и чинили настоящие дефекты: Grid с колонкой «*» даёт тексту предел.
        string xaml = Wrap("""
            <StackPanel Orientation="Horizontal">
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="Auto" />
                  <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Button Grid.Column="0" Content="Обновить" />
                <TextBlock Grid.Column="1" Text="{Binding SummaryRu}" />
              </Grid>
            </StackPanel>
            """);

        Assert.Empty(HorizontalTextRule.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Битая_разметка_не_роняет_правило()
    {
        Assert.Empty(HorizontalTextRule.Find("<StackPanel", "Test.xaml"));
    }

    [Fact]
    public void В_настоящей_разметке_нет_текста_в_горизонтальной_панели_без_предела()
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

            problems.AddRange(HorizontalTextRule
                .Find(File.ReadAllText(path), file.Replace("checked-", string.Empty, StringComparison.Ordinal))
                .Select(problem => problem.ToString()));
        }

        Assert.Empty(problems);
    }
}
