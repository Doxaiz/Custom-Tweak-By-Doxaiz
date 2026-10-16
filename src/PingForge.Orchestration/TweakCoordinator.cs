using System.Text.Json;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Security;
using PingForge.Data.Repositories;

namespace PingForge.Orchestration;

/// <summary>
/// Транзакционное применение твиков:
///   1. снапшот — до первого изменения;
///   2. запись PENDING в журнал предзаписи — до каждого изменения;
///   3. Apply → Verify по ОДНОЙ сохранённой цели;
///   4. при провале верификации или исключении — немедленный откат;
///   5. восстановление незавершённых операций при старте — для ВСЕХ категорий
///      (не только TWEAK_APPLY: маршруты, туннели, фильтры и DNS тоже снимаются).
/// </summary>
public sealed class TweakCoordinator : ITweakCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly ISnapshotService _snapshotService;
    private readonly AuditJournalRepository _auditJournal;
    private readonly ITweakCatalog _catalog;
    private readonly ICrashRecoveryHandler? _crashRecovery;

    public TweakCoordinator(
        ISnapshotService snapshotService,
        AuditJournalRepository auditJournal,
        ITweakCatalog catalog,
        ICrashRecoveryHandler? crashRecovery = null)
    {
        _snapshotService = snapshotService;
        _auditJournal = auditJournal;
        _catalog = catalog;
        _crashRecovery = crashRecovery;
    }

    public async Task<TweakBatchReport> ExecuteBatchAsync(
        string batchTitle,
        TweakProfilePreset preset,
        IReadOnlyList<ITweak> selectedTweaks,
        bool requestWindowsRestorePoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedTweaks);

        if (selectedTweaks.Count == 0)
        {
            throw new ArgumentException("Не выбран ни один твик для применения.", nameof(selectedTweaks));
        }

        (SystemSnapshot snapshot, RestorePointResultKind restoreStatus, string restoreMessage) =
            await _snapshotService.CreateSnapshotAsync(batchTitle, preset, selectedTweaks, requestWindowsRestorePoint, ct).ConfigureAwait(false);

        if (!SnapshotHasher.VerifyIntegrity(snapshot))
        {
            throw new InvalidOperationException("Контрольная сумма только что созданного снапшота не совпала — применение отменено.");
        }

        var results = new List<TweakOperationResult>(selectedTweaks.Count);
        int applied = 0;
        int noOp = 0;
        int skipped = 0;
        int failed = 0;

        foreach (ITweak tweak in selectedTweaks)
        {
            ct.ThrowIfCancellationRequested();

            SnapshotItemEntry entry = snapshot.Entries.First(e => string.Equals(e.TweakCode, tweak.Id, StringComparison.OrdinalIgnoreCase));
            string operationId = Guid.NewGuid().ToString("N");

            await _auditJournal.BeginPendingOperationAsync(
                operationId,
                snapshot.SnapshotId,
                AuditJournalRepository.OperationCategories.TweakApply,
                entry.ResourceLocator,
                JsonSerializer.Serialize(entry, JsonOptions),
                ct).ConfigureAwait(false);

            try
            {
                TweakOperationResult appliedResult = await tweak.ApplyAsync(entry, ct).ConfigureAwait(false);

                switch (appliedResult.Outcome)
                {
                    case TweakOutcome.VerifiedEffective:
                    case TweakOutcome.WrittenPendingRestart:
                    case TweakOutcome.Applied:
                    {
                        // Дополнительная верификация по сохранённой цели: доверяем только чтению из системы.
                        TweakOperationResult verified = await tweak.VerifyAsync(entry, ct).ConfigureAwait(false);
                        TweakOperationResult finalResult = verified.Outcome is TweakOutcome.VerifiedEffective or TweakOutcome.WrittenPendingRestart
                            ? verified
                            : appliedResult;

                        applied++;
                        await _auditJournal.CompletePendingOperationAsync(operationId, succeeded: true, null, ct).ConfigureAwait(false);
                        await _auditJournal.AppendModificationLogAsync("APPLY", finalResult, ct).ConfigureAwait(false);
                        results.Add(finalResult);
                        break;
                    }

                    case TweakOutcome.SkippedNoOp:
                    {
                        noOp++;
                        await _auditJournal.CompletePendingOperationAsync(operationId, succeeded: true, "SkippedNoOp", ct).ConfigureAwait(false);
                        await _auditJournal.AppendModificationLogAsync("SKIPPED_NO_OP", appliedResult, ct).ConfigureAwait(false);
                        results.Add(appliedResult);
                        break;
                    }

                    case TweakOutcome.SkippedUnsupported:
                    case TweakOutcome.SkippedUnparsable:
                    case TweakOutcome.SkippedUnsafeTarget:
                    {
                        skipped++;
                        await _auditJournal.CompletePendingOperationAsync(operationId, succeeded: false, appliedResult.FailureReasonRu, ct).ConfigureAwait(false);
                        await _auditJournal.AppendModificationLogAsync("SKIPPED_UNPARSABLE", appliedResult, ct).ConfigureAwait(false);
                        results.Add(appliedResult);
                        break;
                    }

                    default:
                    {
                        TweakOperationResult rollback = await tweak.RollbackAsync(entry, ct).ConfigureAwait(false);
                        failed++;
                        await _auditJournal.CompletePendingOperationAsync(operationId, succeeded: false, appliedResult.FailureReasonRu, ct).ConfigureAwait(false);
                        await _auditJournal.AppendModificationLogAsync("VERIFY_FAIL", appliedResult, ct).ConfigureAwait(false);
                        results.Add(appliedResult with
                        {
                            FailureReasonRu = $"{appliedResult.FailureReasonRu} Выполнен автооткат: {DescribeRollback(rollback)}"
                        });
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Отмена не должна оставить систему в промежуточном состоянии.
                await SafeRollbackAsync(tweak, entry, ct).ConfigureAwait(false);
                await _auditJournal.CompletePendingOperationAsync(operationId, succeeded: false, "Операция отменена пользователем.", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                TweakOperationResult rollback = await SafeRollbackAsync(tweak, entry, ct).ConfigureAwait(false);
                failed++;

                var failedResult = new TweakOperationResult(
                    tweak.Id,
                    tweak.Descriptor.TitleRu,
                    entry.ResourceLocator,
                    TweakOutcome.Failed,
                    TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue),
                    entry.PlannedNewValue,
                    "ошибка выполнения",
                    $"Исключение при применении: {ex.Message}. Автооткат: {DescribeRollback(rollback)}",
                    false,
                    DateTime.UtcNow);

                await _auditJournal.CompletePendingOperationAsync(operationId, succeeded: false, ex.Message, ct).ConfigureAwait(false);
                await _auditJournal.AppendModificationLogAsync("VERIFY_FAIL", failedResult, ct).ConfigureAwait(false);
                results.Add(failedResult);
            }
        }

        bool rebootRequired = results.Any(r => r.RebootOrAdapterRestartRequired && r.CountsAsApplied);

        return new TweakBatchReport(snapshot, restoreStatus, restoreMessage, results, applied, noOp, skipped, failed, rebootRequired);
    }

    /// <summary>
    /// Восстановление после падения службы или перезагрузки. Обрабатываются ВСЕ категории журнала:
    /// раньше незавершённые маршруты и туннели оставались в системе навсегда.
    /// </summary>
    public async Task<int> RecoverInterruptedOperationsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingOperationRecord> pending = await _auditJournal.GetPendingOperationsAsync(ct).ConfigureAwait(false);
        int recovered = 0;

        foreach (PendingOperationRecord operation in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (operation.Category == AuditJournalRepository.OperationCategories.TweakApply)
            {
                recovered += await RecoverTweakAsync(operation, ct).ConfigureAwait(false);
                continue;
            }

            recovered += await RecoverNonTweakAsync(operation, ct).ConfigureAwait(false);
        }

        return recovered;
    }

    private async Task<int> RecoverTweakAsync(PendingOperationRecord operation, CancellationToken ct)
    {
        SnapshotItemEntry? entry = DeserializeEntry(operation.UndoDescriptorJson);
        if (entry is null)
        {
            await _auditJournal.MarkOperationRolledBackAsync(
                operation.OpId,
                "Дескриптор операции повреждён — требуется ручная проверка параметра " + operation.TargetKey,
                ct).ConfigureAwait(false);
            return 0;
        }

        ITweak? tweak = _catalog.GetByCode(entry.TweakCode);
        if (tweak is null)
        {
            await _auditJournal.MarkOperationRolledBackAsync(
                operation.OpId,
                $"Твик {entry.TweakCode} отсутствует в каталоге — параметр {entry.ResourceLocator} требует ручной проверки",
                ct).ConfigureAwait(false);
            return 0;
        }

        TweakOperationResult rollback = await tweak.RollbackAsync(entry, ct).ConfigureAwait(false);
        await _auditJournal.AppendModificationLogAsync("CRASH_RECOVERY", rollback, ct).ConfigureAwait(false);
        await _auditJournal.MarkOperationRolledBackAsync(operation.OpId, rollback.FailureReasonRu, ct).ConfigureAwait(false);
        return 1;
    }

    private async Task<int> RecoverNonTweakAsync(PendingOperationRecord operation, CancellationToken ct)
    {
        if (_crashRecovery is null)
        {
            // Молча пропускать нельзя: помечаем операцию как требующую внимания и пишем в журнал.
            var noHandler = new TweakOperationResult(
                operation.Category,
                $"Аварийный сброс невозможен ({operation.Category})",
                operation.TargetKey,
                TweakOutcome.Failed,
                "PENDING",
                "<не снято>",
                "<не снято>",
                "Обработчик аварийного восстановления сетевых операций не зарегистрирован. " +
                "Проверьте маршруты, туннели и фильтры вручную либо нажмите «Починить сеть».",
                false,
                DateTime.UtcNow);

            await _auditJournal.AppendModificationLogAsync("CRASH_RECOVERY", noHandler, ct).ConfigureAwait(false);
            await _auditJournal.MarkOperationRolledBackAsync(
                operation.OpId,
                "Автоматический откат невозможен: обработчик сетевых операций не зарегистрирован, требуется ручная проверка.",
                ct).ConfigureAwait(false);
            return 0;
        }

        (bool succeeded, string message) = await _crashRecovery
            .UndoPendingOperationAsync(operation.Category, operation.TargetKey, operation.UndoDescriptorJson, ct)
            .ConfigureAwait(false);

        var log = new TweakOperationResult(
            operation.Category,
            $"Аварийный сброс ({operation.Category})",
            operation.TargetKey,
            succeeded ? TweakOutcome.RolledBack : TweakOutcome.Failed,
            "PENDING",
            succeeded ? "<снято>" : "<не снято>",
            succeeded ? "<снято>" : "<не снято>",
            message,
            false,
            DateTime.UtcNow);

        await _auditJournal.AppendModificationLogAsync("CRASH_RECOVERY", log, ct).ConfigureAwait(false);
        await _auditJournal.MarkOperationRolledBackAsync(operation.OpId, message, ct).ConfigureAwait(false);
        return succeeded ? 1 : 0;
    }

    private async Task<TweakOperationResult> SafeRollbackAsync(ITweak tweak, SnapshotItemEntry entry, CancellationToken ct)
    {
        try
        {
            return await tweak.RollbackAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new TweakOperationResult(
                tweak.Id,
                tweak.Descriptor.TitleRu,
                entry.ResourceLocator,
                TweakOutcome.Failed,
                TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue),
                TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue),
                "откат не выполнен",
                $"Автооткат завершился исключением: {ex.Message}",
                false,
                DateTime.UtcNow);
        }
    }

    private static string DescribeRollback(TweakOperationResult rollback) => rollback.Outcome switch
    {
        TweakOutcome.RolledBack => "восстановлено исходное значение.",
        TweakOutcome.SkippedUnsupported => "откат не требовался (цель не поддерживается).",
        TweakOutcome.SkippedNoOp => "откат не требовался (изменений не было).",
        _ => $"откат не удался ({rollback.FailureReasonRu ?? "причина неизвестна"})."
    };

    private static SnapshotItemEntry? DeserializeEntry(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SnapshotItemEntry>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
