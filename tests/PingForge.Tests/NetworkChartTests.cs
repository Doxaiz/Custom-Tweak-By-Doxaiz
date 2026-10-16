using PingForge.Analysis.Charting;
using Xunit;

namespace PingForge.Tests;

public sealed class NetworkChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static ChartSample Sample(int second, double? ping = null, double? jitter = null, double? loss = null) =>
        new(Start.AddSeconds(second), RttAvgMs: ping, JitterMadMs: jitter, PacketLossPercent: loss);

    [Fact]
    public void Обязательный_минимум_показателей_объявлен_и_описан()
    {
        Assert.Equal(11, NetworkChart.RequiredIndicators.Count);
        Assert.All(NetworkChart.RequiredIndicators, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.TitleRu));
            Assert.False(string.IsNullOrWhiteSpace(i.UnitRu));
            Assert.False(string.IsNullOrWhiteSpace(i.WhyRu));
        });
        Assert.Contains("mtu", NetworkChart.DescribeRu());
        Assert.Equal("MTU пути", NetworkChart.TitleOf(ChartIndicator.PathMtuBytes));
        Assert.Equal("мс", NetworkChart.UnitOf(ChartIndicator.RttAvgMs));
    }

    [Fact]
    public void Без_замеров_все_ряды_помечены_как_неизмеренные_и_нигде_нет_нулей()
    {
        var pack = NetworkChartBuilder.Build(Array.Empty<ChartSample>(), TimeSpan.FromSeconds(60),
            unavailableReasonRu: "замер не запускали");

        Assert.Equal(NetworkChart.RequiredIndicators.Count, pack.Series.Count);
        Assert.All(pack.Series, s => Assert.True(s.IsEmpty));
        Assert.All(pack.Series, s => Assert.Null(s.SummaryValue));
        Assert.All(pack.Series, s => Assert.Contains("измерить не удалось", s.DescribeRu()));
        Assert.All(pack.Series, s => Assert.Contains("замер не запускали", s.DescribeRu()));
        Assert.Empty(pack.Measured);
        Assert.Equal(NetworkChart.RequiredIndicators.Count, pack.Unavailable.Count);
    }

    [Fact]
    public void Пропуски_в_середине_не_превращаются_в_ноль_и_не_тянут_среднее()
    {
        var samples = new[]
        {
            Sample(0, ping: 30),
            ChartSample.Empty(Start.AddSeconds(1)),
            Sample(2, ping: 50),
        };

        var series = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(3))
            .Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs);

        Assert.Equal(3, series.TotalCount);
        Assert.Equal(2, series.MeasuredCount);
        Assert.Equal(40, series.Average);
        Assert.Equal(30, series.Min);
        Assert.Equal(50, series.Max);
        Assert.Null(series.Points[1].Value);
        Assert.False(series.Points[1].IsMeasured);
        Assert.Contains("остальное пропущено", series.DescribeRu());
    }

    [Fact]
    public void Процентиль_95_считается_по_измеренным_точкам()
    {
        var samples = Enumerable.Range(0, 20).Select(i => Sample(i, ping: 20 + i)).ToArray();

        var series = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(20))
            .Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs);

        Assert.Equal(38, series.Percentile95);
        Assert.Equal(29.5, series.Average);
    }

    [Fact]
    public void MTU_берётся_последним_значением_а_не_средним()
    {
        var samples = new[]
        {
            new ChartSample(Start, PathMtuBytes: 1480),
            new ChartSample(Start.AddSeconds(1), PathMtuBytes: 1500),
            new ChartSample(Start.AddSeconds(2), PathMtuBytes: 1492),
        };

        var series = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(3))
            .Series.Single(s => s.Indicator == ChartIndicator.PathMtuBytes);

        Assert.Equal(ChartAggregation.Last, series.Aggregation);
        Assert.Equal(1492, series.SummaryValue);
        Assert.Equal(1490.67, Math.Round(series.Average!.Value, 2));
        Assert.Contains("последнее значение", series.DescribeRu());
    }

    [Fact]
    public void Ряд_без_своих_данных_честно_объясняет_причину()
    {
        var samples = new[] { Sample(0, ping: 30), Sample(1, ping: 31) };

        var pack = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(2));
        var speed = pack.Series.Single(s => s.Indicator == ChartIndicator.DownloadThroughput);

        Assert.True(speed.IsEmpty);
        Assert.Contains("этот показатель в замерах не приходил", speed.DescribeRu());
        Assert.Single(pack.Unavailable.Select(s => s.Indicator), ChartIndicator.DownloadThroughput);
    }

    [Fact]
    public void Окно_наблюдения_и_число_проб_подписаны()
    {
        var samples = new[] { Sample(0, ping: 30), Sample(1, ping: 32) };

        var pack = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(60), declaredSamples: 12);

        Assert.Equal("окно наблюдения: 60 с, точек в окне: 12, рядов с данными: 1 из 11", pack.WindowRu);
        Assert.Contains(pack.NotesRu, n => n.Contains("ожидалось 12 проб"));
        Assert.Contains(pack.NotesRu, n => n.Contains("не превращается в ноль"));
    }

    [Fact]
    public void Лишних_проб_не_считаем_но_и_не_молчим()
    {
        var samples = Enumerable.Range(0, 15).Select(i => Sample(i, ping: 30 + i)).ToArray();

        var pack = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(15), declaredSamples: 15);

        Assert.DoesNotContain(pack.NotesRu, n => n.Contains("пропущено"));
        Assert.Equal(15, pack.Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs).MeasuredCount);
    }

    [Fact]
    public void Скорость_не_падает_и_сравнение_до_после_видно_числами()
    {
        var before = Enumerable.Range(0, 5).Select(i => new ChartSample(Start.AddSeconds(i),
            DownloadMbps: 89 + i, RttAvgMs: 40 + i, JitterMadMs: 5 + (i * 0.2))).ToArray();
        var after = Enumerable.Range(0, 5).Select(i => new ChartSample(Start.AddSeconds(i),
            DownloadMbps: 91 + i, RttAvgMs: 30 + i, JitterMadMs: 2 + (i * 0.1))).ToArray();

        var packBefore = NetworkChartBuilder.Build(before, TimeSpan.FromSeconds(5));
        var packAfter = NetworkChartBuilder.Build(after, TimeSpan.FromSeconds(5));

        var speedBefore = packBefore.Series.Single(s => s.Indicator == ChartIndicator.DownloadThroughput).Average!.Value;
        var speedAfter = packAfter.Series.Single(s => s.Indicator == ChartIndicator.DownloadThroughput).Average!.Value;
        var pingBefore = packBefore.Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs).Average!.Value;
        var pingAfter = packAfter.Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs).Average!.Value;
        var jitterBefore = packBefore.Series.Single(s => s.Indicator == ChartIndicator.JitterMadMs).Average!.Value;
        var jitterAfter = packAfter.Series.Single(s => s.Indicator == ChartIndicator.JitterMadMs).Average!.Value;

        Assert.True(speedAfter >= speedBefore, "скорость не должна падать после оптимизации");
        Assert.True(pingAfter < pingBefore, "задержка должна снижаться, если выигрыш подтверждён");
        Assert.True(jitterAfter < jitterBefore, "джиттер должен снижаться вместе с задержкой");
    }

    [Fact]
    public void Потери_считаются_по_каждой_пробе_и_не_скрываются_усреднением()
    {
        var samples = new[]
        {
            Sample(0, loss: 0), Sample(1, loss: 0), Sample(2, loss: 3.4), Sample(3, loss: 0),
        };

        var series = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(4))
            .Series.Single(s => s.Indicator == ChartIndicator.PacketLossPercent);

        Assert.Equal(3.4, series.Max);
        Assert.Equal(0, series.Min);
        Assert.True(series.Average > 0, "средние потери не могут быть обнулены");
    }

    [Fact]
    public void Подпись_ряда_содержит_единицу_измерения_и_правило_свёртки()
    {
        var samples = new[] { Sample(0, jitter: 3.0), Sample(1, jitter: 4.0) };

        var series = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(2))
            .Series.Single(s => s.Indicator == ChartIndicator.JitterMadMs);

        Assert.Contains("мс", series.DescribeRu());
        Assert.Contains("среднее по замерам", series.AggregationRu);
        Assert.Contains("измерено 2 из 2", series.DescribeRu());
    }

    [Fact]
    public void Порядок_точек_нормализуется_по_времени()
    {
        var samples = new[]
        {
            Sample(2, ping: 40),
            Sample(0, ping: 20),
            Sample(1, ping: 30),
        };

        var series = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(3))
            .Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs);

        Assert.Equal(new double?[] { 20, 30, 40 }, series.Points.Select(p => p.Value).ToArray());
        Assert.All(series.Points.Zip(series.Points.Skip(1)), pair =>
            Assert.True(pair.First.At <= pair.Second.At));
    }
}
