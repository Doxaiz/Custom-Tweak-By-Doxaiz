using PingForge.Core.Models;

namespace PingForge.Orchestration.Realtime;

/// <summary>Состояние сессии живой оптимизации — то, что показывает большая кнопка в интерфейсе.</summary>
public enum RealtimeOptimizationState
{
    Idle,

    /// <summary>Ищем запущенную игру и её живой поток.</summary>
    FindingGame,

    /// <summary>Меряем «до»: несколько окон замеров, пока картина не станет устойчивой.</summary>
    MeasuringBaseline,

    /// <summary>Выбираем путь: прямой или альтернативный (релей, другой шлюз).</summary>
    ChoosingRoute,

    /// <summary>Применяем выбранные действия (маршрут для игры, маркировка DSCP, обновление твиков).</summary>
    ApplyingActions,

    /// <summary>Оптимизация работает: идёт непрерывный контроль качества.</summary>
    ActiveMonitoring,

    /// <summary>Качество просело, но переключиться пока нельзя (нет альтернативы или идёт период покоя).</summary>
    Degraded,

    /// <summary>Без прав администратора: замеры идут, менять сеть нельзя.</summary>
    ReadOnlyNoAdmin,

    Stopping,
    Stopped,
    Failed
}

/// <summary>Найденный живой поток игры: конкретный процесс, конкретный адрес сервера.</summary>
public sealed record GameFlowInfo(
    int ProcessId,
    string ProcessName,
    string GameTitleRu,
    string RemoteEndpoint,
    int RemotePort,
    string Transport,
    AntiCheatStatus AntiCheat)
{
    public string Key => $"{ProcessName}:{ProcessId}";
}

/// <summary>Поиск запущенных игр и их сетевых потоков.</summary>
public interface IGameFlowWatcher
{
    Task<IReadOnlyList<GameFlowInfo>> FindActiveGameFlowsAsync(CancellationToken ct = default);
}

/// <summary>
/// Расширенный поиск: кроме потоков игры он объясняет, почему игры нет, если в таблице соединений
/// есть что-то похожее. Без этого человек с запущенной игрой, которой нет в списке известных,
/// видел бы сухое «игра не найдена» и решил, что программа не работает.
/// </summary>
public interface IGameFlowReporter : IGameFlowWatcher
{
    /// <summary>Один проход по таблице: потоки игры и честное объяснение, если игр нет.</summary>
    Task<(IReadOnlyList<GameFlowInfo> Flows, string? NoGameReasonRu)> FindGameFlowsWithReasonAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Одно окно замеров живого потока. Неизмеримо — это честный результат, а не нули.
/// Минимум/среднее/p95 — необязательные поля: не каждый способ замера даёт распределение
/// (живой поток игры отдаёт одно число за окно), и тогда их место честно занимает null.
/// </summary>
public sealed record PathMeasurement(
    bool IsMeasurable,
    double RttMedianMs,
    double? JitterMs,
    double? LossPercent,
    int Samples,
    string MethodRu,
    double? RttMinMs = null,
    double? RttAvgMs = null,
    double? RttP95Ms = null,
    ProbeMethodUsed Method = ProbeMethodUsed.Unmeasurable)
{
    public static PathMeasurement Unmeasurable(string reasonRu) => new(false, 0, null, null, 0, reasonRu);

    /// <summary>
    /// Джиттер для текста. Неизмеренное называется словами, а не превращается в «0,0 мс»:
    /// ноль — это измеренное отсутствие разброса, и путать его с отсутствием замера нельзя.
    /// </summary>
    public string JitterRu => JitterMs is { } jitter ? $"{jitter:F1} мс" : "не измерено";

    /// <summary>Потери для текста: тот же принцип — нет замера, нет нуля.</summary>
    public string LossRu => LossPercent is { } loss ? $"{loss:F1} %" : "не измерено";
}

/// <summary>
/// Одна точка истории замеров сессии живой оптимизации — источник данных графика сети.
/// Неизмеренное окно записывается точкой с null-полями: на графике это разрыв линии
/// с подписью «не измерялось», а не ноль.
/// </summary>
public sealed record RealtimeMeasurementPoint(
    DateTime AtUtc,
    bool IsMeasurable,
    double? RttMinMs,
    double? RttMedianMs,
    double? RttP95Ms,
    double? JitterMadMs,
    double? LossPercent,
    int Samples,
    string MethodRu)
{
    public static RealtimeMeasurementPoint Unmeasured(DateTime atUtc, string reasonRu) =>
        new(atUtc, false, null, null, null, null, null, 0, reasonRu);
}

/// <summary>
/// Источник замеров потока. В приложении сейчас это пробы к адресу сервера (ICMP и TCP-рукопожатие):
/// живой поток читается пассивно для UDP: задержка считается по парам «отправка → ответ» из событий
/// сетевого стека, без единого лишнего пакета. TCP-статистика уже открытого соединения не читается —
/// это оставшийся долг, он назван здесь, а не спрятан. В тестах — заранее заданная последовательность.
/// </summary>
public interface IRealtimeMeasurementSource
{
    Task<PathMeasurement> MeasurePathAsync(string endpoint, int port, CancellationToken ct = default);
}

public enum OptimizationActionKind
{
    /// <summary>Маршрут только для адреса игрового сервера (не глобальный VPN).</summary>
    RouteForGame,

    /// <summary>Маркировка DSCP через политику QoS — приоритет пакетов игры в домашнем роутере.</summary>
    DscpMarking,

    /// <summary>Применение/обновление твиков сети (уже через снапшот и откат).</summary>
    TweakRefresh
}

/// <summary>
/// Действие оптимизации. <paramref name="RouteNextHop"/> — конкретный следующий узел для маршрута:
/// без него исполнитель берёт «текущую цель» из платформы, а при переключении на резервный путь
/// цель в платформе ещё указывает на прежний узел. Явный узел в самом действии убирает эту
/// зависимость от порядка вызовов: маршрут ведёт ровно туда, куда решили замеры.
/// </summary>
public sealed record OptimizationActionDescriptor(
    string ActionId,
    OptimizationActionKind Kind,
    string TitleRu,
    string DetailRu,
    bool IsReversible = true,
    string? RouteNextHop = null);

public sealed record OptimizationActionOutcome(bool Succeeded, string MessageRu)
{
    public static OptimizationActionOutcome Ok(string message) => new(true, message);
    public static OptimizationActionOutcome Fail(string message) => new(false, message);
}

/// <summary>Применение и снятие действий оптимизации. Реализация — в Windows-слое.</summary>
public interface IOptimizationActionRunner
{
    Task<OptimizationActionOutcome> ApplyAsync(OptimizationActionDescriptor action, CancellationToken ct = default);

    Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct = default);
}

/// <summary>
/// Альтернативный путь до того же адреса сервера (релей или другой шлюз). Нужен для переключений
/// на ходу. <paramref name="MedianRttMs"/> и <paramref name="SpreadMs"/> — измеренные числа этого пути
/// в той же сессии замеров: по ним решается, стоит ли уходить с текущего пути, когда тот в норме
/// (как «постоянно проверяем альтернативы» у игровых ускорителей). Нет чисел — нет и решения:
/// путь без замера не выбирается.
/// </summary>
public sealed record RealtimePathOption(
    string PathId,
    string DisplayName,
    bool IsRelay,
    string? NextHop,
    OptimizationActionDescriptor Action,
    double? MedianRttMs = null,
    double? SpreadMs = null);

public interface IRealtimePathOptionsProvider
{
    Task<IReadOnlyList<RealtimePathOption>> GetOptionsAsync(GameFlowInfo flow, CancellationToken ct = default);
}

/// <summary>Настройки живой оптимизации. Значения по умолчанию рассчитаны на игровой поток, а не на «пинг в браузере».</summary>
public sealed record RealtimeOptions(
    TimeSpan MonitorInterval = default,
    int SamplesPerWindow = 12,
    int BaselineWindows = 3,
    int DegradedWindowsBeforeSwitch = 3,
    int GoodWindowsBeforeReturn = 12,
    double DegradationRttMarginMs = 6.0,
    double DegradationJitterMs = 12.0,
    double DegradationLossPercent = 2.0,
    bool AllowRoutingChanges = true,
    bool AllowDscpMarking = true,
    TimeSpan MaxSessionDuration = default,
    ResourceBudgetOptions? Budget = null,
    int HealthyWindowsBetweenRouteReviews = 90,
    double RouteUpgradeMarginMs = 3.0,
    TimeSpan RouteUpgradeCooldown = default,
    int WindowsBetweenServerChecks = 15)
{
    /// <summary>Бюджет ресурсов: профиль нагрузки, паузы, обновление значка в трее.</summary>
    public ResourceBudgetOptions EffectiveBudget => Budget ?? new ResourceBudgetOptions();

    /// <summary>
    /// Интервал контроля с учётом профиля нагрузки. Экономный профиль растягивает интервал втрое —
    /// это и есть та самая «оптимизация, чтобы не грузить компьютер»: меньше проб, меньше работы.
    /// </summary>
    public TimeSpan EffectiveMonitorInterval =>
        EffectiveBudget.EffectiveMonitorInterval(EffectiveMonitorIntervalForProfile);

    /// <summary>Базовый интервал без учёта профиля нагрузки: два секунды по умолчанию.</summary>
    public TimeSpan EffectiveMonitorIntervalForProfile =>
        MonitorInterval == default ? TimeSpan.FromSeconds(2) : MonitorInterval;

    public TimeSpan EffectiveMaxSessionDuration => MaxSessionDuration == default ? TimeSpan.FromHours(6) : MaxSessionDuration;

    /// <summary>
    /// Не чаще одного «поиска пути получше» за это время. Иначе контроль начнёт дёргать маршрут
    /// ради каждого случайного выигрыша в 3 мс: переключение — это разрыв соединения на игре.
    /// </summary>
    public TimeSpan EffectiveRouteUpgradeCooldown =>
        RouteUpgradeCooldown == default ? TimeSpan.FromMinutes(5) : RouteUpgradeCooldown;
}

/// <summary>Снимок состояния для интерфейса: то, что видит пользователь на кнопке и в панели.</summary>
public sealed record RealtimeSnapshot(
    RealtimeOptimizationState State,
    string StatusRu,
    string? GameTitleRu,
    string? Endpoint,
    double? BaselineRttMs,
    double? CurrentRttMs,
    double? FloorRttMs,
    double? CurrentJitterMs,
    double? CurrentLossPercent,
    double? ImprovementMs,
    string QualityLabelRu,
    IReadOnlyList<OptimizationActionDescriptor> AppliedActions,
    IReadOnlyList<string> Warnings,
    DateTime UpdatedUtc,
    string BaselineMethodTag = "",
    string FinalMethodTag = "",
    // Выигрыш засчитан только тогда, когда разница медиан выше шума (95% ДИ не накрывает ноль).
    // Без этого флага «стало лучше» — это разница в 0,6 мс при разбросе 5 мс, то есть шум.
    bool GainConfirmed = false,
    string GainVerdictRu = "")
{
    public bool IsRunning => State is RealtimeOptimizationState.ActiveMonitoring
        or RealtimeOptimizationState.Degraded
        or RealtimeOptimizationState.ApplyingActions
        or RealtimeOptimizationState.ChoosingRoute;

    /// <summary>
    /// Честный вывод: выигрыш есть только тогда, когда разница медиан выше шума замера.
    /// Раньше здесь стоял порог 0,5 мс — он засчитывал шум как результат.
    /// </summary>
    public bool HasMeasuredImprovement => GainConfirmed && ImprovementMs is > 0;
}

/// <summary>Итог сессии: «до», «после», что применялось и что было снято.</summary>
public sealed record RealtimeStopReport(
    string GameTitleRu,
    string Endpoint,
    double? BaselineRttMs,
    double? FinalRttMs,
    double? FloorRttMs,
    double? ImprovementMs,
    string QualityLabelRu,
    TimeSpan Duration,
    IReadOnlyList<string> AppliedActionTitles,
    IReadOnlyList<string> RevertMessagesRu,
    bool EverythingReverted,
    string HonestSummaryRu,
    string BaselineMethodTag = "",
    string FinalMethodTag = "");
