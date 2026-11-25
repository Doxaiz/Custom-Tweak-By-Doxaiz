using System.Globalization;
using PingForge.Core.Statistics;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сторожа волны 51: выигрыш засчитывается только тогда, когда он выше шума замера.
///
/// Почему это не косметика: раньше итог сравнивал две медианы и при разнице больше 0,5 мс писал
/// «стало лучше на N мс». При разбросе ±3 мс такая фраза — та же зелёная галочка, только в цифрах:
/// человек видит «стало лучше», а на деле изменился шум. Здесь проверяется и расчёт значимости,
/// и то, что движок, самопроверка, окно и CI используют именно его.
/// </summary>
public sealed class GainVerdictTests
{
    /// <summary>Ряд с заданной медианой и разбросом: восьми значений хватает для вывода.</summary>
    private static List<double> Series(double center, double spread, int count = 8)
    {
        var values = new List<double>(count);
        for (int i = 0; i < count; i++)
        {
            double offset = spread == 0 ? 0 : (i % 2 == 0 ? -spread : spread) * (1 - (i / (double)count));
            values.Add(center + offset);
        }

        return values;
    }

    [Fact]
    public void Явный_выигрыш_подтверждается_интервалом_а_не_разницей_на_глаз()
    {
        GainVerdict verdict = GainVerdict.Evaluate(Series(120, 0), Series(80, 0));

        Assert.Equal(GainOutcome.Confirmed, verdict.Outcome);
        Assert.True(verdict.IsConfirmedGain);
        Assert.Equal(40, verdict.GainMs!.Value, 0.01);
        Assert.True(verdict.CiLowMs > GainVerdict.MeaningfulMs, "нижняя граница интервала должна быть выше порога значимости");
        Assert.Contains("Выигрыш подтверждён", verdict.SummaryRu, StringComparison.Ordinal);
        Assert.Contains("95% ДИ", verdict.SummaryRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Разница_в_пределах_шума_не_называется_выигрышем()
    {
        // Один и тот же шумный канал, «после» быстрее ровно на 1 мс: это ровно тот случай,
        // в котором старая логика с порогом 0,5 мс писала «стало лучше на 1 мс».
        List<double> before = Series(40, 6);
        List<double> after = before.Select(value => value - 1).ToList();

        GainVerdict verdict = GainVerdict.Evaluate(before, after);

        Assert.Equal(GainOutcome.WithinNoise, verdict.Outcome);
        Assert.False(verdict.IsConfirmedGain);
        Assert.True(verdict.CiLowMs < GainVerdict.MeaningfulMs, "интервал обязан накрывать ноль");
        Assert.Contains("Разница в пределах шума", verdict.SummaryRu, StringComparison.Ordinal);
        Assert.Contains("выигрышем это не считаем", verdict.SummaryRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Одинаковые_наборы_дают_нулевую_разницу_и_никакого_выигрыша()
    {
        List<double> series = Series(55, 4);
        GainVerdict verdict = GainVerdict.Evaluate(series, series.ToList());

        Assert.Equal(GainOutcome.WithinNoise, verdict.Outcome);
        Assert.Equal(0, verdict.GainMs!.Value, 0.0001);
        Assert.True(verdict.CiLowMs <= 0 && verdict.CiHighMs >= 0);
    }

    [Fact]
    public void Ухудшение_называется_ухудшением_а_не_выигрышем()
    {
        GainVerdict verdict = GainVerdict.Evaluate(Series(80, 0), Series(120, 0));

        Assert.Equal(GainOutcome.Worse, verdict.Outcome);
        Assert.Contains("Стало хуже", verdict.SummaryRu, StringComparison.Ordinal);
        Assert.True(verdict.CiHighMs < 0);
    }

    [Fact]
    public void Мало_окон_значит_нет_вывода()
    {
        GainVerdict verdict = GainVerdict.Evaluate([120, 118, 122], [80, 81, 79]);

        Assert.Equal(GainOutcome.NotMeasured, verdict.Outcome);
        Assert.Null(verdict.GainMs);
        Assert.Contains("не заявлен", verdict.SummaryRu, StringComparison.Ordinal);
        Assert.Contains(GainVerdict.MinimumSamples.ToString(CultureInfo.InvariantCulture), verdict.SummaryRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Ровно_минимум_окон_уже_даёт_вывод()
    {
        GainVerdict verdict = GainVerdict.Evaluate(
            Enumerable.Repeat(120.0, GainVerdict.MinimumSamples).ToList(),
            Enumerable.Repeat(80.0, GainVerdict.MinimumSamples).ToList());

        Assert.True(verdict.IsMeasured);
        Assert.Equal(GainVerdict.MinimumSamples, verdict.BeforeSamples);
    }

    [Fact]
    public void Стабильность_сравнивается_тем_же_способом_что_и_скорость()
    {
        GainVerdict steady = GainVerdict.Evaluate(
            Series(40, 0),
            Series(40, 0),
            jitterBefore: Series(8, 0),
            jitterAfter: Series(2, 0));

        Assert.Contains("Стабильность лучше", steady.StabilityRu, StringComparison.Ordinal);

        GainVerdict noise = GainVerdict.Evaluate(
            Series(40, 0),
            Series(40, 0),
            jitterBefore: Series(4, 0.6),
            jitterAfter: Series(4, 0.6));

        Assert.Contains("в пределах шума", noise.StabilityRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Без_измеренного_разброса_о_стабильности_молчим_честно()
    {
        GainVerdict verdict = GainVerdict.Evaluate(Series(120, 0), Series(80, 0));

        Assert.Contains("не сравнить", verdict.StabilityRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Вывод_не_зависит_от_запуска()
    {
        GainVerdict first = GainVerdict.Evaluate(Series(40, 5), Series(40, 5, 9));
        GainVerdict second = GainVerdict.Evaluate(Series(40, 5), Series(40, 5, 9));

        Assert.Equal(first.CiLowMs, second.CiLowMs);
        Assert.Equal(first.CiHighMs, second.CiHighMs);
        Assert.Equal(first.Outcome, second.Outcome);
    }

    [Fact]
    public void Мусор_в_замерах_не_превращается_в_выигрыш()
    {
        List<double> before = [120, double.NaN, 118, double.PositiveInfinity, -5, 122, 119, 121, 120, 119, 118, 120];
        List<double> after = [80, 81, double.NaN, 79, 80, -1, 82, 80, 81, 80, 79, 81];

        GainVerdict verdict = GainVerdict.Evaluate(before, after);

        Assert.True(verdict.IsMeasured);
        Assert.True(verdict.GainMs > 30, "выигрыш должен считаться по чистым замерам");
        Assert.Empty(GainVerdict.Evaluate([], []).SummaryRu.Where(char.IsControl));
        Assert.Equal(GainOutcome.NotMeasured, GainVerdict.Evaluate([], []).Outcome);
    }

    [Fact]
    public void Медиана_считается_как_в_статистике()
    {
        Assert.Equal(3, GainVerdict.Median([5, 1, 3]), 0.001);
        Assert.Equal(2.5, GainVerdict.Median([1, 2, 3, 4]), 0.001);
    }

    // ── Сторожа исходников: значимость должна использоваться, а не лежать рядом ──────────────

    private static string ReadChecked(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
        Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Движок_реального_времени_решает_по_шуму_а_не_по_порогу_полмиллисекунды()
    {
        string optimizer = ReadChecked("RealtimeOptimizer.cs");

        Assert.Contains("GainVerdict.Evaluate", optimizer, StringComparison.Ordinal);
        Assert.Contains("EvaluateGain()", optimizer, StringComparison.Ordinal);
        // Порога «0,5 мс» в движке больше нет: он и был источником фразы «стало лучше на 1 мс».
        Assert.DoesNotContain("0.5", optimizer, StringComparison.Ordinal);
        Assert.DoesNotContain("0,5 мс — выигрыш", optimizer, StringComparison.Ordinal);
    }

    [Fact]
    public void Самопроверка_меряет_шум_и_CI_требует_строку()
    {
        string selfCheck = ReadChecked("SelfCheck.cs");
        Assert.Contains("ШУМ:", selfCheck, StringComparison.Ordinal);
        Assert.Contains("GainVerdict.Evaluate", selfCheck, StringComparison.Ordinal);

        string workflow = ReadChecked("build.yml");
        Assert.Contains("ШУМ:", workflow, StringComparison.Ordinal);
        Assert.Contains("Строки «ШУМ:» нет в журнале", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Окно_не_хвастается_выигрышем_без_подтверждения()
    {
        string viewModel = ReadChecked("MainViewModel.cs");

        Assert.Contains("GainConfirmed", viewModel, StringComparison.Ordinal);
        Assert.Contains("разница в пределах шума", viewModel, StringComparison.Ordinal);
        // Фраза «стало лучше» обязана требовать подтверждения, а не просто числа.
        Assert.DoesNotContain("ImprovementMs is > 0.5", viewModel, StringComparison.Ordinal);
    }
}
