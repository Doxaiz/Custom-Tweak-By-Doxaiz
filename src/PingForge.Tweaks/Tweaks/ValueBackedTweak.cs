using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Твик, который работает с одним значением. Вся опасная логика — «уже стоит нужное значение»,
/// «проверить, что система подтвердила запись», «вернуть прежнее или удалить созданное» —
/// живёт здесь однажды и покрыта тестами, а не повторяется в каждом твике.
/// </summary>
public class ValueBackedTweak : TweakBase
{
    private readonly IValueAccessor _accessor;
    private readonly IActiveAdapterResolver? _adapterResolver;

    public ValueBackedTweak(TweakDescriptor descriptor, IValueAccessor accessor, IActiveAdapterResolver? adapterResolver = null)
        : base(descriptor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _adapterResolver = adapterResolver;
    }

    public IValueAccessor Accessor => _accessor;

    public override async Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default)
    {
        TweakTarget target = await ResolveRawTargetAsync(ct).ConfigureAwait(false);
        TweakSupport support = await _accessor.CheckSupportAsync(target, ct).ConfigureAwait(false);
        return (target, support);
    }

    public override async Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default)
    {
        TweakSupport support = await _accessor.CheckSupportAsync(target, ct).ConfigureAwait(false);
        if (!support.IsSupported)
        {
            return new CapturedParameterState(
                Id, _accessor.Mechanism, target, false, null, Descriptor.DataType, false, false, support.ReasonRu);
        }

        (bool exists, string? raw) = await _accessor.ReadAsync(target, ct).ConfigureAwait(false);

        return new CapturedParameterState(
            Id, _accessor.Mechanism, target, exists, raw, Descriptor.DataType, true, false, support.ReasonRu);
    }

    public override async Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        TweakSupport support = await _accessor.CheckSupportAsync(entry.Target, ct).ConfigureAwait(false);
        if (!support.IsSupported)
        {
            return Skipped(entry, TweakOutcome.SkippedUnsupported, support.ReasonRu, "<н/д>");
        }

        string planned = entry.PlannedNewValue?.Trim() ?? string.Empty;
        if (planned.Length == 0)
        {
            return Skipped(entry, TweakOutcome.SkippedUnsafeTarget,
                "Целевое значение не определено: применять наугад запрещено.", "<н/д>");
        }

        try
        {
            (bool exists, string? current) = await _accessor.ReadAsync(entry.Target, ct).ConfigureAwait(false);
            if (exists && TweakValueCanonicalFormatter.AreEqual(entry.ValueDataType, current, planned))
            {
                return Skipped(entry, TweakOutcome.SkippedNoOp, "Параметр уже имеет нужное значение.", Describe(current));
            }

            await _accessor.WriteAsync(entry.Target, planned, ct).ConfigureAwait(false);

            (bool existsAfter, string? actual) = await _accessor.ReadAsync(entry.Target, ct).ConfigureAwait(false);
            if (!existsAfter || !TweakValueCanonicalFormatter.AreEqual(entry.ValueDataType, actual, planned))
            {
                return BuildResult(
                    entry,
                    TweakOutcome.Failed,
                    Describe(actual),
                    $"Система не подтвердила запись: ожидалось «{planned}», в системе «{Describe(actual)}».");
            }

            TweakOutcome outcome = Descriptor.RebootRequired
                ? TweakOutcome.WrittenPendingRestart
                : TweakOutcome.VerifiedEffective;

            return BuildResult(entry, outcome, Describe(actual), requiresRestart: Descriptor.RebootRequired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BuildResult(entry, TweakOutcome.Failed, "<н/д>", ex.Message);
        }
    }

    public override async Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            (bool exists, string? actual) = await _accessor.ReadAsync(entry.Target, ct).ConfigureAwait(false);
            bool matches = exists && TweakValueCanonicalFormatter.AreEqual(entry.ValueDataType, actual, entry.PlannedNewValue);

            TweakOutcome outcome = matches
                ? Descriptor.RebootRequired ? TweakOutcome.WrittenPendingRestart : TweakOutcome.VerifiedEffective
                : TweakOutcome.Failed;

            return BuildResult(
                entry,
                outcome,
                Describe(actual),
                matches ? null : $"Фактическое значение «{Describe(actual)}» не совпало с применённым «{entry.PlannedNewValue}».",
                requiresRestart: matches && Descriptor.RebootRequired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BuildResult(entry, TweakOutcome.Failed, "<н/д>", ex.Message);
        }
    }

    public override async Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            if (!entry.ExistedBefore || entry.CapturedRawValue is null)
            {
                await _accessor.DeleteAsync(entry.Target, ct).ConfigureAwait(false);

                (bool stillExists, string? leftover) = await _accessor.ReadAsync(entry.Target, ct).ConfigureAwait(false);
                return stillExists
                    ? BuildResult(entry, TweakOutcome.Failed, Describe(leftover),
                        "Параметра до оптимизации не было, но удалить созданное значение не удалось.")
                    : BuildResult(entry, TweakOutcome.RolledBack, "<удалено>");
            }

            string restore = entry.CapturedRawValue;
            await _accessor.WriteAsync(entry.Target, restore, ct).ConfigureAwait(false);

            (bool exists, string? actual) = await _accessor.ReadAsync(entry.Target, ct).ConfigureAwait(false);
            return exists && TweakValueCanonicalFormatter.AreEqual(entry.ValueDataType, actual, restore)
                ? BuildResult(entry, TweakOutcome.RolledBack, Describe(actual))
                : BuildResult(entry, TweakOutcome.Failed, Describe(actual),
                    $"Не удалось вернуть прежнее значение «{restore}»: в системе «{Describe(actual)}».");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BuildResult(entry, TweakOutcome.Failed, "<н/д>", ex.Message);
        }
    }

    protected virtual async Task<TweakTarget> ResolveRawTargetAsync(CancellationToken ct)
    {
        if (!_accessor.IsAdapterScoped)
        {
            return TweakTarget.Global(Descriptor.ResourceLocator);
        }

        if (_adapterResolver is null)
        {
            return TweakTarget.Global(Descriptor.ResourceLocator);
        }

        return await _adapterResolver.ResolveActiveAdapterAsync(ct).ConfigureAwait(false);
    }

    private static string Describe(string? raw) => TweakValueCanonicalFormatter.Describe(raw);
}

/// <summary>
/// Твик, целевое значение которого нельзя записать константой: MTU считается пробами с флагом DF,
/// DNS — победителем бенчмарка, буферы NIC — максимально допустимым значением драйвера.
/// Вычисленное значение и объяснение сохраняются в снапшоте до применения.
/// </summary>
public sealed class ComputedValueTweak : ValueBackedTweak, IComputedTargetValueTweak
{
    private readonly Func<TweakTarget, CancellationToken, Task<ComputedTargetValue>> _compute;

    public ComputedValueTweak(
        TweakDescriptor descriptor,
        IValueAccessor accessor,
        Func<TweakTarget, CancellationToken, Task<ComputedTargetValue>> computeTargetValue,
        IActiveAdapterResolver? adapterResolver = null)
        : base(descriptor, accessor, adapterResolver)
    {
        _compute = computeTargetValue ?? throw new ArgumentNullException(nameof(computeTargetValue));
    }

    public Task<ComputedTargetValue> ComputeTargetValueAsync(TweakTarget target, CancellationToken ct = default) =>
        _compute(target, ct);
}

/// <summary>
/// Заглушка на случай, когда порт платформы не подключён (например, тесты на Linux).
/// Твик обязан честно сказать «не поддерживается», а не делать вид, что применился.
/// </summary>
public sealed class MissingPortAccessor : IValueAccessor
{
    private readonly string _reasonRu;

    public MissingPortAccessor(string reasonRu) => _reasonRu = reasonRu;

    public TweakMechanismKind Mechanism => TweakMechanismKind.Registry;

    public bool IsAdapterScoped => false;

    public Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default) =>
        Task.FromResult(TweakSupport.Unsupported(_reasonRu));

    public Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default) =>
        Task.FromResult((false, (string?)null));

    public Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default) =>
        throw new NotSupportedException(_reasonRu);

    public Task DeleteAsync(TweakTarget target, CancellationToken ct = default) =>
        throw new NotSupportedException(_reasonRu);
}
