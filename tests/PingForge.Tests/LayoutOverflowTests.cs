using PingForge.Core.Layout;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Полоса содержимого окна прокручивается только вниз, поэтому всё, что шире окна, обрезано справа.
/// Проверка «ничего не вышло за край» решает это по размерам, и решение проверяется здесь — без окна
/// и без Windows: на сборочной машине живую раскладку меряет самопроверка, а правила — тесты.
/// </summary>
/// <remarks>
/// Дефект известен по снимкам окна: карточки упирались в правый край и обрезались. Тогда ширину
/// сравнивали глазами по картинке; теперь тот же вопрос задаётся числами.
/// </remarks>
public sealed class LayoutOverflowTests
{
    [Fact]
    public void Элемент_в_пределах_окна_не_считается_дефектом()
    {
        LayoutBox[] boxes = [new("Панель твиков", 236, 788)];

        IReadOnlyList<LayoutBox> found = LayoutOverflow.Find(boxes, limit: 1024);

        Assert.Empty(found);
    }

    [Fact]
    public void Элемент_за_краем_называется_с_именем_и_правым_краем()
    {
        LayoutBox[] boxes = [new("Панель твиков", 236, 806)];

        IReadOnlyList<LayoutBox> found = LayoutOverflow.Find(boxes, limit: 1024);

        LayoutBox box = Assert.Single(found);
        Assert.Equal("Панель твиков", box.Name);
        Assert.Equal(1042.0, box.Right, 3);
    }

    [Fact]
    public void Округление_на_полторы_точки_не_дефект()
    {
        // Раскладка WPF округляет до целых пикселей: край может выйти на полточки и вернуться.
        LayoutBox[] boxes = [new("Граница", 0, 1025.5)];

        Assert.Empty(LayoutOverflow.Find(boxes, limit: 1024));
    }

    [Fact]
    public void Выход_больше_допуска_дефект()
    {
        LayoutBox[] boxes = [new("Граница", 0, 1026.5)];

        Assert.Single(LayoutOverflow.Find(boxes, limit: 1024));
    }

    [Fact]
    public void Свёрнутый_элемент_не_считается_вышедшим_за_край()
    {
        // У свёрнутого или ещё не разложенного элемента ширина ноль. Это не «шире окна», это «нет его».
        LayoutBox[] boxes = [new("Скрытая страница", 0, 0), new("Не измерено", double.NaN, double.NaN)];

        Assert.Empty(LayoutOverflow.Find(boxes, limit: 1024));
    }

    [Fact]
    public void Если_окно_не_измерилось_виноватых_нет()
    {
        // Сравнивать не с чем: до первого прохода раскладки ActualWidth окна равен нулю.
        LayoutBox[] boxes = [new("Карточка", 0, 4000)];

        Assert.Empty(LayoutOverflow.Find(boxes, limit: 0));
        Assert.Empty(LayoutOverflow.Find(boxes, limit: double.NaN));
    }

    [Fact]
    public void Порядок_элементов_сохраняется_как_в_обходе_окна()
    {
        LayoutBox[] boxes =
        [
            new("Первый", 0, 2000),
            new("Второй", 0, 1024),
            new("Третий", 0, 1200),
        ];

        IReadOnlyList<LayoutBox> found = LayoutOverflow.Find(boxes, limit: 1024);

        Assert.Equal(new[] { "Первый", "Третий" }, found.Select(box => box.Name));
    }

    [Fact]
    public void Отчёт_называет_элемент_и_два_числа_точками_без_культуры()
    {
        // Числа печатаются без разделителя тысяч: «1 042» и «1042» — одна точка, но два разных отчёта.
        LayoutBox[] boxes = [new("Панель твиков", 236, 806)];

        string report = LayoutOverflow.DescribeRu(LayoutOverflow.Find(boxes, limit: 1024), 1024);

        Assert.Equal("Панель твиков — правый край 1042 при окне 1024", report);
    }

    [Fact]
    public void Отчёт_обрезает_список_и_пишет_сколько_ещё()
    {
        LayoutBox[] boxes =
        [
            new("Первый", 0, 2000),
            new("Второй", 0, 2000),
            new("Третий", 0, 2000),
            new("Четвёртый", 0, 2000),
            new("Пятый", 0, 2000),
            new("Шестой", 0, 2000),
            new("Седьмой", 0, 2000),
        ];

        string report = LayoutOverflow.DescribeRu(LayoutOverflow.Find(boxes, limit: 1024), 1024);

        Assert.Contains("Первый", report, StringComparison.Ordinal);
        Assert.Contains("Пятый", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Шестой", report, StringComparison.Ordinal);
        Assert.EndsWith("и ещё 2", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Пустой_отчёт_когда_всё_на_месте()
    {
        Assert.Equal(string.Empty, LayoutOverflow.DescribeRu(Array.Empty<LayoutBox>(), 1024));
    }
}
