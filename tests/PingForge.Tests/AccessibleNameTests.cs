using PingForge.Core.Ui;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Правило разметки: у элемента управления должно быть имя для экранного диктора.
/// </summary>
/// <remarks>
/// У кнопки имя — это её текст, его диктор читает сам. У поля ввода и у списка содержимого нет,
/// поэтому без явного имени диктор говорит «поле редактирования» и человек не знает, что вписывать.
/// Проверка правила нашла в разметке программы одиннадцать таких мест: поиск и три фильтра в
/// «Журнале», два поля узла и поиск в «Настройках» и «Твиках», три списка выбора сессий и сервера
/// и переключатель твика без подписи.
/// </remarks>
public sealed class AccessibleNameTests
{
    private const string Head =
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    private static string Wrap(string body) => Head + body + "</UserControl>";

    [Fact]
    public void Поле_ввода_без_имени_находится()
    {
        string xaml = Wrap("""
            <TextBox Width="220" Text="{Binding SearchText}" />
            """);

        UnnamedControl found = Assert.Single(AccessibleName.Find(xaml, "Test.xaml"));
        Assert.Equal("TextBox", found.Element);
        Assert.Contains("AutomationProperties.Name", found.Reason, StringComparison.Ordinal);
        Assert.Contains("Test.xaml:", found.ToString(), StringComparison.Ordinal);
        Assert.True(found.Line > 0, "в отчёте нет строки — искать в разметке нечего");
    }

    [Fact]
    public void Имя_снимает_замечание()
    {
        string xaml = Wrap("""
            <TextBox AutomationProperties.Name="Поиск по журналу" Width="220" />
            <ComboBox AutomationProperties.Name="Тип записи" Width="160" />
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Имя_из_привязки_принимается()
    {
        // У переключателя твика подписи нет: название стоит рядом надписью, и оно же — имя.
        string xaml = Wrap("""
            <CheckBox AutomationProperties.Name="{Binding TitleRu}" IsChecked="{Binding IsOn}" />
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Список_без_имени_находится()
    {
        string xaml = Wrap("""
            <ComboBox Width="340" ItemsSource="{Binding Rows}" DisplayMemberPath="TitleRu" />
            """);

        UnnamedControl found = Assert.Single(AccessibleName.Find(xaml, "Test.xaml"));
        Assert.Equal("ComboBox", found.Element);
    }

    [Fact]
    public void Имя_взятое_у_надписи_принимается()
    {
        string xaml = Wrap("""
            <TextBox AutomationProperties.LabeledBy="{Binding ElementName=AddressLabel}" />
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Метка_через_Label_Target_считается_именем()
    {
        string xaml = Wrap("""
            <StackPanel>
              <Label Target="{Binding ElementName=SearchBox}" Content="Поиск по твикам" />
              <TextBox x:Name="SearchBox" Width="300" />
            </StackPanel>
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Кнопка_с_текстом_имени_не_требует()
    {
        string xaml = Wrap("""
            <StackPanel>
              <Button Content="Применить" />
              <Button Content="{Binding ApplyButtonRu}" />
              <RadioButton Content="Игры" />
            </StackPanel>
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Кнопка_с_панелью_без_текста_находится()
    {
        // Значок без подписи: диктор назовёт такую кнопку одним словом «кнопка».
        string xaml = Wrap("""
            <Button>
              <StackPanel>
                <Ellipse Width="10" Height="10" />
              </StackPanel>
            </Button>
            """);

        UnnamedControl found = Assert.Single(AccessibleName.Find(xaml, "Test.xaml"));
        Assert.Equal("Button", found.Element);
        Assert.Contains("в содержимом нет текста", found.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Кнопка_с_надписью_внутри_панели_проходит()
    {
        string xaml = Wrap("""
            <Button>
              <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding OpenButtonRu}" />
              </StackPanel>
            </Button>
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Внутри_шаблона_стиля_правило_молчит()
    {
        // Стрелки полосы прокрутки и заголовок раскрывашки — внутренности шаблона: имя им даёт он,
        // а не разметка раздела, и требовать его в разметке не за что.
        string xaml = Wrap("""
            <Style x:Key="ScrollBarPageButton" TargetType="RepeatButton">
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="RepeatButton">
                    <Border Background="Transparent" />
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            <ControlTemplate TargetType="Expander">
              <ToggleButton Content="{TemplateBinding Header}" />
            </ControlTemplate>
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Свёрнутое_правило_пропускает()
    {
        // Свёрнутого диктор не читает: пока элемент не показали, его нет в дереве автоматизации.
        string xaml = Wrap("""
            <StackPanel Visibility="Collapsed">
              <TextBox Width="220" />
            </StackPanel>
            <TextBox Width="160" Visibility="Hidden" />
            """);

        Assert.Empty(AccessibleName.Find(xaml, "Test.xaml"));
    }

    [Fact]
    public void Битая_разметка_не_роняет_правило()
    {
        Assert.Empty(AccessibleName.Find("<StackPanel", "Test.xaml"));
    }

    [Fact]
    public void В_настоящей_разметке_у_всех_элементов_есть_имя()
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

            problems.AddRange(AccessibleName
                .Find(File.ReadAllText(path), file.Replace("checked-", string.Empty, StringComparison.Ordinal))
                .Select(problem => problem.ToString()));
        }

        Assert.Empty(problems);
    }
}
