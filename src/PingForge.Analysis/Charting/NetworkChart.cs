using System.Globalization;

namespace PingForge.Analysis.Charting;

/// <summary>
/// Показатель графика сети. Список закрытый: это обязательный минимум, который программа обязана
/// показать или честно сказать «измерить не удалось».
/// </summary>
public enum ChartIndicator
{
    /// <summary>Скорость приёма, Мбит/с.</summary>
    DownloadThroughput = 0,

    /// <summary>Скорость отдачи, Мбит/с.</summary>
    UploadThroughput = 1,

    /// <summary>Минимальная задержка за окно, мс.</summary>
    RttMinMs = 2,

    /// <summary>Средняя задержка за окно, мс.</summary>
    RttAvgMs = 3,

    /// <summary>95-й процентиль задержки: показывает «плохие» моменты, а не средний комфорт.</summary>
    RttP95Ms = 4,

    /// <summary>Джиттер (MAD), мс.</summary>
    JitterMadMs = 5,

    /// <summary>Потери пакетов, %.</summary>
    PacketLossPercent = 6,

    /// <summary>Повторы TCP на 1000 пакетов.</summary>
    TcpRetransmitPermille = 7,

    /// <summary>Задержка под нагрузкой (буферблоат): разница «простой / загруженный канал», мс.</summary>
    LoadedLatencyMs = 8,

    /// <summary>Время ответа DNS, мс.</summary>
    DnsResponseMs = 9,

    /// <summary>MTU пути, байт: не усредняется — берётся последнее подтверждённое значение.</summary>
    PathMtuBytes = 10,
}

/// <summary>Как сворачивать ряд в одно число на подписи графика.</summary>
public enum ChartAggregation
{
    /// <summary>Среднее по измеренным точкам (пропуски не считаются нулями).</summary>
    Average = 0,

    /// <summary>Последнее измеренное значение: так показывают MTU и другие «состояния», а не потоки.</summary>
    Last = 1,
}

/// <summary>Один замер: все поля необязательные — если измерить не удалось, остаётся <c>null</c>, а не ноль.</summary>
public sealed record ChartSample(
    DateTimeOffset At,
    double? DownloadMbps = null,
    double? UploadMbps = null,
    double? RttMinMs = null,
    double? RttAvgMs = null,
    double? RttP95Ms = null,
    double? JitterMadMs = null,
    double? PacketLossPercent = null,
    double? TcpRetransmitPermille = null,
    double? LoadedLatencyMs = null,
    double? DnsResponseMs = null,
    double? PathMtuBytes = null)
{
    /// <summary>Пустой замер нужного времени: точка графика, где данных нет.</summary>
    public static ChartSample Empty(DateTimeOffset at) => new(at);

    /// <summary>Есть ли в замере хоть одно число.</summary>
    public bool HasAnyValue =>
        DownloadMbps is not null || UploadMbps is not null || RttMinMs is not null || RttAvgMs is not null ||
        RttP95Ms is not null || JitterMadMs is not null || PacketLossPercent is not null ||
        TcpRetransmitPermille is not null || LoadedLatencyMs is not null || DnsResponseMs is not null ||
        PathMtuBytes is not null;
}

/// <summary>Точка ряда: значение может быть неизвестным — тогда <see cref="IsMeasured"/> = <c>false</c>.</summary>
public sealed record ChartPoint(DateTimeOffset At, double? Value)
{
    /// <summary>Значение измерено, а не подставлено.</summary>
    public bool IsMeasured => Value is not null;
}

/// <summary>Ряд одного показателя для графика.</summary>
public sealed record ChartSeries(
    ChartIndicator Indicator,
    string TitleRu,
    string UnitRu,
    string WhyRu,
    ChartAggregation Aggregation,
    IReadOnlyList<ChartPoint> Points,
    string? UnavailableReasonRu = null)
{
    /// <summary>Сколько точек реально измерено.</summary>
    public int MeasuredCount => Points.Count(p => p.IsMeasured);

    /// <summary>Всего точек в окне наблюдения.</summary>
    public int TotalCount => Points.Count;

    /// <summary>Ряд пуст: показывать нечего, нужна честная плашка «измерить не удалось».</summary>
    public bool IsEmpty => MeasuredCount == 0;

    /// <summary>Минимум по измеренным точкам.</summary>
    public double? Min => IsEmpty ? null : Points.Where(p => p.IsMeasured).Min(p => p.Value);

    /// <summary>Максимум по измеренным точкам.</summary>
    public double? Max => IsEmpty ? null : Points.Where(p => p.IsMeasured).Max(p => p.Value);

    /// <summary>Среднее по измеренным точкам: пропуски не тянут среднее к нулю.</summary>
    public double? Average => IsEmpty ? null : Points.Where(p => p.IsMeasured).Average(p => p.Value);

    /// <summary>Последнее измеренное значение.</summary>
    public double? LastValue => Points.LastOrDefault(p => p.IsMeasured)?.Value;

    /// <summary>Значение для подписи: по правилу ряда (среднее или последнее).</summary>
    public double? SummaryValue => Aggregation == ChartAggregation.Average ? Average : LastValue;

    /// <summary>Замеренный 95-й процентиль: используется только когда сам ряд — процентиль задержки.</summary>
    public double? Percentile95
    {
        get
        {
            var values = Points.Where(p => p.IsMeasured).Select(p => p.Value!.Value).OrderBy(v => v).ToList();
            if (values.Count == 0)
            {
                return null;
            }

            var index = (int)Math.Ceiling(values.Count * 0.95) - 1;
            return values[Math.Clamp(index, 0, values.Count - 1)];
        }
    }

    /// <summary>Подпись ряда для интерфейса: значение, единица и честность выборки.</summary>
    public string DescribeRu()
    {
        if (IsEmpty)
        {
            return UnavailableReasonRu is null
                ? $"{TitleRu}: измерить не удалось — данных нет."
                : $"{TitleRu}: измерить не удалось ({UnavailableReasonRu}).";
        }

        var value = SummaryValue ?? 0;
        var number = value.ToString("0.##", CultureInfo.InvariantCulture);
        var tail = MeasuredCount == TotalCount
            ? $"измерено {MeasuredCount} из {TotalCount}"
            : $"измерено {MeasuredCount} из {TotalCount} — остальное пропущено, в нули не превращено";
        return $"{TitleRu}: {number} {UnitRu} ({tail}, {AggregationRu}).";
    }

    /// <summary>Словесное правило свёртки — чтобы в интерфейсе не приходилось угадывать.</summary>
    public string AggregationRu => Aggregation == ChartAggregation.Average ? "среднее по замерам" : "последнее значение";

    /// <summary>Ряд, который честно помечается как неизмеренный.</summary>
    public static ChartSeries Unavailable(ChartIndicator indicator, string reasonRu) =>
        new(indicator, NetworkChart.RequiredIndicators.First(i => i.Indicator == indicator).TitleRu,
            NetworkChart.RequiredIndicators.First(i => i.Indicator == indicator).UnitRu,
            NetworkChart.RequiredIndicators.First(i => i.Indicator == indicator).WhyRu,
            NetworkChart.RequiredIndicators.First(i => i.Indicator == indicator).Aggregation,
            Array.Empty<ChartPoint>(), reasonRu);
}

/// <summary>Описание обязательного показателя графика.</summary>
public sealed record ChartIndicatorInfo(
    ChartIndicator Indicator,
    string TitleRu,
    string UnitRu,
    string WhyRu,
    ChartAggregation Aggregation);

/// <summary>Готовый набор рядов: то, что рисует интерфейс.</summary>
public sealed record ChartPack(
    IReadOnlyList<ChartSeries> Series,
    TimeSpan Window,
    int DeclaredSamples,
    IReadOnlyList<string> NotesRu)
{
    /// <summary>Ряды, по которым действительно есть данные.</summary>
    public IReadOnlyList<ChartSeries> Measured => Series.Where(s => !s.IsEmpty).ToList();

    /// <summary>Ряды без данных — их надо показать плашкой, а не нулём.</summary>
    public IReadOnlyList<ChartSeries> Unavailable => Series.Where(s => s.IsEmpty).ToList();

    /// <summary>Честная подпись окна: без неё любые цифры ничего не значат.</summary>
    public string WindowRu
    {
        get
        {
            var seconds = (int)Math.Round(Window.TotalSeconds);
            return $"окно наблюдения: {seconds} с, точек в окне: {DeclaredSamples}, рядов с данными: {Measured.Count} из {Series.Count}";
        }
    }
}

/// <summary>
/// Сборка рядов графика из истории замеров. Правило одно и жёсткое: нет измерения — <c>null</c>,
/// а не ноль. Ноль на графике выглядел бы как идеальный результат.
/// </summary>
public static class NetworkChartBuilder
{
    /// <summary>Собрать набор рядов за окно наблюдения.</summary>
    public static ChartPack Build(
        IReadOnlyList<ChartSample> samples,
        TimeSpan window,
        string? unavailableReasonRu = null,
        int? declaredSamples = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples.OrderBy(s => s.At).ToList();
        var declared = declaredSamples ?? ordered.Count;
        var notes = new List<string>
        {
            "Пропущенный замер не превращается в ноль: на графике остаётся разрыв и подпись «измерить не удалось».",
            "Среднее считается только по измеренным точкам, поэтому редкие провалы видно, а не «размазано».",
        };

        if (ordered.Count == 0)
        {
            notes.Add(unavailableReasonRu is null
                ? "Замеров пока не было, поэтому все ряды помечены как неизмеренные."
                : $"Замеров пока не было: {unavailableReasonRu}.");
            return new ChartPack(
                NetworkChart.RequiredIndicators
                    .Select(i => ChartSeries.Unavailable(i.Indicator, unavailableReasonRu ?? "замеров не было"))
                    .ToList(),
                window, declared, notes);
        }

        var series = NetworkChart.RequiredIndicators
            .Select(i => BuildSeries(i, ordered, unavailableReasonRu))
            .ToList();

        var skipped = declared - ordered.Count;
        if (skipped > 0)
        {
            notes.Add($"В окне ожидалось {declared} проб, дошло {ordered.Count}: {skipped} пропущено — это тоже видно на графике.");
        }

        return new ChartPack(series, window, declared, notes);
    }

    private static ChartSeries BuildSeries(
        ChartIndicatorInfo info,
        IReadOnlyList<ChartSample> samples,
        string? unavailableReasonRu)
    {
        var points = samples
            .Select(s => new ChartPoint(s.At, Extract(info.Indicator, s)))
            .ToList();

        if (points.All(p => !p.IsMeasured))
        {
            return new ChartSeries(info.Indicator, info.TitleRu, info.UnitRu, info.WhyRu, info.Aggregation,
                points, unavailableReasonRu ?? "этот показатель в замерах не приходил");
        }

        return new ChartSeries(info.Indicator, info.TitleRu, info.UnitRu, info.WhyRu, info.Aggregation, points);
    }

    private static double? Extract(ChartIndicator indicator, ChartSample sample) => indicator switch
    {
        ChartIndicator.DownloadThroughput => sample.DownloadMbps,
        ChartIndicator.UploadThroughput => sample.UploadMbps,
        ChartIndicator.RttMinMs => sample.RttMinMs,
        ChartIndicator.RttAvgMs => sample.RttAvgMs,
        ChartIndicator.RttP95Ms => sample.RttP95Ms,
        ChartIndicator.JitterMadMs => sample.JitterMadMs,
        ChartIndicator.PacketLossPercent => sample.PacketLossPercent,
        ChartIndicator.TcpRetransmitPermille => sample.TcpRetransmitPermille,
        ChartIndicator.LoadedLatencyMs => sample.LoadedLatencyMs,
        ChartIndicator.DnsResponseMs => sample.DnsResponseMs,
        ChartIndicator.PathMtuBytes => sample.PathMtuBytes,
        _ => null,
    };
}

/// <summary>Список обязательных показателей графика: из него строится и легенда, и справка.</summary>
public static class NetworkChart
{
    /// <summary>Обязательный минимум показателей — тот же список попадает в интерфейс и в справку.</summary>
    public static readonly IReadOnlyList<ChartIndicatorInfo> RequiredIndicators = new List<ChartIndicatorInfo>
    {
        new(ChartIndicator.DownloadThroughput, "Скорость приёма", "Мбит/с",
            "Показывает, что оптимизация не отняла пропускную способность: скорость не должна падать.", ChartAggregation.Average),
        new(ChartIndicator.UploadThroughput, "Скорость отдачи", "Мбит/с",
            "Нужна для голосового чата и античита: провал отдачи слышно в микрофоне раньше, чем видно на пинге.", ChartAggregation.Average),
        new(ChartIndicator.RttMinMs, "Задержка: минимум", "мс",
            "Лучшее, что физически возможно на этом маршруте: по нему видно, есть ли запас вообще.", ChartAggregation.Average),
        new(ChartIndicator.RttAvgMs, "Задержка: средняя", "мс",
            "Обычный пинг, который видно в игре.", ChartAggregation.Average),
        new(ChartIndicator.RttP95Ms, "Задержка: 95-й процентиль", "мс",
            "Худшие 5 % моментов: из-за них ощущаются рывки, хотя среднее может быть красивым.", ChartAggregation.Average),
        new(ChartIndicator.JitterMadMs, "Джиттер (MAD)", "мс",
            "Разброс задержки, устойчивый к одиночным скачкам: главная причина «плавающего» отклика.", ChartAggregation.Average),
        new(ChartIndicator.PacketLossPercent, "Потери пакетов", "%",
            "Даже полпроцента потерь ощущаются как телепорты: пакет не дошёл — игра досылает его позже.", ChartAggregation.Average),
        new(ChartIndicator.TcpRetransmitPermille, "Повторы TCP", "на 1000 пакетов",
            "Повторная отправка занимает путь дважды: чем меньше повторов, тем ровнее соединение.", ChartAggregation.Average),
        new(ChartIndicator.LoadedLatencyMs, "Задержка под нагрузкой", "мс",
            "Показывает буферблоат: если закачка задирает пинг на сотни миллисекунд, виноват буфер, а не провайдер.", ChartAggregation.Average),
        new(ChartIndicator.DnsResponseMs, "Время ответа DNS", "мс",
            "Влияет на вход в лобби и поиск матча, а не на бой: это честно сказано и в интерфейсе.", ChartAggregation.Average),
        new(ChartIndicator.PathMtuBytes, "MTU пути", "байт",
            "Не усредняется: важно последнее подтверждённое значение, иначе фрагментация вернётся незаметно.", ChartAggregation.Last),
    };

    /// <summary>Короткая справка для интерфейса: что именно обязательно показывает график.</summary>
    public static string DescribeRu() =>
        $"График обязан показать {RequiredIndicators.Count} показателей или честно сказать «измерить не удалось»: " +
        string.Join(", ", RequiredIndicators.Select(i => i.TitleRu.ToLowerInvariant())) + ".";

    /// <summary>Название показателя для интерфейса.</summary>
    public static string TitleOf(ChartIndicator indicator) =>
        RequiredIndicators.First(i => i.Indicator == indicator).TitleRu;

    /// <summary>Единица измерения для подписи оси.</summary>
    public static string UnitOf(ChartIndicator indicator) =>
        RequiredIndicators.First(i => i.Indicator == indicator).UnitRu;
}
