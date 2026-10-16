using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Security;
using PingForge.Core.Tweaks;
using PingForge.Data.Repositories;

namespace PingForge.Orchestration;

/// <summary>
/// Снапшот — единственный источник правды о состоянии системы до вмешательства.
///
/// Ключевые правила:
///   • цель каждого твика разрешается ОДИН раз и сохраняется в снапшоте (Apply/Verify/Rollback
///     дальше работают именно с ней, поэтому смена активного адаптера не ломает откат);
///   • у вычисляемых твиков (MTU, DNS, буферы NIC) целевое значение рассчитывается здесь
///     и тоже фиксируется в снапшоте;
///   • статус точки восстановления фиксируется честно, включая «не запрашивалась».
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    private readonly SnapshotRepository _repository;
    private readonly ITweakCatalog _catalog;
    private readonly TargetValueResolver _targetValueResolver;
    private readonly ISystemRestorePointService? _restorePoints;

    public SnapshotService(
        SnapshotRepository repository,
        ITweakCatalog catalog,
        TargetValueResolver? targetValueResolver = null,
        ISystemRestorePointService? restorePoints = null)
    {
        _repository = repository;
        _catalog = catalog;
        _targetValueResolver = targetValueResolver ?? new TargetValueResolver();
        _restorePoints = restorePoints;
    }

    public async Task<(SystemSnapshot Snapshot, RestorePointResultKind RestorePointStatus, string RestorePointMessageRu)> CreateSnapshotAsync(
        string label,
        TweakProfilePreset preset,
        IReadOnlyList<ITweak> tweaks,
        bool requestWindowsRestorePoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tweaks);

        (RestorePointResultKind status, string message) = ResolveRestorePoint(requestWindowsRestorePoint, label);

        var entries = new List<SnapshotItemEntry>(tweaks.Count);

        foreach (ITweak tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();

            (TweakTarget target, TweakSupport support) = await tweak.ResolveTargetAsync(ct).ConfigureAwait(false);
            CapturedParameterState captured = await tweak.CaptureAsync(target, ct).ConfigureAwait(false);

            TargetValueResolution resolution = await _targetValueResolver.ResolveAsync(tweak, target, ct).ConfigureAwait(false);

            string plannedValue = resolution.IsAvailable ? resolution.Value! : string.Empty;
            string reason = resolution.IsAvailable
                ? resolution.ExplanationRu
                : $"Целевое значение не определено: {resolution.ExplanationRu}";

            if (!support.IsSupported)
            {
                reason = $"Твик не поддерживается на этой системе: {support.ReasonRu}";
                plannedValue = string.Empty;
            }

            entries.Add(new SnapshotItemEntry(
                tweak.Id,
                tweak.Descriptor.Mechanism,
                target,
                captured.ExistedBefore,
                captured.CapturedRawValue,
                captured.ValueDataType,
                plannedValue)
            {
                PlannedValueReason = reason
            });
        }

        string snapshotId = Guid.NewGuid().ToString("N");
        string hash = SnapshotHasher.ComputeCanonicalHash(snapshotId, entries);

        var snapshot = new SystemSnapshot(
            snapshotId,
            DateTime.UtcNow,
            label,
            preset,
            status == RestorePointResultKind.CreatedSuccessfully,
            hash,
            WasRolledBack: false,
            RolledBackUtc: null,
            entries);

        await _repository.SaveAsync(snapshot, status, ct).ConfigureAwait(false);
        return (snapshot, status, message);
    }

    public async Task<SnapshotRollbackReport> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        StoredSnapshot stored = await _repository.GetByIdAsync(snapshotId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Снапшот «{snapshotId}» не найден.");

        if (!SnapshotHasher.VerifyIntegrity(stored.Snapshot))
        {
            throw new InvalidOperationException(
                $"Нарушена целостность снапшота «{snapshotId}»: контрольная сумма SHA-256 не совпадает. Откат не выполняется.");
        }

        var results = new List<TweakOperationResult>();
        var orphans = new List<string>();

        foreach (SnapshotItemEntry entry in stored.Snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();

            ITweak? tweak = _catalog.GetByCode(entry.TweakCode);
            if (tweak is null)
            {
                orphans.Add(entry.TweakCode);
                results.Add(TweakOperationResult.Skip(
                    entry.TweakCode,
                    $"Твик отсутствует в каталоге ({entry.TweakCode})",
                    entry.ResourceLocator,
                    TweakOutcome.SkippedUnsupported,
                    "Твик больше не входит в каталог — параметр не откатывался. Проверьте его вручную.",
                    TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue)));
                continue;
            }

            TweakOperationResult result = await tweak.RollbackAsync(entry, ct).ConfigureAwait(false);
            results.Add(result);
        }

        int rolledBack = results.Count(r => r.Outcome == TweakOutcome.RolledBack);
        int failed = results.Count(r => r.Outcome == TweakOutcome.Failed);
        int noop = results.Count(r => r.IsNoChange);

        SnapshotRollbackStatus status = failed > 0
            ? (rolledBack > 0 || noop > 0 ? SnapshotRollbackStatus.PartiallyRolledBack : SnapshotRollbackStatus.Failed)
            : orphans.Count > 0
                ? SnapshotRollbackStatus.RequiresManualVerification
                : SnapshotRollbackStatus.FullyRolledBack;

        await _repository.MarkRollbackAsync(snapshotId, status, ct).ConfigureAwait(false);

        string summary = status switch
        {
            SnapshotRollbackStatus.FullyRolledBack =>
                $"Полный откат выполнен: восстановлено параметров — {rolledBack}, изменений не требовалось — {noop}.",
            SnapshotRollbackStatus.PartiallyRolledBack =>
                $"Частичный откат: восстановлено {rolledBack}, не удалось {failed}, твиков вне каталога {orphans.Count}. " +
                $"Требуется ручная проверка параметров: {string.Join(", ", results.Where(r => r.Outcome == TweakOutcome.Failed).Select(r => r.TweakCode))}",
            SnapshotRollbackStatus.RequiresManualVerification =>
                $"Восстановлено параметров — {rolledBack}, изменений не требовалось — {noop}. " +
                $"Твиков нет в каталоге: {string.Join(", ", orphans)} — их параметры придётся вернуть вручную. " +
                "Кнопка «Откатить» такие значения не трогает, чтобы не записать наугад чужое значение.",
            _ => $"Откат не выполнен: успешно восстановлено {rolledBack} параметров, ошибок {failed}. Требуется ручная проверка."
        };

        return new SnapshotRollbackReport(snapshotId, status, results, orphans, summary);
    }

    public async Task<IReadOnlyList<SystemSnapshot>> GetAllSnapshotsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<StoredSnapshot> stored = await _repository.ListAsync(ct).ConfigureAwait(false);
        return stored.Select(s => s.Snapshot).ToList();
    }

    private (RestorePointResultKind Status, string Message) ResolveRestorePoint(bool requested, string label)
    {
        if (!requested)
        {
            return (RestorePointResultKind.NotRequested,
                "Точка восстановления Windows не запрашивалась. Создан внутренний снапшот PingForge с точными исходными значениями.");
        }

        if (_restorePoints is null)
        {
            return (RestorePointResultKind.NotRequested,
                "Служба точек восстановления недоступна. Создан внутренний снапшот PingForge.");
        }

        return _restorePoints.TryCreateRestorePoint($"PingForge: {label}");
    }
}
