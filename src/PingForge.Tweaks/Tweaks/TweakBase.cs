using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Общая часть всех твиков: описание, разбор причины отказа и сборка результата операции.
/// Никаких «молчаливых» успехов: если система не подтвердила значение, операция считается проваленной.
/// </summary>
public abstract class TweakBase : ITweak
{
    protected TweakBase(TweakDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
    }

    public string Id => Descriptor.Code;

    public TweakDescriptor Descriptor { get; }

    public abstract Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default);

    public abstract Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default);

    public abstract Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default);

    public abstract Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default);

    public abstract Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default);

    protected TweakOperationResult BuildResult(
        SnapshotItemEntry entry,
        TweakOutcome outcome,
        string actualSystemValue,
        string? failureReason = null,
        bool requiresRestart = false) =>
        new(
            Descriptor.Code,
            Descriptor.TitleRu,
            entry.ResourceLocator,
            outcome,
            TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue),
            TweakValueCanonicalFormatter.Describe(entry.PlannedNewValue),
            actualSystemValue,
            failureReason,
            requiresRestart,
            DateTime.UtcNow);

    protected TweakOperationResult Skipped(SnapshotItemEntry entry, TweakOutcome outcome, string reason, string actual) =>
        TweakOperationResult.Skip(Descriptor.Code, Descriptor.TitleRu, entry.ResourceLocator, outcome, reason, actual);

    protected TweakSupport Unsupported(string reason) => TweakSupport.Unsupported(reason);
}
