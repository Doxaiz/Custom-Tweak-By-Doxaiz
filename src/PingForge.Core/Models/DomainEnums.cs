namespace PingForge.Core.Models;

/// <summary>Категория твика — используется для группировки во вкладке «Мои твики».</summary>
public enum TweakCategory
{
    NicDriver,
    TcpIpStack,

    /// <summary>Задержка ввода: мышь, клавиатура, USB-устройства, отрисовка кадра.</summary>
    InputLatency,

    PowerAndScheduler,
    WindowsBackground,
    DnsAndResolution
}

/// <summary>
/// Подкатегория твика — то, ради чего его включают. Нужна для навигации: «пинг», «скорость», «фпс», «ввод».
/// Это не «вторая категория», а ответ на вопрос пользователя: «что мне станет лучше».
/// </summary>
public enum TweakFocus
{
    /// <summary>Сетевой отклик: пинг, джиттер, потери.</summary>
    Ping,

    /// <summary>Пропускная способность: скорость загрузки и отдачи.</summary>
    Speed,

    /// <summary>Частота кадров и ровность кадра: фризы, статтеры, фоновая нагрузка.</summary>
    Fps,

    /// <summary>Задержка ввода: мышь, клавиатура, обработка кадра.</summary>
    Input,

    /// <summary>Общее состояние системы: питание, фоновые задачи, планировщик.</summary>
    System
}

public enum TweakRiskLevel
{
    Low,
    Medium,
    High
}

/// <summary>Бейдж «стоит ли применять для игр».</summary>
public enum GameRecommendationBadge
{
    Recommended,
    Optional,
    Caution,
    NotRecommended
}

/// <summary>Механизм, через который твик меняет систему.</summary>
public enum TweakMechanismKind
{
    Registry,
    WmiNicAdvancedProperty,
    WmiNicPowerManagement,
    NetshCommand,
    PowerCfgCommand,
    QoSPolicyRegistry,
    DnsResolverConfig,
    MtuSubinterface
}

public enum TweakValueDataType
{
    DWord,
    QWord,
    String,
    MultiString,
    Binary,
    CimString,
    CommandState,
    None
}

/// <summary>
/// Итог операции над твиком. Разделяет «значение записано» и «эффект подтверждён»,
/// а также отдельно кодирует no-op и отказы (нераспознанный вывод, нет поддержки).
/// </summary>
public enum TweakOutcome
{
    /// <summary>Значение записано, статус эффекта ещё не определён.</summary>
    Applied,

    /// <summary>Значение записано и эффект подтверждён в системе.</summary>
    VerifiedEffective,

    /// <summary>Значение записано, но вступит в силу после перезапуска адаптера/ПК.</summary>
    WrittenPendingRestart,

    RolledBack,
    Failed,

    /// <summary>Текущее значение уже равно целевому — применять нечего.</summary>
    SkippedNoOp,

    /// <summary>Вывод утилиты не распознан ни в русском, ни в английском варианте.</summary>
    SkippedUnparsable,

    SkippedUnsupported,

    /// <summary>Цель небезопасна (адаптер отсутствует, значение не прошло валидацию).</summary>
    SkippedUnsafeTarget
}

public enum TweakProfilePreset
{
    /// <summary>Низкий пинг: только то, что сокращает задержку и джиттер.</summary>
    LowLatencyGaming,

    /// <summary>Максимальная скорость: пропускная способность и буферы.</summary>
    MaxThroughput,

    /// <summary>Отзывчивость и кадры: питание, фон, планировщик, ввод.</summary>
    FpsAndResponsiveness,

    /// <summary>Откат к прежним настройкам — берётся из снапшота, а не из каталога.</summary>
    RestoreSnapshotDefault,

    CustomUserPreset
}

public enum SnapshotRollbackStatus
{
    /// <summary>Все параметры вернулись к значениям из снапшота.</summary>
    FullyRolledBack,

    /// <summary>Часть параметров восстановлена, часть — нет (или потребовалось вмешательство).</summary>
    PartiallyRolledBack,

    /// <summary>Откатывать нечего автоматически: твик исчез из каталога. Нужна ручная проверка параметра.</summary>
    RequiresManualVerification,

    /// <summary>Откат не выполнен: восстановить не удалось ничего.</summary>
    Failed
}

public enum RestorePointResultKind
{
    CreatedSuccessfully,
    SkippedFrequencyLimit24h,
    SystemProtectionDisabled,

    /// <summary>Пользователь/настройка не запрашивали точку восстановления — это НЕ ошибка.</summary>
    NotRequested,

    FailedWithError
}

public enum OptimizationEngineState
{
    IdleNoChanges,
    Analyzing,
    ProbingRoutes,
    ApplyingRoute,
    ActiveOptimized,
    DegradedWarning,
    StoppingAndRestoring,
    ReadOnlyNoAdmin
}

/// <summary>Каким способом реально получено измерение. Всегда попадает в отчёт.</summary>
public enum ProbeMethodUsed
{
    /// <summary>
    /// Живой TCP-поток игры: статистика уже открытого соединения (iphlpapi, TCP_ESTATS_PATH).
    /// Ни одного пакета сверх трафика игры не отправляется.
    /// </summary>
    LiveTcpEstats,

    /// <summary>Живой UDP-поток игры: пассивные метки времени на ответах сервера.</summary>
    LiveUdpPassiveFlow,

    ActiveIcmpEcho,
    ActiveTcpHandshake,

    /// <summary>Ни один метод не сработал. Числа не выдумываются.</summary>
    Unmeasurable
}

/// <summary>Фактическая причина, по которой измерить не удалось (раньше всегда писалось «ICMP+UDP+TCP заблокированы»).</summary>
public enum ProbeFailureCause
{
    None,
    ResolutionFailed,
    AllProbesTimedOut,
    PermissionDenied,
    TargetUnreachable
}

/// <summary>Результат одной DF-пробы при подборе Path MTU.</summary>
public enum DfProbeResult
{
    /// <summary>Пакет указанного размера прошёл без фрагментации.</summary>
    Success,

    /// <summary>ICMP «fragmentation needed» — этот размер слишком велик.</summary>
    PacketTooBig,

    /// <summary>Таймаут — НЕ означает «слишком большой»; требует повтора.</summary>
    Timeout,

    Other
}

public enum MtuDiscoveryOutcome
{
    OptimalFound,

    /// <summary>ICMP блокируется — определить нельзя, остаёмся на текущем значении.</summary>
    UndeterminedIcmpBlocked,

    /// <summary>Пробы дают противоречивый результат — определить нельзя.</summary>
    UndeterminedInconsistent
}

public enum RoutingMode
{
    DirectOnly,
    SingleBestPop,
    MultipathRedundantFec,
    AutoFailover
}

/// <summary>Что именно делает изменение таблицы маршрутизации — нужно для точного undo.</summary>
public enum RouteChangeKind
{
    Add,
    Delete,
    Modify
}

public enum FailoverDecisionKind
{
    StayOnActive,

    /// <summary>Мало данных или активный маршрут в порядке.</summary>
    InsufficientData,

    SwitchToCandidate,

    /// <summary>Оптимизация не даёт выигрыша — честная рекомендация выключить релей.</summary>
    RecommendDisableRelay
}

public enum AntiCheatStatus
{
    VerifiedSafe,
    Untested,
    KnownKernelConflict
}
