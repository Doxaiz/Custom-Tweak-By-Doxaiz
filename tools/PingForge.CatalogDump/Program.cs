using System.Text.Json;
using System.Text.Json.Serialization;
using PingForge.Analysis.Charting;
using PingForge.Core.Models;
using PingForge.Core.Tweaks;
using PingForge.Probing;
using PingForge.Orchestration.Realtime;
using PingForge.Routing;
using PingForge.Tweaks;

// Утилита выгружает РЕАЛЬНЫЕ данные каталога и настроек в JSON.
// Макет интерфейса наполняется из этого файла, а не из текста, написанного руками:
// если в каталоге что-то поменяется, макет поменяется вместе с ним.

string outputPath = args.Length > 0 ? args[0] : "catalog.json";

// Портов платформы нет — макету нужны описания и правила, а не живая система.
var catalog = new TweakCatalog(new TweakPlatformPorts());

var presets = new[]
{
    TweakProfilePreset.LowLatencyGaming,
    TweakProfilePreset.MaxThroughput,
    TweakProfilePreset.FpsAndResponsiveness,
    TweakProfilePreset.RestoreSnapshotDefault,
    TweakProfilePreset.CustomUserPreset
};

var dump = new
{
    generatedUtc = DateTime.UtcNow,
    catalogProblems = PresetPolicy.ValidateCatalog(catalog.GetAllTweaks()),
    focusCounts = catalog.GetDefinitions()
        .GroupBy(d => DescribeFocus(d.EffectiveFocus))
        .Select(g => new { focusRu = g.Key, count = g.Count() })
        .ToArray(),
    safetyProblems = CatalogSafetyAudit.AuditCatalog(catalog.GetAllTweaks()),
    tweakCount = catalog.GetAllTweaks().Count,
    presets = presets.Select(p => new
    {
        preset = p.ToString(),
        titleRu = DescribePreset(p),
        tweakCount = catalog.GetByPreset(p).Count,
        tweakCodes = catalog.GetByPreset(p).Select(t => t.Id).ToArray()
    }),
    tweaks = catalog.GetDefinitions().Select(d => new
    {
        code = d.Code,
        titleRu = d.TitleRu,
        titleEn = d.TitleEn,
        category = d.Category.ToString(),
        categoryRu = DescribeCategory(d.Category),
        mechanism = TweakDefinition.MechanismOf(d.Source).ToString(),
        mechanismRu = DescribeMechanism(TweakDefinition.MechanismOf(d.Source)),
        risk = d.Risk.ToString(),
        badge = d.Badge.ToString(),
        badgeRu = TweakVerdict.BadgeRu(d.Badge),
        verdictRu = TweakVerdict.ShortRu(d.Badge),
        verdictWhyRu = TweakVerdict.WhyRu(d.Badge),
        enabledByDefault = TweakVerdict.EnabledByDefault(d.Badge),
        defaultStateRu = TweakVerdict.DefaultStateRu(d.Badge),
        resourceLocator = d.ResourceLocator,
        whatItDoesRu = d.WhatItDoesRu,
        gamingJustificationRu = d.GamingJustificationRu,
        defaultValueNoteRu = d.DefaultValueNoteRu,
        recommendedRawValue = d.RecommendedRawValue,
        isComputed = d.IsComputedTarget,
        effectUnconfirmed = d.EffectUnconfirmedOnModernWindows,
        requiresReboot = d.RebootRequired,
        ping = d.PingImpactDirection,
        speed = d.SpeedImpactDirection,
        stability = d.StabilityImpactDirection,
        inLowPingPreset = d.IncludedInLowPingPreset,
        inMaxSpeedPreset = d.IncludedInMaxSpeedPreset,
        inFpsPreset = d.IncludedInFpsPreset,
        focus = d.EffectiveFocus.ToString(),
        focusRu = DescribeFocus(d.EffectiveFocus),
        allowedInLowPing = PresetPolicy.IsAllowedInPreset(d.ToDescriptor(), TweakProfilePreset.LowLatencyGaming),
        allowedInMaxSpeed = PresetPolicy.IsAllowedInPreset(d.ToDescriptor(), TweakProfilePreset.MaxThroughput)
    }),
    routing = new
    {
        weights = DescribeWeights(new RouteScoreWeights()),
        thresholds = DescribeThresholds(new RouteEligibilityThresholds()),
        failover = DescribeFailover(new FailoverOptions()),
        formulaRu = "Балл = RTT×1.0 + джиттер×1.5 + потери²×0.5 + (хопы−1)×0.4 + нагрузка узла сверх 50 %×0.05 + доп. трафик×0.20",
        rulesRu = new[]
        {
            "Меньший балл — лучший маршрут; релей предлагается только при выигрыше ≥ 1 балла.",
            "Переключение только после 5 «плохих» замеров подряд и запаса ≥ 4 балла.",
            "Возврат на прямой путь — после 20 замеров, где прямой путь лучше.",
            "Период покоя после переключения — 30 секунд: защита от «дёрганья» маршрута.",
            "Маршруты, созданные не PingForge, не удаляются никогда (метка OwnerTag)."
        }
    },
    realtime = new
    {
        demoGameRu = "Fortnite (пример)",
        demoGameProcess = "FortniteClient-Win64-Shipping",
        plannerActions = RealtimeDemoPlan(new GameFlowInfo(
            4242, "FortniteClient-Win64-Shipping", "Fortnite", "3.4.5.6", 9000, "UDP", AntiCheatStatus.VerifiedSafe)),
        plannerNoAdminBlockRu = new OptimizationActionPlanner()
            .Plan(new GameFlowInfo(
                4242, "FortniteClient-Win64-Shipping", "Fortnite", "3.4.5.6", 9000, "UDP", AntiCheatStatus.VerifiedSafe),
                new RealtimeOptions(), isAdministrator: false).BlockReasonRu,
        plannerKernelAntiCheatNotes = new OptimizationActionPlanner()
            .Plan(new GameFlowInfo(
                4242, "FortniteClient-Win64-Shipping", "Fortnite", "3.4.5.6", 9000, "UDP", AntiCheatStatus.KnownKernelConflict),
                new RealtimeOptions(), isAdministrator: true).Notes.ToArray(),
        monitorIntervalSeconds = new RealtimeOptions().EffectiveMonitorInterval.TotalSeconds,
        samplesPerWindow = new RealtimeOptions().SamplesPerWindow,
        baselineWindows = new RealtimeOptions().BaselineWindows,
        degradedWindowsBeforeSwitch = new RealtimeOptions().DegradedWindowsBeforeSwitch,
        goodWindowsBeforeReturn = new RealtimeOptions().GoodWindowsBeforeReturn,
        degradationRttMarginMs = new RealtimeOptions().DegradationRttMarginMs,
        degradationJitterMs = new RealtimeOptions().DegradationJitterMs,
        degradationLossPercent = new RealtimeOptions().DegradationLossPercent,
        maxSessionHours = new RealtimeOptions().EffectiveMaxSessionDuration.TotalHours,
        qualityBands = RealtimeOptimizer.QualityBands
            .Select(b => new
            {
                upperBoundMs = double.IsPositiveInfinity(b.UpperBoundMs) ? (double?)null : b.UpperBoundMs,
                labelRu = b.LabelRu,
                textRu = double.IsPositiveInfinity(b.UpperBoundMs) ? $"выше — {b.LabelRu}" : $"до {b.UpperBoundMs:F0} мс — {b.LabelRu}"
            })
            .ToArray(),
        rulesRu = new[]
        {
            "Оптимизация затрагивает только адрес игрового сервера; чужой трафик идёт как обычно.",
            "Твики этой кнопкой не применяются — для них есть отдельное окно со снапшотом и точкой восстановления.",
            "Без прав администратора кнопка работает в режиме «только замеры» и ничего не меняет.",
            "Для игр с ядерным анти-читом фильтр пакетов не поднимается: только маршрут и политика QoS.",
            "Выигрыш показывается как разница медиан живого пинга; нет выигрыша — так и пишем.",
            "При остановке снимается ровно то, что поставили, в обратном порядке."
        },
        stepsRu = new[]
        {
            "1. Ищем запущенную игру и её живой сетевой поток: статистика уже открытого TCP-соединения для TCP, метки на ответах сервера для UDP — ни одного пакета сверх трафика игры.",
            "2. Меряем «до»: три окна замеров, чтобы исходный пинг был честным числом, а не одним кадром.",
            "3. Выбираем путь: прямое подключение или POP-узел релея по формуле скоринга.",
            "4. Применяем действия: маршрут для адреса сервера и политика QoS (DSCP 46) для процесса игры.",
            "5. Контролируем качество каждые 2 секунды: просадка → переключение пути, восстановление → возврат.",
            "6. Остановка: снимаем изменения, показываем «до/после» медианами и физический предел пути."
        }
    },
    probing = new
    {
        stabilityFormulaRu = "Стабильность = 100 − 4.5×потери% − 2.2×джиттер(MAD) − 1.5×доля выбросов%",
        samplesForFullReport = StatisticalMetricsCalculator.MinimumSamplesForFullReport,
        dnsCandidates = DnsResolverBenchmarker.DefaultCandidates
            .Select(c => new { name = c.Name, primary = c.Primary, secondary = c.Secondary }).ToArray(),
        dnsProbeDomains = DnsResolverBenchmarker.DefaultProbeDomains.ToArray(),
        notesRu = new[]
        {
            "Джиттер считается по MAD (медианное отклонение), а не по «среднему разбросу»: один скачок не портит картину.",
            "Меньше 30 замеров — результат помечается как предварительный.",
            "DNS-бенчмарк валидирует ответ (ID транзакции, имя вопроса, адрес источника) и делает canary-запрос: подмена DNS видна.",
            "Если измерить нельзя — приложение пишет «измерить не удалось» и причину, а не рисует нули."
        }
    },
    product = new
    {
        name = "Custom Tweak By Doxaiz",
        shortName = "Custom Tweak By Doxaiz",
        signatureRu = "By Doxaiz",
        windowTitleRu = "Custom Tweak By Doxaiz",
        supportUrl = "https://dalink.to/doxaiz",
        supportTitleRu = "Поддержать автора",
        supportNoteRu = "Ссылка ведёт на страницу автора. Ничего не списывается само, данные не отправляются: кнопка просто открывает страницу в браузере.",
        codeNameRu = "Внутри кода проект пока называется PingForge (пространства имён, пути и база). " +
                     "Продуктовое имя — Custom Tweak By Doxaiz; переименование кода отложено осознанно: имена службы и базы " +
                     "уже стоят на дисках у людей, и смена имени сломала бы и то, и другое."
    },
    routes = new
    {
        summaryRu = GameRouteCatalog.DescribeSummaryRu(),
        honestNoteRu = GameRouteCatalog.HonestNoteRu,
        gameCount = GameRouteCatalog.Targets.Count,
        games = GameRouteCatalog.Targets.Select(g => new
        {
            gameRu = g.GameRu,
            processName = g.ProcessName,
            isFreeToPlay = g.IsFreeToPlay,
            regionsRu = g.RegionsRu.ToArray(),
            portsRu = g.PublishedPortsRu.ToArray(),
            noteRu = g.NoteRu
        }).ToArray(),
        dns = GameRouteCatalog.FreeDnsRoutes.Select(d => new
        {
            providerRu = d.ProviderRu,
            primary = d.PrimaryIp,
            secondary = d.SecondaryIp,
            noteRu = d.NoteRu
        }).ToArray(),
        freePathCount = GameRouteCatalog.FreePathCount,
        freePathKindsRu = GameRouteCatalog.FreePathKindsRu.ToArray(),
        pathKinds = GameRouteCatalog.PathKinds.Select(p => new
        {
            kindRu = p.KindRu,
            whatRu = p.WhatRu,
            costRu = p.CostRu,
            safetyRu = p.SafetyRu,
            isFree = GameRouteCatalog.IsFreePath(p.KindRu)
        }).ToArray()
    },
    perf = new
    {
        profiles = new[] { PerfProfile.Economy, PerfProfile.Balanced, PerfProfile.MaxPerformance }
            .Select(p =>
            {
                var budget = new ResourceBudgetOptions(p);
                var baseOptions = new RealtimeOptions();
                return new
                {
                    profile = p.ToString(),
                    titleRu = ResourceBudgetOptions.DescribeProfile(p),
                    monitorIntervalSeconds = budget.EffectiveMonitorInterval(baseOptions.EffectiveMonitorIntervalForProfile).TotalSeconds,
                    measurementsPerMinute = budget.MeasurementsPerMinute(baseOptions.EffectiveMonitorIntervalForProfile, baseOptions.SamplesPerWindow),
                    pauseOnBattery = budget.PauseWhenOnBattery,
                    pauseWhenMinimized = budget.PauseWhenGameMinimized,
                    trayIconRefreshSeconds = budget.TrayIconRefreshSeconds,
                    describeRu = budget.DescribeRu(baseOptions.EffectiveMonitorIntervalForProfile, baseOptions.SamplesPerWindow)
                };
            }).ToArray(),
        honestNoteRu = "Профиль нагрузки меняет только частоту контроля: число проб в одном окне остаётся тем же, " +
                       "поэтому точность замера не страдает — страдает скорость реакции на просадку. " +
                       "Экономный профиль безопасен для ноутбуков и слабых процессоров."
    },
    tray = new
    {
        runningMenu = TrayMenuBuilder.Build(DemoSnapshot(RealtimeOptimizationState.ActiveMonitoring, 58, 62), isAdministrator: true, revertableChangeCount: 2)
            .Select(m => new { command = m.Command.ToString(), titleRu = m.TitleRu, isEnabled = m.IsEnabled, tooltipRu = m.TooltipRu }).ToArray(),
        idleMenu = TrayMenuBuilder.Build(DemoSnapshot(RealtimeOptimizationState.Idle, null, null), isAdministrator: true, revertableChangeCount: 0)
            .Select(m => new { command = m.Command.ToString(), titleRu = m.TitleRu, isEnabled = m.IsEnabled, tooltipRu = m.TooltipRu }).ToArray(),
        noAdminMenu = TrayMenuBuilder.Build(DemoSnapshot(RealtimeOptimizationState.ReadOnlyNoAdmin, 64, null), isAdministrator: false, revertableChangeCount: 0)
            .Select(m => new { command = m.Command.ToString(), titleRu = m.TitleRu, isEnabled = m.IsEnabled, tooltipRu = m.TooltipRu }).ToArray(),
        tooltipRunningRu = TrayMenuBuilder.TooltipFor(DemoSnapshot(RealtimeOptimizationState.ActiveMonitoring, 58, 62)),
        tooltipNoDataRu = TrayMenuBuilder.TooltipFor(DemoSnapshot(RealtimeOptimizationState.Idle, null, null)),
        honestNoteRu = "Значок в трее не опрашивает компьютер сам по себе: он обновляется на том же такте, что и контроль, " +
                       "а в экономном профиле — раз в 5 секунд. Закрытие окна на крестик не убивает оптимизацию, " +
                       "но выход через меню трея снимает изменения и останавливает замеры."
    },
    verdicts = new
    {
        legend = TweakVerdict.Legend.Select(v => new
        {
            badge = v.Badge.ToString(),
            shortRu = v.ShortRu,
            whyRu = v.WhyRu,
            enabledByDefault = TweakVerdict.EnabledByDefault(v.Badge),
            defaultStateRu = TweakVerdict.DefaultStateRu(v.Badge)
        }).ToArray(),
        recommendCount = catalog.GetDefinitions()
                .Count(d => d.Badge == GameRecommendationBadge.Recommended),
        honestyRu = "Вердикт — это часть каталога, а не подпись в интерфейсе: формулировки и правило " +
                    "«что включено по умолчанию» живут в коде (TweakVerdict) и проверяются тестами. " +
                    "По умолчанию включены только твики с вердиктом «включать: да»."
    },
    chart = new
    {
        requiredCount = NetworkChart.RequiredIndicators.Count,
        describeRu = NetworkChart.DescribeRu(),
        honestyRu = "Нет измерения — нет нуля: пропущенный замер остаётся разрывом с подписью «измерить не удалось». " +
                    "Среднее считается только по измеренным точкам, MTU берётся последним подтверждённым значением.",
        indicators = NetworkChart.RequiredIndicators.Select(i => new
        {
            indicator = i.Indicator.ToString(),
            titleRu = i.TitleRu,
            unitRu = i.UnitRu,
            whyRu = i.WhyRu,
            aggregation = i.Aggregation.ToString()
        }).ToArray(),
        demo = DescribeDemoChart()
    }
};

// Набор рядов для графика: пример истории замеров «до» и «после», чтобы макет рисовал
// те же показатели, что перечислены в коде, и подписи брались из NetworkChart.
static object DescribeDemoChart()
{
    ChartSample[] samples =
    [
        new ChartSample(DateTimeOffset.UnixEpoch, DownloadMbps: 89.4, UploadMbps: 41.2, RttMinMs: 31, RttAvgMs: 37.9,
            RttP95Ms: 55, JitterMadMs: 4.6, PacketLossPercent: 0.55, TcpRetransmitPermille: 19,
            LoadedLatencyMs: 61, DnsResponseMs: 42, PathMtuBytes: 1480),
        new ChartSample(DateTimeOffset.UnixEpoch.AddSeconds(5), DownloadMbps: 92.3, UploadMbps: 42.0, RttMinMs: 27,
            RttAvgMs: 30.2, RttP95Ms: 36, JitterMadMs: 2.3, PacketLossPercent: 0.06, TcpRetransmitPermille: 4,
            LoadedLatencyMs: 34, DnsResponseMs: 18, PathMtuBytes: 1492)
    ];
    ChartPack pack = NetworkChartBuilder.Build(samples, TimeSpan.FromSeconds(60), declaredSamples: 360);
    return new
    {
        windowRu = pack.WindowRu,
        notesRu = pack.NotesRu.ToArray(),
        series = pack.Series.Select(series => new
        {
            indicator = series.Indicator.ToString(),
            titleRu = series.TitleRu,
            unitRu = series.UnitRu,
            aggregation = series.Aggregation.ToString(),
            measured = series.MeasuredCount,
            total = series.TotalCount,
            unavailable = series.IsEmpty,
            values = series.Points.Select(p => p.Value).ToArray(),
            describeRu = series.DescribeRu()
        }).ToArray()
    };
}

static RealtimeSnapshot DemoSnapshot(RealtimeOptimizationState state, double? currentRttMs, double? baselineRttMs) => new(
    State: state,
    StatusRu: "пример состояния для макета",
    GameTitleRu: "Fortnite",
    Endpoint: "3.4.5.6:9000",
    BaselineRttMs: baselineRttMs,
    CurrentRttMs: currentRttMs,
    FloorRttMs: currentRttMs is null ? null : 46,
    CurrentJitterMs: currentRttMs is null ? null : 3.4,
    CurrentLossPercent: currentRttMs is null ? null : 0.0,
    ImprovementMs: currentRttMs is not null && baselineRttMs is not null ? baselineRttMs - currentRttMs : null,
    QualityLabelRu: currentRttMs is null ? "нет данных" : "хорошо",
    AppliedActions: Array.Empty<OptimizationActionDescriptor>(),
    Warnings: Array.Empty<string>(),
    UpdatedUtc: DateTime.UtcNow,
    // Способ замера в макете — тот же, что называет окно: иначе подсказка значка в макете
    // молчала бы о том, чем снято число.
    BaselineMethodTag: baselineRttMs is null ? string.Empty : ProbeMethodTags.LiveTcp,
    FinalMethodTag: currentRttMs is null ? string.Empty : ProbeMethodTags.LiveTcp);

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
};

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(dump, options));

Console.WriteLine($"Выгружено твиков: {catalog.GetAllTweaks().Count}");
Console.WriteLine($"Проблем каталога: {dump.catalogProblems.Count}, нарушений запретов: {dump.safetyProblems.Count}");
Console.WriteLine($"Файл: {Path.GetFullPath(outputPath)}");

static string DescribePreset(TweakProfilePreset preset) => preset switch
{
    TweakProfilePreset.LowLatencyGaming => "Низкий пинг",
    TweakProfilePreset.MaxThroughput => "Максимальная скорость",
    TweakProfilePreset.FpsAndResponsiveness => "Отзывчивость и кадры",
    TweakProfilePreset.RestoreSnapshotDefault => "Вернуть как было (дефолт)",
    TweakProfilePreset.CustomUserPreset => "Свой набор",
    _ => preset.ToString()
};

static string DescribeFocus(TweakFocus focus) => focus switch
{
    TweakFocus.Ping => "Пинг",
    TweakFocus.Speed => "Скорость",
    TweakFocus.Fps => "ФПС и кадры",
    TweakFocus.Input => "Ввод",
    TweakFocus.System => "Система",
    _ => focus.ToString()
};

static string DescribeCategory(TweakCategory category) => category switch
{
    TweakCategory.NicDriver => "Сетевой адаптер",
    TweakCategory.InputLatency => "Задержка ввода",
    TweakCategory.TcpIpStack => "Стек TCP/IP",
    TweakCategory.PowerAndScheduler => "Питание и планировщик",
    TweakCategory.WindowsBackground => "Фон Windows",
    TweakCategory.DnsAndResolution => "DNS и разрешение имён",
    _ => category.ToString()
};

static string DescribeMechanism(TweakMechanismKind mechanism) => mechanism switch
{
    TweakMechanismKind.Registry => "Параметр реестра",
    TweakMechanismKind.WmiNicAdvancedProperty => "Свойство драйвера NIC",
    TweakMechanismKind.NetshCommand => "Команда netsh",
    TweakMechanismKind.MtuSubinterface => "MTU подынтерфейса",
    TweakMechanismKind.DnsResolverConfig => "Настройка DNS",
    TweakMechanismKind.PowerCfgCommand => "Схема питания (powercfg)",
    TweakMechanismKind.QoSPolicyRegistry => "Политика QoS",
    _ => mechanism.ToString()
};

static object DescribeWeights(RouteScoreWeights w) => new
{
    perRttMs = w.PerRttMs,
    perJitterMs = w.PerJitterMs,
    perLossPercentSquared = w.PerLossPercentSquared,
    perHop = w.PerHop,
    perNodeLoadOver50 = w.PerNodeLoadPercentOver50,
    perExtraTrafficPercent = w.PerExtraTrafficPercent
};

static object DescribeThresholds(RouteEligibilityThresholds t) => new
{
    maxLossPercent = t.MaxLossPercent,
    maxJitterMs = t.MaxJitterMs,
    maxRttMs = t.MaxRttMs
};

static object DescribeFailover(FailoverOptions o) => new
{
    badSamplesBeforeSwitch = o.BadSamplesBeforeSwitch,
    switchMarginPoints = o.SwitchMarginPoints,
    goodSamplesBeforeReturn = o.GoodSamplesBeforeReturn,
    minTimeOnRouteSeconds = o.MinimumTimeOnRoute.TotalSeconds,
    degradationJitterMs = o.DegradationJitterMs,
    degradationLossPercent = o.DegradationLossPercent
};

// Действия, которые реально вернёт планировщик для примера с Fortnite: макет показывает
// настоящие тексты, а не написанные руками.
static object[] RealtimeDemoPlan(GameFlowInfo flow)
{
    OptimizationPlan plan = new OptimizationActionPlanner().Plan(flow, new RealtimeOptions(), isAdministrator: true);
    return plan.Actions
        .Select(a => (object)new { actionId = a.ActionId, kind = a.Kind.ToString(), titleRu = a.TitleRu, detailRu = a.DetailRu })
        .ToArray();
}
