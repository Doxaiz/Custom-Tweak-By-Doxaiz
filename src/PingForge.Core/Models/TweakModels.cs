namespace PingForge.Core.Models;

/// <summary>
/// Конкретная цель твика, зафиксированная ОДИН раз — в момент снятия снапшота.
/// Apply, Verify и Rollback обязаны работать по этой же цели: иначе при смене активного
/// адаптера между шагами значение уйдёт не туда, а откат вернёт его в третий адаптер.
/// </summary>
public sealed record TweakTarget(
    string ResourceLocator,
    string? AdapterInterfaceGuid = null,
    string? AdapterDisplayName = null)
{
    /// <summary>Цель, не привязанная к адаптеру (глобальные ключи TCP/IP, схема питания, DNS-кэш).</summary>
    public static TweakTarget Global(string resourceLocator) => new(resourceLocator);

    public bool IsAdapterBound => !string.IsNullOrWhiteSpace(AdapterInterfaceGuid);
}

/// <summary>Полные метаданные твика для карточки во вкладке «Мои твики» и для мастера.</summary>
public sealed record TweakDescriptor(
    string Code,
    string TitleRu,
    string TitleEn,
    TweakCategory Category,
    TweakMechanismKind Mechanism,
    string ResourceLocator,
    string WhatItDoesRu,
    GameRecommendationBadge Badge,
    string GamingJustificationRu,
    TweakRiskLevel Risk,
    int PingImpactDirection,
    int SpeedImpactDirection,
    int StabilityImpactDirection,
    bool RebootRequired,
    bool RequiresAdminPrivileges,
    bool RequiresManualVerification,
    string DefaultValueNoteRu,
    string RecommendedRawValue,
    TweakValueDataType DataType,
    bool IncludedInLowPingPreset,
    bool IncludedInMaxSpeedPreset)
{
    /// <summary>Входит в пресет «отзывчивость и кадры» (питание, фон, планировщик, ввод).</summary>
    public bool IncludedInFpsPreset { get; init; }

    /// <summary>Подкатегория для интерфейса: пинг, скорость, кадры, ввод или система.</summary>
    public TweakFocus FocusKind { get; init; } = TweakFocus.System;

    /// <summary>True, если у твика нет статичного целевого значения и оно вычисляется перед применением.</summary>
    public bool HasComputedTarget { get; init; }

    /// <summary>Честная пометка: параметр может не читаться современным стеком Windows.</summary>
    public bool EffectUnconfirmedOnModernWindows { get; init; }
}

/// <summary>Состояние параметра, прочитанное из системы.</summary>
public sealed record CapturedParameterState(
    string TweakCode,
    TweakMechanismKind Mechanism,
    TweakTarget Target,
    bool ExistedBefore,
    string? CapturedRawValue,
    TweakValueDataType ValueDataType,
    bool IsSupported,
    bool IsUnparsableOutput,
    string SupportReasonRu);

/// <summary>Результат Apply / Verify / Rollback с фактическим значением, прочитанным из системы.</summary>
public sealed record TweakOperationResult(
    string TweakCode,
    string TweakTitleRu,
    string ResourceLocator,
    TweakOutcome Outcome,
    string PreviousValueText,
    string TargetValueText,
    string ActualSystemValueText,
    string? FailureReasonRu,
    bool RebootOrAdapterRestartRequired,
    DateTime TimestampUtc)
{
    public bool IsVerifiedInSystem =>
        Outcome is TweakOutcome.VerifiedEffective or TweakOutcome.WrittenPendingRestart or TweakOutcome.RolledBack;

    /// <summary>Засчитывается ли операция как реально применённое изменение.</summary>
    public bool CountsAsApplied => Outcome is TweakOutcome.VerifiedEffective or TweakOutcome.WrittenPendingRestart;

    /// <summary>No-op и «не поддерживается» не должны попадать в счётчик «применено N».</summary>
    public bool IsNoChange => Outcome is TweakOutcome.SkippedNoOp
        or TweakOutcome.SkippedUnsupported
        or TweakOutcome.SkippedUnparsable
        or TweakOutcome.SkippedUnsafeTarget;

    public static TweakOperationResult Skip(string code, string title, string locator, TweakOutcome outcome, string reason, string currentValue) =>
        new(code, title, locator, outcome, currentValue, currentValue, currentValue, reason, false, DateTime.UtcNow);
}

/// <summary>Одна запись внутри снапшота: точное состояние параметра до вмешательства.</summary>
public sealed record SnapshotItemEntry(
    string TweakCode,
    TweakMechanismKind Mechanism,
    TweakTarget Target,
    bool ExistedBefore,
    string? CapturedRawValue,
    TweakValueDataType ValueDataType,
    string PlannedNewValue)
{
    public string ResourceLocator => Target.ResourceLocator;
    public string? AdapterInterfaceGuid => Target.AdapterInterfaceGuid;
    public string? AdapterDisplayName => Target.AdapterDisplayName;

    /// <summary>Пояснение, почему выбрано именно это целевое значение (для вычисляемых твиков).</summary>
    public string? PlannedValueReason { get; init; }
}

public sealed record SystemSnapshot(
    string SnapshotId,
    DateTime CreatedUtc,
    string Label,
    TweakProfilePreset SelectedProfile,
    bool OsRestorePointCreated,
    string PayloadSha256,
    bool WasRolledBack,
    DateTime? RolledBackUtc,
    IReadOnlyList<SnapshotItemEntry> Entries);

public sealed record SnapshotRollbackReport(
    string SnapshotId,
    SnapshotRollbackStatus Status,
    IReadOnlyList<TweakOperationResult> OperationResults,
    IReadOnlyList<string> OrphanedTweakCodes,
    string SummaryMessageRu)
{
    public int RolledBackCount => OperationResults.Count(r => r.Outcome == TweakOutcome.RolledBack);
    public int FailedCount => OperationResults.Count(r => r.Outcome == TweakOutcome.Failed);
}

/// <summary>Результат проверки поддерживаемости твика на конкретной машине.</summary>
public sealed record TweakSupport(bool IsSupported, bool RequiresManualVerification, string ReasonRu)
{
    public static TweakSupport Supported(string reason, bool manual = false) => new(true, manual, reason);
    public static TweakSupport Unsupported(string reason) => new(false, false, reason);
}

/// <summary>Итог батча применения твиков — с раздельными счётчиками, чтобы отчёт не врал.</summary>
public sealed record TweakBatchReport(
    SystemSnapshot Snapshot,
    RestorePointResultKind RestorePointStatus,
    string RestorePointMessageRu,
    IReadOnlyList<TweakOperationResult> Results,
    int AppliedCount,
    int NoOpCount,
    int SkippedCount,
    int FailedCount,
    bool RebootRequired);
