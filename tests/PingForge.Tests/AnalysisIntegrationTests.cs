using PingForge.Analysis;
using PingForge.Analysis.Charting;
using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Задача F: анализ подключён к продукту. Проверяются три места окна и правила честности,
/// которые требуются задачей:
///   • график строится из серии, точки без измерения остаются null и не превращаются в 0;
///   • пустая серия — «нет данных» с объяснением, а не пустой график;
///   • советник ввода: у каждой находки текст, приоритет и ссылка на твик либо объяснение отсутствия;
///   • разбор конфликтов не говорит «всё хорошо», если данные не собраны (это «не проверено»);
///   • тест-«сторож»: PingForge.Analysis подключён к PingForge.App.ViewModels.
/// </summary>
public sealed class AnalysisIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    // ── Сторож: слой анализа реально подключён к проекту интерфейса ──────────────────────────

    [Fact]
    public void Сторож_проект_интерфейса_ссылается_на_слой_анализа()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-PingForge.App.ViewModels.csproj");
        Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
        string project = File.ReadAllText(path);

        Assert.Contains("PingForge.Analysis.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PingForge.Analysis.csproj condition=\"false\"", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Сторож_все_три_места_окна_содержат_блоки_задачи_F()
    {
        string Read(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
            Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
            return File.ReadAllText(path);
        }

        // График — в «Оптимизации», с честной подписью и переключателем окна.
        string optimize = Read("OptimizeView.xaml");
        Assert.Contains("График сети", optimize, StringComparison.Ordinal);
        Assert.Contains("HonestCaptionRu", optimize, StringComparison.Ordinal);
        Assert.Contains("IsWindow15", optimize, StringComparison.Ordinal);
        Assert.DoesNotContain("Показатели", optimize, StringComparison.Ordinal);

        // Карточка задержки ввода — в «Твиках», с кнопками проверки и перехода.
        string tweaks = Read("TweaksView.xaml");
        Assert.Contains("Задержка ввода", tweaks, StringComparison.Ordinal);
        Assert.Contains("Проверить задержку ввода", tweaks, StringComparison.Ordinal);
        Assert.Contains("Показать связанные твики", tweaks, StringComparison.Ordinal);
        Assert.Contains("VisibleRows", tweaks, StringComparison.Ordinal);

        // Проверка настроек — в «Настройках», только просмотр.
        string settings = Read("SettingsView.xaml");
        Assert.Contains("Проверка настроек", settings, StringComparison.Ordinal);
        Assert.Contains("Проверить настройки", settings, StringComparison.Ordinal);
    }

    // ── График: разрывы, пустые состояния, окна ───────────────────────────────────────────────

    [Fact]
    public void График_из_серии_строится_а_пропуски_остаются_разрывами_и_null()
    {
        var chart = new NetworkChartViewModel();

        chart.ReceiveSamples(
        [
            new ChartSample(Start.AddSeconds(-10), RttMinMs: 38, RttAvgMs: 40, RttP95Ms: 44, JitterMadMs: 1, PacketLossPercent: 0),
            ChartSample.Empty(Start.AddSeconds(-8)),
            new ChartSample(Start.AddSeconds(-6), RttMinMs: 42, RttAvgMs: 45, RttP95Ms: 50, JitterMadMs: 2, PacketLossPercent: 0.5),
        ]);
        chart.IsWindow15 = true;

        Assert.True(chart.HasData);
        Assert.False(chart.RttPanel.IsEmpty);

        // Разрыв в середине: линии RTT — два отрезка (до пропуска и после), а не одна сплошная.
        Assert.True(chart.RttPanel.Segments.Count >= 2,
            $"ожидались два отрезка из-за пропуска, а получилось {chart.RttPanel.Segments.Count}");
        Assert.All(chart.RttPanel.Segments, segment => Assert.False(string.IsNullOrWhiteSpace(segment.PointsText)));

        // Ни одна точка без измерения не превратилась в число: в геометрии нет нулевых «провалов».
        string legend = string.Join("\n", chart.RttPanel.LegendLines);
        Assert.Contains("измерено 2 из 3", legend, StringComparison.Ordinal);
        Assert.Contains("в нули не превращено", legend, StringComparison.Ordinal);
    }

    [Fact]
    public void Пустая_серия_даёт_честное_нет_данных_а_не_пустой_график()
    {
        var chart = new NetworkChartViewModel();

        Assert.False(chart.HasData);
        Assert.Contains("Данных пока нет", chart.EmptyStateRu, StringComparison.Ordinal);
        Assert.Contains("ничего не измеряет", chart.EmptyStateRu, StringComparison.Ordinal);
        Assert.All(new[] { chart.RttPanel, chart.JitterPanel, chart.LossPanel }, panel => Assert.True(panel.IsEmpty));
        Assert.All(new[] { chart.RttPanel, chart.JitterPanel, chart.LossPanel }, panel =>
            Assert.Contains("измерить не удалось", panel.EmptyRu, StringComparison.Ordinal));

        // Подпись честности — над графиком всегда, даже пустым.
        Assert.Contains("этого запуска", chart.HonestCaptionRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Серия_только_неизмеренных_точек_это_нет_данных_а_не_нули()
    {
        var chart = new NetworkChartViewModel();

        chart.ReceiveSamples([ChartSample.Empty(Start), ChartSample.Empty(Start.AddSeconds(2))]);

        Assert.False(chart.HasData);
        Assert.Contains("ни одного измеренного значения", chart.EmptyStateRu, StringComparison.Ordinal);
        Assert.True(chart.RttPanel.IsEmpty);
        Assert.Empty(chart.RttPanel.Segments);
    }

    [Fact]
    public void Окно_1_5_15_минут_считается_от_последнего_замера()
    {
        var chart = new NetworkChartViewModel();
        chart.ReceiveSamples(
        [
            new ChartSample(Start.AddMinutes(-10), RttAvgMs: 100),
            new ChartSample(Start.AddSeconds(-30), RttAvgMs: 40),
        ]);

        Assert.True(chart.IsWindow5);
        chart.IsWindow1 = true;
        Assert.True(chart.IsWindow1);
        Assert.False(chart.IsWindow5);

        // Окно 1 минута: старая точка (10 минут назад) за пределами окна — в легенде одна точка из двух.
        Assert.Contains("измерено 1 из 1", string.Join("\n", chart.RttPanel.LegendLines), StringComparison.Ordinal);

        chart.IsWindow15 = true;
        Assert.Contains("измерено 2 из 2", string.Join("\n", chart.RttPanel.LegendLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task График_наполняется_точками_из_ответа_службы_status()
    {
        string data = """
            {
              "realtimeState":"active-monitoring",
              "chartSamples":[
                {"atUtc":"2026-10-18T12:00:00.0000000Z","measured":true,"rttMinMs":38,"rttMedianMs":40,"rttP95Ms":44,"jitterMadMs":1.5,"lossPercent":0},
                {"atUtc":"2026-10-18T12:00:02.0000000Z","measured":false},
                {"atUtc":"2026-10-18T12:00:04.0000000Z","measured":true,"rttMedianMs":46,"jitterMadMs":2,"lossPercent":0.5}
              ]
            }
            """;

        var responderChannel = new FakeEngineChannel(request => EngineResponse.Success(
            request.Id,
            "Идёт контроль качества.",
            data));
        var main = new MainViewModel(new EngineClient(responderChannel), new FakeDialogs());

        await main.RefreshStatusAsync();

        Assert.True(main.Chart.HasData);
        Assert.False(main.Chart.RttPanel.IsEmpty);

        // Линия «средняя» — среднее по измеренным (40 и 46 → 43): медиана окна идёт в неё,
        // когда способ замера не дал среднего. Это то же число, что кнопка называет «пинг сейчас».
        Assert.Contains("43", string.Join("\n", main.Chart.RttPanel.LegendLines), StringComparison.Ordinal);
        Assert.True(main.Chart.RttPanel.Segments.Count >= 2, "разрыв (неизмеренная точка) обязан дать два отрезка");
    }

    [Fact]
    public async Task Ответ_службы_без_точек_графика_оставляет_его_пустым()
    {
        var responderChannel = new FakeEngineChannel(request => EngineResponse.Success(
            request.Id,
            "Оптимизация не запущена.",
            """{"realtimeState":"idle","pendingOperations":0}"""));
        var main = new MainViewModel(new EngineClient(responderChannel), new FakeDialogs());

        await main.RefreshStatusAsync();

        Assert.False(main.Chart.HasData);
        Assert.Contains("Данных пока нет", main.Chart.EmptyStateRu, StringComparison.Ordinal);
    }

    // ── Советник задержки ввода: находки с приоритетом и связью с твиками ─────────────────────

    private sealed class StubInputLatencyAnalysis : IInputLatencyAnalysis
    {
        public NetworkAnalysisReport? Report { get; set; }

        public Task<NetworkAnalysisReport> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(Report ?? new NetworkAnalysisReport([], [], [], [], "пусто"));
    }

    private static NetworkAnalysisReport SampleReport() => new(
        Warnings:
        [
            new InterferenceWarning(
                "WIRELESS_POINTER",
                "Беспроводной указатель",
                "Мышь работает по радио.",
                "Помогает проводная мышь.",
                TweakRiskLevel.Low),
        ],
        Recommendations:
        [
            new TweakRecommendationItem(
                "KEYBOARD_FILTERKEYS_OFF",
                "Фильтр ввода клавиатуры",
                GameRecommendationBadge.Recommended,
                RequiresManualVerification: false,
                "Сейчас: включён",
                "выключен",
                "Фильтр отбрасывает короткие нажатия.",
                TweakRiskLevel.Medium),
        ],
        SkippedChecks: ["GAME_DVR_OFF: уже настроено — менять нечего"],
        Notes: ["правки не снижают пинг внутри боя"],
        SummaryRu: "Проверок выполнено: 7. Предупреждений: 1. Предлагается изменить: 1. Пропущено проверок: 1.");

    [Fact]
    public async Task Советник_ввода_у_каждой_находки_текст_приоритет_и_ссылка_на_твик()
    {
        var analysis = new StubInputLatencyAnalysis { Report = SampleReport() };
        List<IReadOnlyList<string>> filterCalls = [];
        var card = new InputLatencyViewModel(analysis, codes => filterCalls.Add(codes));

        await card.CheckCommand.ExecuteAsync();

        Assert.True(card.HasResult);
        Assert.Equal(2, card.Findings.Count);

        // Предупреждение: приоритет из риска, объяснение и честное «твика нет» с причиной.
        InputLatencyFindingViewModel warning = card.Findings.Single(f => f.TitleRu == "Беспроводной указатель");
        Assert.Equal("низкий", warning.PriorityRu);
        Assert.False(string.IsNullOrWhiteSpace(warning.ExplanationRu));
        Assert.Null(warning.TweakCode);
        Assert.Contains("твика в каталоге нет", warning.LinkRu, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(warning.NoTweakReasonRu));

        // Предложение: связано с твиком каталога по коду.
        InputLatencyFindingViewModel recommendation = card.Findings.Single(f => f.TweakCode == "KEYBOARD_FILTERKEYS_OFF");
        Assert.Equal("средний", recommendation.PriorityRu);
        Assert.Contains("KEYBOARD_FILTERKEYS_OFF", recommendation.LinkRu, StringComparison.Ordinal);

        // Пропущенные проверки показываются: «не знаем» ≠ «всё хорошо».
        Assert.Single(card.SkippedChecks);

        // Числа в подписи считаются из отчёта.
        Assert.Contains("Находок: 2", card.CountsRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Советник_ввода_переходит_к_отфильтрованному_списку_твиков()
    {
        var analysis = new StubInputLatencyAnalysis { Report = SampleReport() };
        List<IReadOnlyList<string>> filterCalls = [];
        var card = new InputLatencyViewModel(analysis, codes => filterCalls.Add(codes));

        await card.CheckCommand.ExecuteAsync();
        card.ShowRelatedTweaksCommand.Execute(null);

        Assert.Single(filterCalls);
        Assert.Equal(["KEYBOARD_FILTERKEYS_OFF"], filterCalls[0]);
    }

    [Fact]
    public async Task Сбой_советника_это_не_удалось_а_не_находок_нет()
    {
        var analysis = new StubLatencyFailure();
        var card = new InputLatencyViewModel(analysis, _ => { });

        await card.CheckCommand.ExecuteAsync();

        Assert.False(card.HasResult);
        Assert.Contains("не удалась", card.StatusRu, StringComparison.Ordinal);
        Assert.Empty(card.Findings);
    }

    private sealed class StubLatencyFailure : IInputLatencyAnalysis
    {
        public Task<NetworkAnalysisReport> CheckAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("PowerShell не ответил");
    }

    // ── Раздел «Твики»: фильтр по кодам ───────────────────────────────────────────────────────

    [Fact]
    public void Фильтр_твиков_отбирает_по_кодам_и_возвращается_целиком()
    {
        var channel = new FakeEngineChannel();
        var rows = new List<TweakRowViewModel>
        {
            new("MOUSE_ACCEL_SPEED", "Акселерация", "Что делает.", "включать: да", "почему", true, false, true, false, true, "Ввод", false),
            new("KEYBOARD_FILTERKEYS_OFF", "Фильтр ввода", "Что делает.", "включать: да", "почему", true, false, false, false, false, "Ввод", false),
            new("TCP_NO_DELAY", "Nagle", "Что делает.", "по желанию", "почему", false, false, true, false, true, "Пинг", false),
        };
        var tweaks = new TweaksViewModel(
            new EngineClient(channel),
            new FakeDialogs(),
            rows,
            [new PresetViewModel(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 3)]);

        Assert.Equal(3, tweaks.VisibleRows.Count);

        tweaks.ShowOnlyCodes(["MOUSE_ACCEL_SPEED", "KEYBOARD_FILTERKEYS_OFF"]);

        Assert.True(tweaks.IsFiltered);
        Assert.Equal(2, tweaks.VisibleRows.Count);
        Assert.All(tweaks.VisibleRows, row => Assert.Contains(row.Code, new[] { "MOUSE_ACCEL_SPEED", "KEYBOARD_FILTERKEYS_OFF" }));
        Assert.Contains("2 из 3", tweaks.FilterLineRu, StringComparison.Ordinal);

        tweaks.ClearFilter();

        Assert.False(tweaks.IsFiltered);
        Assert.Equal(3, tweaks.VisibleRows.Count);
    }

    [Fact]
    public void Фильтр_с_чужими_кодами_не_прячет_весь_список_молча()
    {
        var channel = new FakeEngineChannel();
        var rows = new List<TweakRowViewModel>
        {
            new("TCP_NO_DELAY", "Nagle", "Что делает.", "по желанию", "почему", false, false, true, false, true, "Пинг", false),
        };
        var tweaks = new TweaksViewModel(
            new EngineClient(channel),
            new FakeDialogs(),
            rows,
            [new PresetViewModel(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 1)]);

        tweaks.ShowOnlyCodes(["НЕТ_ТАКОГО_КОДА"]);

        // Ноль строк по чужим кодам — фильтр не включается молча: список остаётся, статус объясняет.
        Assert.Single(tweaks.VisibleRows);
        Assert.Contains("нет ни одного из перечисленных кодов", tweaks.StatusRu, StringComparison.Ordinal);
    }

    // ── Проверка настроек: «не проверено» ≠ «всё хорошо» ──────────────────────────────────────

    private sealed class StubSettingsCheckAnalysis : ISettingsCheckAnalysis
    {
        public SettingsCheckReport? Report { get; set; }

        public bool Throw { get; set; }

        public Task<SettingsCheckReport> CheckAsync(CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("адаптер не читается");
            }

            return Task.FromResult(Report ?? new SettingsCheckReport(
                new NetworkAnalysisReport([], [], [], [], "пусто"),
                EmptyTweakReport(),
                DataCollected: false,
                ProblemRu: null));
        }
    }

    private static TweakAnalysisReport EmptyTweakReport() => new(
        TweakTarget.Global("no-adapter"),
        AdapterResolved: false,
        AlreadyOptimal: [],
        RecommendedToChange: [],
        DoNotTouch: [],
        Notes: []);

    [Fact]
    public void До_запуска_проверки_настроек_статус_честно_говорит_что_её_не_было()
    {
        var check = new SettingsCheckViewModel(new StubSettingsCheckAnalysis(), _ => { });

        Assert.False(check.HasResult);
        Assert.Contains("ещё не запускалась", check.StatusRu, StringComparison.Ordinal);
        Assert.DoesNotContain("всё хорошо", check.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Разбор_конфликтов_с_несобранными_данными_это_не_проверено_а_не_всё_хорошо()
    {
        var analysis = new StubSettingsCheckAnalysis
        {
            Report = new SettingsCheckReport(
                Network: new NetworkAnalysisReport(
                    Warnings: [new InterferenceWarning("CONTEXT_INCOMPLETE", "Часть данных не прочитана", "нет доступа", "проверьте вручную", TweakRiskLevel.Low)],
                    Recommendations: [],
                    SkippedChecks: ["MTU_SUBINTERFACE_OPTIMAL: игровой сервер не выбран"],
                    Notes: [],
                    "Проверок выполнено: 4. Предупреждений: 1."),
                Tweaks: EmptyTweakReport(),
                DataCollected: false,
                ProblemRu: "адаптер не определён"),
        };
        var check = new SettingsCheckViewModel(analysis, _ => { });

        await check.CheckCommand.ExecuteAsync();

        Assert.True(check.HasResult);
        Assert.False(check.DataCollected);

        // «Всё хорошо» не звучит итогом: неполные данные — это «проверено не всё».
        Assert.Contains("Проверено не всё", check.SummaryRu, StringComparison.Ordinal);
        Assert.Contains("враньём", check.SummaryRu, StringComparison.Ordinal);

        // «Мешает» показывается с кодом предупреждения.
        Assert.Contains("CONTEXT_INCOMPLETE", check.Interference.Single().TitleRu, StringComparison.Ordinal);

        // Пропуски видны как «не проверено» — с причиной.
        Assert.Contains(check.SkippedChecks, skipped => skipped.Contains("не определён", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Проверка_настроек_с_полными_данными_и_без_помех_говорит_всё_хорошо_честно()
    {
        var analysis = new StubSettingsCheckAnalysis
        {
            Report = new SettingsCheckReport(
                Network: new NetworkAnalysisReport([], [], [], [], "Проверок выполнено: 4."),
                Tweaks: new TweakAnalysisReport(
                    TweakTarget.Global("ok"),
                    AdapterResolved: true,
                    AlreadyOptimal: [new TweakRecommendationItem("TCP_NO_DELAY", "Nagle", GameRecommendationBadge.Recommended, false, "выключен", "выключен", "уже стоит", TweakRiskLevel.Low)],
                    RecommendedToChange: [],
                    DoNotTouch: [],
                    Notes: []),
                DataCollected: true,
                ProblemRu: null),
        };
        var check = new SettingsCheckViewModel(analysis, _ => { });

        await check.CheckCommand.ExecuteAsync();

        Assert.True(check.DataCollected);
        Assert.Contains("Помех не найдено", check.SummaryRu, StringComparison.Ordinal);
        Assert.Contains("1 твик уже в рекомендованном состоянии", check.SummaryRu, StringComparison.Ordinal);
        Assert.Empty(check.Recommended);
    }

    [Fact]
    public async Task Проверка_настроек_предлагает_твики_по_кодам_и_переходит_к_ним()
    {
        var analysis = new StubSettingsCheckAnalysis
        {
            Report = new SettingsCheckReport(
                Network: new NetworkAnalysisReport([], [], [], [], "Проверок выполнено: 4."),
                Tweaks: new TweakAnalysisReport(
                    TweakTarget.Global("ok"),
                    AdapterResolved: true,
                    AlreadyOptimal: [],
                    RecommendedToChange:
                    [
                        new TweakRecommendationItem("DNS_SERVERS_FASTEST", "DNS-серверы", GameRecommendationBadge.Recommended, false, "Сейчас: авто", "1.1.1.1|1.0.0.1", "Бенчмарк выбрал Cloudflare.", TweakRiskLevel.Low),
                    ],
                    DoNotTouch: [],
                    Notes: []),
                DataCollected: true,
                ProblemRu: null),
        };
        List<IReadOnlyList<string>> filterCalls = [];
        var check = new SettingsCheckViewModel(analysis, codes => filterCalls.Add(codes));

        await check.CheckCommand.ExecuteAsync();

        Assert.Single(check.Recommended);
        Assert.Contains("DNS_SERVERS_FASTEST", check.Recommended[0].TitleRu, StringComparison.Ordinal);
        Assert.Equal("DNS_SERVERS_FASTEST", check.Recommended[0].TweakCode);

        check.ShowRecommendedTweaksCommand.Execute(null);
        Assert.Single(filterCalls);
        Assert.Equal(["DNS_SERVERS_FASTEST"], filterCalls[0]);
    }

    [Fact]
    public async Task Сбой_проверки_настроек_оставляет_честное_не_проверено()
    {
        var check = new SettingsCheckViewModel(new StubSettingsCheckAnalysis { Throw = true }, _ => { });

        await check.CheckCommand.ExecuteAsync();

        Assert.False(check.HasResult);
        Assert.False(check.DataCollected);
        Assert.Contains("не проверено", check.StatusRu, StringComparison.Ordinal);
        Assert.Contains("не удалась", check.StatusRu, StringComparison.Ordinal);
    }
}
