using Microsoft.Data.Sqlite;
using PingForge.Core.Models;

namespace PingForge.Data.Repositories;

public sealed record PendingOperationRecord(
    string OpId,
    string? SnapshotId,
    string Category,
    string TargetKey,
    string UndoDescriptorJson,
    DateTime StartedUtc);

public sealed record AuditFindingRecord(
    string Category,
    string Code,
    string TitleRu,
    string DetailsRu,
    TweakRiskLevel Severity);

/// <summary>
/// Запись журнала изменений в том виде, в каком её показывает пользователь: что было, что стало
/// и что реально прочитано из системы после применения.
/// </summary>
public sealed record ModificationLogRecord(
    DateTime LoggedUtc,
    string EventKind,
    string TweakCode,
    string TweakTitleRu,
    string ResourceLocator,
    string PreviousValueText,
    string AppliedValueText,
    string VerifiedActualText,
    bool Succeeded,
    bool RebootNeeded,
    string ExplanationRu);

/// <summary>
/// Журнал предзаписи и журнал изменений. Запись в pending_operations_journal выполняется
/// ДО любого изменения системы, чтобы после падения можно было восстановиться.
/// </summary>
public sealed class AuditJournalRepository
{
    public static class OperationCategories
    {
        public const string TweakApply = "TWEAK_APPLY";
        public const string RouteAdd = "ROUTE_ADD";
        public const string WinDivertStart = "WINDIVERT_START";
        public const string TunnelUp = "TUNNEL_UP";
        public const string DnsSet = "DNS_SET";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            TweakApply, RouteAdd, WinDivertStart, TunnelUp, DnsSet
        };
    }

    private readonly SqliteDatabaseInitializer _database;

    public AuditJournalRepository(SqliteDatabaseInitializer database) => _database = database;

    public async Task BeginPendingOperationAsync(
        string operationId,
        string? snapshotId,
        string category,
        string targetKey,
        string undoDescriptorJson,
        CancellationToken ct = default)
    {
        if (!OperationCategories.All.Contains(category))
        {
            throw new ArgumentException($"Неизвестная категория операции «{category}».", nameof(category));
        }

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_operations_journal (
                op_id, snapshot_id, op_category, target_key, undo_descriptor_json, op_state, started_utc
            ) VALUES ($id, $snapshot, $category, $target, $undo, 'PENDING', $now);
            """;
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$snapshot", (object?)snapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$target", targetKey);
        command.Parameters.AddWithValue("$undo", undoDescriptorJson);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompletePendingOperationAsync(
        string operationId,
        bool succeeded,
        string? failureReason = null,
        CancellationToken ct = default)
    {
        await UpdateStateAsync(operationId, succeeded ? "COMMITTED" : "FAILED", failureReason, ct).ConfigureAwait(false);
    }

    public async Task MarkOperationRolledBackAsync(string operationId, string? note = null, CancellationToken ct = default) =>
        await UpdateStateAsync(operationId, "ROLLED_BACK", note, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PendingOperationRecord>> GetPendingOperationsAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT op_id, snapshot_id, op_category, target_key, undo_descriptor_json, started_utc
            FROM pending_operations_journal WHERE op_state = 'PENDING'
            ORDER BY started_utc ASC;
            """;

        var result = new List<PendingOperationRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PendingOperationRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                EnumCodec.ParseUtc(reader.GetString(5), "started_utc")));
        }

        return result;
    }

    public async Task AppendModificationLogAsync(string eventKind, TweakOperationResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO system_modification_log (
                logged_utc, event_kind, tweak_code, tweak_title, resource_locator,
                previous_value_text, applied_value_text, verified_actual_text,
                succeeded, reboot_needed, explanation
            ) VALUES ($ts, $kind, $code, $title, $locator, $previous, $applied, $verified, $ok, $reboot, $explanation);
            """;
        command.Parameters.AddWithValue("$ts", result.TimestampUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$kind", eventKind);
        command.Parameters.AddWithValue("$code", result.TweakCode);
        command.Parameters.AddWithValue("$title", result.TweakTitleRu);
        command.Parameters.AddWithValue("$locator", result.ResourceLocator);
        command.Parameters.AddWithValue("$previous", result.PreviousValueText);
        command.Parameters.AddWithValue("$applied", result.TargetValueText);
        command.Parameters.AddWithValue("$verified", result.ActualSystemValueText);
        command.Parameters.AddWithValue("$ok", result.IsVerifiedInSystem ? 1 : 0);
        command.Parameters.AddWithValue("$reboot", result.RebootOrAdapterRestartRequired ? 1 : 0);
        command.Parameters.AddWithValue("$explanation", result.FailureReasonRu ?? result.Outcome.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Последние записи журнала изменений, новые сверху. Читается ровно столько, сколько просили:
    /// журнал растёт с каждым применением, и «покажи всё» превратилось бы в тормоза на пустом месте.
    /// </summary>
    public async Task<IReadOnlyList<ModificationLogRecord>> GetModificationLogTailAsync(
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<ModificationLogRecord>();
        }

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT logged_utc, event_kind, tweak_code, tweak_title, resource_locator,
                   previous_value_text, applied_value_text, verified_actual_text,
                   succeeded, reboot_needed, explanation
            FROM system_modification_log
            ORDER BY logged_utc DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<ModificationLogRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ModificationLogRecord(
                EnumCodec.ParseUtc(reader.GetString(0), "logged_utc"),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8) != 0,
                reader.GetInt32(9) != 0,
                reader.GetString(10)));
        }

        return result;
    }

    public async Task<int> CountModificationLogEntriesAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM system_modification_log;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    public async Task SaveSupportMatrixEntryAsync(
        string tweakCode,
        string targetDescription,
        bool isSupported,
        bool requiresManualVerification,
        string reasonRu,
        string? liveValue,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tweak_support_matrix (
                tweak_code, target_description, is_supported, requires_manual_verification,
                support_reason_ru, current_live_value, evaluated_utc
            ) VALUES ($code, $target, $supported, $manual, $reason, $value, $now)
            ON CONFLICT(tweak_code) DO UPDATE SET
                target_description = excluded.target_description,
                is_supported = excluded.is_supported,
                requires_manual_verification = excluded.requires_manual_verification,
                support_reason_ru = excluded.support_reason_ru,
                current_live_value = excluded.current_live_value,
                evaluated_utc = excluded.evaluated_utc;
            """;
        command.Parameters.AddWithValue("$code", tweakCode);
        command.Parameters.AddWithValue("$target", targetDescription);
        command.Parameters.AddWithValue("$supported", isSupported ? 1 : 0);
        command.Parameters.AddWithValue("$manual", requiresManualVerification ? 1 : 0);
        command.Parameters.AddWithValue("$reason", reasonRu);
        command.Parameters.AddWithValue("$value", (object?)liveValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Полная замена результатов сканирования. Предупреждения о помехах (CONFLICT_WARNING)
    /// тоже сохраняются — иначе после перезапуска они исчезают из UI и из отчёта.
    /// </summary>
    public async Task ReplaceAuditFindingsAsync(IEnumerable<AuditFindingRecord> findings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM system_audit_findings;";
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        string now = DateTime.UtcNow.ToString("O");
        foreach (AuditFindingRecord finding in findings)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO system_audit_findings (scanned_utc, finding_category, code, title_ru, details_ru, severity)
                VALUES ($now, $category, $code, $title, $details, $severity);
                """;
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$category", finding.Category);
            insert.Parameters.AddWithValue("$code", finding.Code);
            insert.Parameters.AddWithValue("$title", finding.TitleRu);
            insert.Parameters.AddWithValue("$details", finding.DetailsRu);
            insert.Parameters.AddWithValue("$severity", finding.Severity.ToString());
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditFindingRecord>> GetAuditFindingsAsync(string? category = null, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = category is null
            ? "SELECT finding_category, code, title_ru, details_ru, severity FROM system_audit_findings ORDER BY finding_id;"
            : "SELECT finding_category, code, title_ru, details_ru, severity FROM system_audit_findings WHERE finding_category = $category ORDER BY finding_id;";

        if (category is not null)
        {
            command.Parameters.AddWithValue("$category", category);
        }

        var result = new List<AuditFindingRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AuditFindingRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                EnumCodec.ParseOrThrow<TweakRiskLevel>(reader.GetString(4), "severity")));
        }

        return result;
    }

    private async Task UpdateStateAsync(string operationId, string state, string? note, CancellationToken ct)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_operations_journal
            SET op_state = $state, finished_utc = $now, failure_reason = $note
            WHERE op_id = $id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", operationId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
