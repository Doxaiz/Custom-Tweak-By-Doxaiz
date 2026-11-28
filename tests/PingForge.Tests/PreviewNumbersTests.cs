using System.Text.RegularExpressions;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Числа в макете интерфейса: те, что можно посчитать, считаются; те, что не посчитать, честно называются неизвестными.
/// </summary>
/// <remarks>
/// В разделе «Что уже готово» полтора года стояло «169 автотестов», хотя настоящих тестов давно больше тысячи:
/// цифру вписали руками при 169 и больше не трогали. Рядом стояло утверждение «веса в сумме дают ровно сто
/// процентов, итог считается по весам» — тоже словами, без проверки на самой странице. Теперь число автотестов приходит
/// из журнала прогона (`--tests` из TRX), а сумма весов и итог проверяются пересчётом по таблице страницы.
/// Эти тесты держат оба правила в исходниках: вписанная цифра и подпись без проверки не вернутся.
/// </remarks>
public sealed class PreviewNumbersTests
{
    private const string RunPhrase = "берётся из журнала прогона";

    [Fact]
    public void Макет_не_вписывает_число_автотестов_руками()
    {
        string renderer = Read("checked-render-ui-preview.py");

        // «169 автотестов», «1147 автотестов» — любая цифра, приклеенная к слову, снова станет неправдой.
        Assert.DoesNotMatch(new Regex(@"\d+\s*автотест"), renderer);

        // Число приходит аргументом, оба вида записи поддерживаются, а на странице живёт в атрибуте.
        Assert.Contains("\"--tests\"", renderer, StringComparison.Ordinal);
        Assert.Contains("\"--tests=\"", renderer, StringComparison.Ordinal);
        Assert.Contains("data-tests", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void Без_журнала_прогона_макет_говорит_что_числа_нет()
    {
        string renderer = Read("checked-render-ui-preview.py");

        Assert.Contains(RunPhrase, renderer, StringComparison.Ordinal);
        Assert.Contains("тесты проверяют", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void Проверка_страницы_сверяет_подпись_с_атрибутом()
    {
        string checker = Read("checked-check-ui-preview.js");

        Assert.Contains("querySelector('[data-tests]')", checker, StringComparison.Ordinal);
        Assert.Contains("getAttribute('data-tests')", checker, StringComparison.Ordinal);

        // Дробное или отрицательное число тестов — не число тестов.
        Assert.Contains(@"\d+", checker, StringComparison.Ordinal);
        Assert.Contains("Number(testsAttr) >= 100", checker, StringComparison.Ordinal);
    }

    [Fact]
    public void Итог_готовности_проверяется_по_таблице_страницы()
    {
        string checker = Read("checked-check-ui-preview.js");

        // Подпись «итог считается по весам» проверяется пересчётом, а не доверием.
        Assert.Contains("weightsTotal === 100", checker, StringComparison.Ordinal);
        Assert.Contains("#progress .score", checker, StringComparison.Ordinal);
        Assert.Contains("по формуле", checker, StringComparison.Ordinal);
    }

    [Fact]
    public void Сборка_передаёт_число_тестов_из_TRX()
    {
        string build = Read("checked-build.yml");

        Assert.Contains("Counters.total", build, StringComparison.Ordinal);
        Assert.Contains("--tests $tests", build, StringComparison.Ordinal);
    }

    private static string Read(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"нет копии {name} для проверки: {path}");
        return File.ReadAllText(path);
    }
}
