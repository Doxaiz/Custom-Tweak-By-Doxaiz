using PingForge.Core.Models;

namespace PingForge.Core.Abstractions;

/// <summary>
/// Контракт твика. Ключевое отличие от «наивной» версии: Apply и Verify принимают
/// уже зафиксированную цель (SnapshotItemEntry), поэтому apply/verify/rollback всегда
/// работают с одним и тем же адаптером и ключом — даже если активный адаптер сменился.
/// </summary>
public interface ITweak
{
    string Id { get; }

    TweakDescriptor Descriptor { get; }

    /// <summary>Разрешает конкретную цель (например, GUID активного адаптера). Вызывается один раз при снапшоте.</summary>
    Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default);

    Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default);

    Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default);

    /// <summary>Читает фактическое значение из системы по той же цели и сравнивает с ожидаемым.</summary>
    Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default);

    Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Твик, целевое значение которого вычисляется перед применением (MTU по DF-пингу,
/// DNS — победитель бенчмарка, буферы NIC — допустимое значение драйвера).
/// </summary>
public interface IComputedTargetValueTweak : ITweak
{
    Task<ComputedTargetValue> ComputeTargetValueAsync(TweakTarget target, CancellationToken ct = default);
}

public sealed record ComputedTargetValue(string Value, TweakValueDataType DataType, string ExplanationRu, bool IsAvailable = true)
{
    public static ComputedTargetValue Unavailable(string reason) =>
        new(string.Empty, TweakValueDataType.None, reason, false);
}

/// <summary>Разовое действие без снапшота и отката (например, очистка DNS-кэша).</summary>
public interface IActionTask
{
    string TaskCode { get; }
    string TitleRu { get; }
    string DescriptionRu { get; }
    Task<(bool Succeeded, string MessageRu)> ExecuteActionAsync(CancellationToken ct = default);
}

public interface ITweakCatalog
{
    IReadOnlyList<ITweak> GetAllTweaks();
    IReadOnlyList<IActionTask> GetAllActionTasks();
    ITweak? GetByCode(string tweakCode);
    IReadOnlyList<ITweak> GetByPreset(TweakProfilePreset preset);
}

public interface ISnapshotService
{
    Task<(SystemSnapshot Snapshot, RestorePointResultKind RestorePointStatus, string RestorePointMessageRu)> CreateSnapshotAsync(
        string label, TweakProfilePreset preset, IReadOnlyList<ITweak> tweaks, bool requestWindowsRestorePoint, CancellationToken ct = default);

    Task<SnapshotRollbackReport> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default);

    Task<IReadOnlyList<SystemSnapshot>> GetAllSnapshotsAsync(CancellationToken ct = default);

    Task<int> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default) =>
        Task.FromResult(0);

    Task<long> GetDatabaseFileSizeAsync(CancellationToken ct = default) =>
        Task.FromResult(0L);

    string DatabasePath => string.Empty;
}

public interface ITweakCoordinator
{
    Task<TweakBatchReport> ExecuteBatchAsync(
        string batchTitle, TweakProfilePreset preset, IReadOnlyList<ITweak> selectedTweaks, bool requestWindowsRestorePoint, CancellationToken ct = default);

    Task<int> RecoverInterruptedOperationsAsync(CancellationToken ct = default);
}

public interface INetworkSnapshot
{
    Task<NetworkAndOsSnapshot> CaptureFullDiagnosticAsync(CancellationToken ct = default);
}

public interface IRouteProbe
{
    Task<ProbeStatistics> ProbeEndpointAsync(string hostOrIp, int port, string routeLabel, int sampleCount = 30, CancellationToken ct = default);
    Task<IReadOnlyList<RouteHopMetric>> TraceRouteAsync(string targetHostOrIp, int maxHops = 12, CancellationToken ct = default);
}

/// <summary>Снятие «нетвиковых» операций: маршруты, туннели, фильтры, DNS.</summary>
public interface ICrashRecoveryHandler
{
    Task<(bool Succeeded, string MessageRu)> UndoPendingOperationAsync(
        string operationCategory, string targetKey, string undoDescriptorJson, CancellationToken ct = default);
}

public interface ISystemRestorePointService
{
    (RestorePointResultKind Outcome, string MessageRu) TryCreateRestorePoint(string description);
}
