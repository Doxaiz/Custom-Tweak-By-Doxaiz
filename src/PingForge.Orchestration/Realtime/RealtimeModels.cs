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

/// <summary>Одно окно замеров живого потока. Неизмеримо — это честный результат, а не нули.</summary>
public sealed record PathMeasurement(
    bool IsMeasurable,
    double RttMedianMs,
    double JitterMs,
    double LossPercent,
    int Samples,
    string MethodRu)
{
    public static PathMeasurement Unmeasurable(string reasonRu) => new(false, 0, 0, 0, 0, reasonRu);
}

/// <summary>
/// Источник замеров живого потока. В приложении это SIO_TCP_INFO для TCP и пассивные метки
/// на ответах сервера для UDP; в тестах — заранее заданная последовательность.
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

public sealed record OptimizationActionDescriptor(
    string ActionId,
    OptimizationActionKind Kind,
    string TitleRu,
    string DetailRu,
    bool IsReversible = true);

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

/// <summary>Альтернативный путь до того же адреса сервера (релей или другой шлюз). Нужен для переключений на ходу.</summary>
public sealed record RealtimePathOption(string PathId, string DisplayName, bool IsRelay, string? NextHop, OptimizationActionDescriptor Action);

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
    ResourceBudgetOptions? Budget = null)
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
    DateTime UpdatedUtc)
{
    public bool IsRunning => State is RealtimeOptimizationState.ActiveMonitoring
        or RealtimeOptimizationState.Degraded
        or RealtimeOptimizationState.ApplyingActions
        or RealtimeOptimizationState.ChoosingRoute;

    /// <summary>Честный вывод: выигрыш есть только тогда, когда он измерен и больше 0.</summary>
    public bool HasMeasuredImprovement => ImprovementMs is > 0.5;
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
    string HonestSummaryRu);
