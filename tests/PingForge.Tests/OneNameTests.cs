using System.Text.RegularExpressions;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Одна программа, одно имя наружу: «PingForge» — техническое имя службы и её папки данных, а не второй продукт.
/// </summary>
/// <remarks>
/// Имя `PingForge` осталось от первых версий в трёх местах: имя службы, её канал и папка данных
/// (`%ProgramData%\PingForge`). Переименовывать их нельзя без потери журнала и снапшотов у тех, кто уже
/// обновился, поэтому решение записано словами: наружу программа одна. Эти тесты держат слова на месте —
/// если README начнёт обещать «вторую программу», а служба в списке служб назовётся чужим именем, прогон упадёт.
/// </remarks>
public sealed class OneNameTests
{
    private const string ProductName = "Custom Tweak By Doxaiz";

    [Fact]
    public void README_говорит_что_программа_одна()
    {
        string readme = Read("checked-README.md");

        Assert.Contains($"Программа одна — **{ProductName}**", readme, StringComparison.Ordinal);
        Assert.Contains("Имя **PingForge** осталось техническим", readme, StringComparison.Ordinal);
        Assert.Contains(@"%ProgramData%\PingForge", readme, StringComparison.Ordinal);
        Assert.Contains($"{ProductName} — служба", readme, StringComparison.Ordinal);
        Assert.Contains("отложено осознанно", readme, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("checked-README.md")]
    [InlineData("checked-architecture.md")]
    public void В_документах_нет_второй_программы(string file)
    {
        string text = Read(file);

        foreach (string claim in new[] { "PingForge Lite", "PingForge Pro" })
        {
            Assert.DoesNotContain(claim, text, StringComparison.Ordinal);
        }

        Assert.Contains(ProductName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Архитектура_объясняет_почему_техническое_имя_осталось()
    {
        string architecture = Read("checked-architecture.md");

        Assert.Contains("### Одно имя", architecture, StringComparison.Ordinal);
        Assert.Contains("техническим", architecture, StringComparison.Ordinal);
        Assert.Contains("потерялись бы", architecture, StringComparison.Ordinal);
    }

    [Fact]
    public void Справка_службы_называет_себя_частью_программы()
    {
        string service = Read("checked-ServiceProgram.cs");

        Assert.Contains($"{ProductName} — служба программы.", service, StringComparison.Ordinal);
        Assert.Contains("техническое", service, StringComparison.Ordinal);
        Assert.Contains("Отдельной программы нет", service, StringComparison.Ordinal);
        Assert.DoesNotContain("служба PingForge", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Видимое_имя_службы_это_имя_программы()
    {
        string installer = Read("checked-install-service.ps1");

        Assert.Contains($"$displayName = '{ProductName} — служба'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("PingForge (Custom", installer, StringComparison.Ordinal);

        // Описание службы — единственное место в списке служб, где можно объяснить техническое имя.
        Assert.Contains("техническое имя службы и её папки данных — PingForge", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void В_окне_нет_второго_имени()
    {
        string[] windows = Directory.GetFiles(AppContext.BaseDirectory, "checked-*.xaml");
        Assert.NotEmpty(windows);

        foreach (string path in windows)
        {
            // Комментарии в разметке человек не видит: `PingForge.Analysis` в пояснении — это имя слоя,
            // а не надпись в окне. Правило — про видимый текст, поэтому комментарии вырезаются заранее,
            // а переводы строк внутри них остаются: иначе номер строки в сообщении уедет.
            string text = Regex.Replace(
                File.ReadAllText(path),
                "<!--.*?-->",
                match => new string('\n', match.Value.Count(c => c == '\n')),
                RegexOptions.Singleline);
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("PingForge", StringComparison.Ordinal))
                {
                    continue;
                }

                // В разметке «PingForge» живёт только в имени класса: заголовок, подписи и тексты — не его.
                bool isClassName = lines[i].Contains("x:Class", StringComparison.Ordinal)
                    || lines[i].Contains("clr-namespace", StringComparison.Ordinal);
                Assert.True(isClassName, $"{Path.GetFileName(path)}:{i + 1}: имя программы просочилось в разметку: {lines[i].Trim()}");
            }

            Assert.DoesNotMatch(new Regex(@"\bLite\b"), string.Join('\n', lines));
        }
    }

    private static string Read(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"нет копии {name} для проверки: {path}");
        return File.ReadAllText(path);
    }
}
