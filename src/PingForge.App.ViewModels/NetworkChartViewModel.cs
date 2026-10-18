using System.Globalization;
using PingForge.Analysis.Charting;

namespace PingForge.App.ViewModels;

/// <summary>Подпись на оси графика: координаты в пикселях канвы (не зависит от WPF).</summary>
public sealed record ChartAxisLabel(double X, double Y, string Text);

/// <summary>
/// Отрезок линии без разрывов: точки в пикселях канвы, строкой «x,y x,y …».
/// Разрыв измерения — новый отрезок: точка без данных не рисуется и в ноль не превращается.
/// </summary>
public sealed record ChartLineSegment(string PointsText, int LineKind);

/// <summary>
/// Вид линии: цвет выбирает разметка по этому номеру, а не по строке — так модель остаётся
/// без зависимостей от WPF, а палитра живёт в одном месте.
/// </summary>
public static class ChartLineKind
{
    public const int RttMin = 0;
    public const int RttAvg = 1;
    public const int RttP95 = 2;
    public const int Jitter = 3;
    public const int Loss = 4;
}

/// <summary>Одна панель графика: линии, оси и честная подпись. Пустая панель — тоже контент: «не измерялось».</summary>
public sealed class ChartPanelViewModel
{
    public ChartPanelViewModel(
        string titleRu,
        string unitRu,
        bool isEmpty,
        string emptyRu,
        IReadOnlyList<ChartLineSegment> segments,
        IReadOnlyList<ChartAxisLabel> yLabels,
        IReadOnlyList<ChartAxisLabel> xLabels,
        IReadOnlyList<string> legendLines)
    {
        TitleRu = titleRu;
        UnitRu = unitRu;
        IsEmpty = isEmpty;
        EmptyRu = emptyRu;
        Segments = segments;
        YLabels = yLabels;
        XLabels = xLabels;
        LegendLines = legendLines;
    }

    public string TitleRu { get; }

    public string UnitRu { get; }

    /// <summary>Ни одного измеренного значения: рисовать нечего, но сказать «почему» — обязательно.</summary>
    public bool IsEmpty { get; }

    /// <summary>Честная причина пустоты: берётся из описания ряда, а не выдумывается здесь.</summary>
    public string EmptyRu { get; }

    public IReadOnlyList<ChartLineSegment> Segments { get; }

    public IReadOnlyList<ChartAxisLabel> YLabels { get; }

    public IReadOnlyList<ChartAxisLabel> XLabels { get; }

    /// <summary>Легенда: строка на каждую линию панели — из DescribeRu ряда (значение, единица, «измерено N из M»).</summary>
    public IReadOnlyList<string> LegendLines { get; }
}

/// <summary>
/// График сети в разделе «Оптимизация» (задача F). Данные — ТОЛЬКО из замеров живой оптимизации:
/// график не запускает собственных зондов и не отправляет пакетов, когда оптимизация выключена.
///
/// Правила честности, зашитые в код:
///   • нет измерения — нет нуля: точка без данных остаётся разрывом линии с подписью «не измерялось»;
///   • окно 1/5/15 минут отсчитывается от последнего замера, а не от часов на стене;
///   • «данные только из замеров этого запуска; за его пределами — пусто» — подпись над графиком;
///   • линии min/p95 — только там, где способ замера дал распределение; иначе честный разрыв.
/// </summary>
public sealed class NetworkChartViewModel : ObservableObject
{
    /// <summary>Размер канвы в пикселях: разметка рисует в этих координатах и масштабирует Viewbox-ом.</summary>
    public const double CanvasWidth = 560;
    public const double CanvasHeight = 132;

    private const double LeftPad = 46;
    private const double RightPad = 10;
    private const double TopPad = 8;
    private const double BottomPad = 6;

    private static readonly TimeSpan[] Windows = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly object _gate = new();
    private List<ChartSample> _samples = [];
    private int _windowIndex = 1; // 5 минут по умолчанию: видно и движение, и провалы

    public NetworkChartViewModel()
    {
        Rebuild();
    }

    /// <summary>Честная подпись над графиком: без неё любые цифры выглядели бы «историей навсегда».</summary>
    public string HonestCaptionRu => "Данные только из замеров этого запуска; за его пределами — пусто. График не запускает собственных зондов и не отправляет пакетов.";

    /// <summary>Есть ли хоть одно измеренное значение. Пустой график — это тоже состояние, и оно подписано.</summary>
    public bool HasData { get; private set; }

    /// <summary>Что показывать, пока замеров нет: честная надпись вместо пустой коробки.</summary>
    public string EmptyStateRu { get; private set; } =
        "Данных пока нет: график заполняется замерами живой оптимизации. Нажмите «Оптимизировать соединение» — до этого программа ничего не измеряет и не отправляет.";

    /// <summary>Подпись окна: сколько точек в окне и сколько из них измерено. Из ChartPack, не из головы.</summary>
    public string WindowSummaryRu { get; private set; } = string.Empty;

    /// <summary>Когда был последний замер: чтобы человек видел, «график живой» или «данные кончились».</summary>
    public string LastSampleRu { get; private set; } = "Замеров ещё не было.";

    /// <summary>Панель задержки: три линии (минимум / средняя / p95) в одной шкале.</summary>
    public ChartPanelViewModel RttPanel { get; private set; } = null!;

    /// <summary>Панель джиттера: отдельная шкала, потому что единицы и масштаб другие.</summary>
    public ChartPanelViewModel JitterPanel { get; private set; } = null!;

    /// <summary>Панель потерь: отдельная шкала.</summary>
    public ChartPanelViewModel LossPanel { get; private set; } = null!;

    /// <summary>Честные пояснения набора рядов: как ChartChartBuilder их собрал, так и показываем.</summary>
    public IReadOnlyList<string> NotesRu { get; private set; } = [];

    /// <summary>Окно 1 минута. Двустороннее: радиокнопки отмечают его и меняют через сеттер.</summary>
    public bool IsWindow1
    {
        get => _windowIndex == 0;
        set
        {
            if (value && _windowIndex != 0)
            {
                SetWindow(0);
            }
        }
    }

    /// <summary>Окно 5 минут (по умолчанию).</summary>
    public bool IsWindow5
    {
        get => _windowIndex == 1;
        set
        {
            if (value && _windowIndex != 1)
            {
                SetWindow(1);
            }
        }
    }

    /// <summary>Окно 15 минут.</summary>
    public bool IsWindow15
    {
        get => _windowIndex == 2;
        set
        {
            if (value && _windowIndex != 2)
            {
                SetWindow(2);
            }
        }
    }

    /// <summary>Название выбранного окна для интерфейса.</summary>
    public string WindowTitleRu => _windowIndex switch
    {
        0 => "1 минута",
        2 => "15 минут",
        _ => "5 минут",
    };

    /// <summary>
    /// Принять новую порцию замеров от службы (весь набор сессии, как отдал status).
    /// Замена целиком, а не слияние: служба — единственный источник правды о сессии.
    /// </summary>
    public void ReceiveSamples(IReadOnlyList<ChartSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        lock (_gate)
        {
            _samples = samples.OrderBy(s => s.At).ToList();
        }

        Rebuild();
    }

    private void SetWindow(int index)
    {
        _windowIndex = index;
        OnPropertyChanged(nameof(IsWindow1), nameof(IsWindow5), nameof(IsWindow15), nameof(WindowTitleRu));
        Rebuild();
    }

    private void Rebuild()
    {
        List<ChartSample> all;
        lock (_gate)
        {
            all = _samples;
        }

        if (all.Count == 0)
        {
            HasData = false;
            RttPanel = EmptyPanel(ChartIndicator.RttAvgMs, "замеров ещё не было");
            JitterPanel = EmptyPanel(ChartIndicator.JitterMadMs, "замеров ещё не было");
            LossPanel = EmptyPanel(ChartIndicator.PacketLossPercent, "замеров ещё не было");
            WindowSummaryRu = string.Empty;
            LastSampleRu = "Замеров ещё не было.";
            NotesRu = [];
            OnPropertyChangedAll();
            return;
        }

        DateTimeOffset newest = all[^1].At;
        DateTimeOffset windowStart = newest - Windows[_windowIndex];
        List<ChartSample> window = all.Where(s => s.At >= windowStart).ToList();

        // Ноль измеренного в окне — это «не измерялось», а не «всё по нулям».
        bool anyMeasured = window.Any(s => s.HasAnyValue);
        ChartPack pack = NetworkChartBuilder.Build(
            window,
            Windows[_windowIndex],
            unavailableReasonRu: "в выбранном окне замеров нет");

        HasData = anyMeasured;
        EmptyStateRu = window.Count == 0
            ? "Замеры есть, но старее выбранного окна: выберите окно побольше — например, 15 минут."
            : "В выбранном окне ни одного измеренного значения: замеры не удавались (на графике это разрывы, а не нули).";

        bool lastMeasured = all[^1].HasAnyValue;
        LastSampleRu = lastMeasured
            ? $"Последний замер: {newest.ToLocalTime():HH:mm:ss}."
            : $"Последняя точка: {newest.ToLocalTime():HH:mm:ss} — без измеренного значения.";

        ChartSeries rttMin = pack.Series.Single(s => s.Indicator == ChartIndicator.RttMinMs);
        ChartSeries rttAvg = pack.Series.Single(s => s.Indicator == ChartIndicator.RttAvgMs);
        ChartSeries rttP95 = pack.Series.Single(s => s.Indicator == ChartIndicator.RttP95Ms);
        ChartSeries jitter = pack.Series.Single(s => s.Indicator == ChartIndicator.JitterMadMs);
        ChartSeries loss = pack.Series.Single(s => s.Indicator == ChartIndicator.PacketLossPercent);

        RttPanel = BuildPanel(
            "Задержка, мс",
            "мс",
            windowStart,
            newest,
            [
                (ChartLineKind.RttMin, rttMin),
                (ChartLineKind.RttAvg, rttAvg),
                (ChartLineKind.RttP95, rttP95),
            ]);
        JitterPanel = BuildPanel("Джиттер (MAD), мс", "мс", windowStart, newest, [(ChartLineKind.Jitter, jitter)]);
        LossPanel = BuildPanel("Потери, %", "%", windowStart, newest, [(ChartLineKind.Loss, loss)]);

        WindowSummaryRu = pack.WindowRu;
        NotesRu = pack.NotesRu.Take(3).ToList();

        OnPropertyChangedAll();
    }

    private static ChartPanelViewModel EmptyPanel(ChartIndicator indicator, string reasonRu)
    {
        ChartSeries unavailable = ChartSeries.Unavailable(indicator, reasonRu);
        return new ChartPanelViewModel(
            NetworkChart.TitleOf(indicator),
            NetworkChart.UnitOf(indicator),
            isEmpty: true,
            emptyRu: unavailable.DescribeRu(),
            segments: [],
            yLabels: [],
            xLabels: [],
            legendLines: [unavailable.DescribeRu()]);
    }

    /// <summary>Собрать панель: общая шкала Y по всем линиям, разрывы — отдельными отрезками.</summary>
    private static ChartPanelViewModel BuildPanel(
        string titleRu,
        string unitRu,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<(int LineKind, ChartSeries Series)> lines)
    {
        var measuredValues = lines
            .SelectMany(pair => pair.Series.Points.Where(p => p.IsMeasured).Select(p => p.Value!.Value))
            .ToList();

        if (measuredValues.Count == 0)
        {
            string reason = lines[0].Series.IsEmpty
                ? lines[0].Series.DescribeRu()
                : "в этом окне нет измеренных точек";
            return new ChartPanelViewModel(
                titleRu,
                unitRu,
                isEmpty: true,
                emptyRu: reason,
                segments: [],
                yLabels: [],
                xLabels: [],
                legendLines: lines.Select(pair => pair.Series.DescribeRu()).ToList());
        }

        double min = measuredValues.Min();
        double max = measuredValues.Max();
        if (max - min < 0.001)
        {
            // Одна плоская линия: растягиваем шкалу, иначе линия приклеится к краю.
            min -= 1;
            max += 1;
        }
        else
        {
            double pad = (max - min) * 0.08;
            min -= pad;
            max += pad;
        }

        double plotWidth = CanvasWidth - LeftPad - RightPad;
        double plotHeight = CanvasHeight - TopPad - BottomPad;
        double span = windowEnd.Ticks - windowStart.Ticks;

        var segments = new List<ChartLineSegment>();
        foreach ((int lineKind, ChartSeries series) in lines)
        {
            var current = new List<string>();
            foreach (ChartPoint point in series.Points)
            {
                if (!point.IsMeasured)
                {
                    // Разрыв: точка без измерения не рисуется и в ноль не превращается.
                    if (current.Count > 0)
                    {
                        segments.Add(new ChartLineSegment(string.Join(' ', current), lineKind));
                        current = [];
                    }

                    continue;
                }

                double x = LeftPad + (point.At.Ticks - windowStart.Ticks) / span * plotWidth;
                double y = TopPad + (1 - (point.Value!.Value - min) / (max - min)) * plotHeight;
                current.Add($"{Format(x)},{Format(y)}");
            }

            if (current.Count > 0)
            {
                // Одиночная точка — отрезок длиной в полпикселя, чтобы её было видно.
                if (current.Count == 1)
                {
                    string single = current[0];
                    string[] parts = single.Split(',');
                    current.Add($"{Format(double.Parse(parts[0], CultureInfo.InvariantCulture) + 0.6)},{parts[1]}");
                }

                segments.Add(new ChartLineSegment(string.Join(' ', current), lineKind));
            }
        }

        // Три подписи по вертикали: верх, середина, низ — значения шкалы, а не «просто линии».
        var yLabels = new List<ChartAxisLabel>
        {
            new(0, TopPad - 1, Label(max - (max - min) * 0.0)),
            new(0, TopPad + plotHeight * 0.5 - 1, Label(max - (max - min) * 0.5)),
            new(0, TopPad + plotHeight - 1, Label(max - (max - min) * 1.0)),
        };

        var xLabels = new List<ChartAxisLabel>
        {
            new(LeftPad, CanvasHeight - 2, windowStart.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)),
            new(LeftPad + plotWidth, CanvasHeight - 2, windowEnd.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)),
        };

        return new ChartPanelViewModel(
            titleRu,
            unitRu,
            isEmpty: false,
            emptyRu: string.Empty,
            segments,
            yLabels,
            xLabels,
            legendLines: lines.Select(pair => pair.Series.DescribeRu()).ToList());
    }

    private static string Label(double value) =>
        Math.Abs(value) >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private void OnPropertyChangedAll()
    {
        OnPropertyChanged(
            nameof(HasData),
            nameof(EmptyStateRu),
            nameof(WindowSummaryRu),
            nameof(LastSampleRu),
            nameof(RttPanel),
            nameof(JitterPanel),
            nameof(LossPanel),
            nameof(NotesRu));
    }
}
