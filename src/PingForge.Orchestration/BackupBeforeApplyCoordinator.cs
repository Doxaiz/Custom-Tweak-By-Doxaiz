using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Data;

namespace PingForge.Orchestration;

/// <summary>
/// Обёртка над координатором применения (задача U): перед каждой пакетной операцией делает резервную
/// копию базы. Порядок важен — копия снимается ДО снапшота и до первого изменения системы, иначе
/// копия уже содержала бы наполовину применённое состояние.
///
/// Сбой копирования не блокирует применение: без копии программа всё равно обязана работать, но об
/// этом честно пишется в журнал службы — молчаливой «всё хорошо» здесь быть не должно.
/// </summary>
public sealed class BackupBeforeApplyCoordinator : ITweakCoordinator
{
    private readonly ITweakCoordinator _inner;
    private readonly DatabaseBackupService _backups;
    private readonly Action<string>? _note;

    public BackupBeforeApplyCoordinator(
        ITweakCoordinator inner,
        DatabaseBackupService backups,
        Action<string>? note = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _note = note;
    }

    public async Task<TweakBatchReport> ExecuteBatchAsync(
        string batchTitle,
        TweakProfilePreset preset,
        IReadOnlyList<ITweak> selectedTweaks,
        bool requestWindowsRestorePoint,
        CancellationToken ct = default)
    {
        await TryBackupAsync(ct).ConfigureAwait(false);

        return await _inner
            .ExecuteBatchAsync(batchTitle, preset, selectedTweaks, requestWindowsRestorePoint, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Восстановление незавершённых операций ничего не меняет в системе, поэтому копия перед ним не
    /// нужна: база читается, а не переписывается.
    /// </summary>
    public Task<int> RecoverInterruptedOperationsAsync(CancellationToken ct = default) =>
        _inner.RecoverInterruptedOperationsAsync(ct);

    private async Task TryBackupAsync(CancellationToken ct)
    {
        try
        {
            string? path = await _backups.CreateBackupAsync(ct).ConfigureAwait(false);

            _note?.Invoke(path is null
                ? "База ещё не создана: резервную копию перед применением делать не из чего."
                : $"Резервная копия базы перед применением: {Path.GetFileName(path)}.");
        }
        // Ловим любой сбой копии, кроме отмены: неожиданная ошибка файловой системы тоже не повод
        // не применять твики — но и молчать о ней нельзя.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _note?.Invoke(
                $"Резервную копию базы перед применением сделать не удалось: {ex.Message}. " +
                "Применение продолжается, но свежей копии базы нет.");
        }
    }
}
