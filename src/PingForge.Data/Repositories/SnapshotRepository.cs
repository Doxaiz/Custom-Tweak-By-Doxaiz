using Microsoft.Data.Sqlite;
using PingForge.Core.Models;

namespace PingForge.Data.Repositories;

/// <summary>Снапшот вместе со служебными полями, которые не входят в хеш содержимого.</summary>
public sealed record StoredSnapshot(
    SystemSnapshot Snapshot,
    RestorePointResultKind RestorePointStatus,
    SnapshotRollbackStatus? RollbackStatus,
    IReadOnlyDictionary<string, string> PlannedValueReasons);

public sealed class SnapshotRepository
{
    private readonly SqliteDatabaseInitializer _database;

    public SnapshotRepository(SqliteDatabaseInitializer database) => _database = database;

    public async Task SaveAsync(
        SystemSnapshot snapshot,
        RestorePointResultKind restorePointStatus,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tweak_snapshots (
                    snapshot_id, created_utc, snapshot_label, selected_profile, restore_point_status,
                    payload_sha256, rollback_status, was_rolled_back, rolled_back_utc
                ) VALUES ($id, $created, $label, $profile, $restore, $sha, NULL, $rolled, $rolledUtc);
                """;
            command.Parameters.AddWithValue("$id", snapshot.SnapshotId);
            command.Parameters.AddWithValue("$created", snapshot.CreatedUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$label", snapshot.Label);
            command.Parameters.AddWithValue("$profile", snapshot.SelectedProfile.ToString());
            command.Parameters.AddWithValue("$restore", restorePointStatus.ToString());
            command.Parameters.AddWithValue("$sha", snapshot.PayloadSha256);
            command.Parameters.AddWithValue("$rolled", snapshot.WasRolledBack ? 1 : 0);
            command.Parameters.AddWithValue("$rolledUtc", (object?)snapshot.RolledBackUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (SnapshotItemEntry item in snapshot.Entries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tweak_snapshot_items (
                    snapshot_id, tweak_code, mechanism_kind, resource_locator,
                    adapter_interface_guid, adapter_display_name, existed_before,
                    captured_raw_value, value_data_type, planned_value, planned_value_reason
                ) VALUES ($sid, $code, $mechanism, $locator, $guid, $display, $existed, $raw, $type, $planned, $reason);
                """;
            command.Parameters.AddWithValue("$sid", snapshot.SnapshotId);
            command.Parameters.AddWithValue("$code", item.TweakCode);
            command.Parameters.AddWithValue("$mechanism", item.Mechanism.ToString());
            command.Parameters.AddWithValue("$locator", item.ResourceLocator);
            command.Parameters.AddWithValue("$guid", (object?)item.AdapterInterfaceGuid ?? DBNull.Value);
            command.Parameters.AddWithValue("$display", (object?)item.AdapterDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$existed", item.ExistedBefore ? 1 : 0);
            command.Parameters.AddWithValue("$raw", (object?)item.CapturedRawValue ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", item.ValueDataType.ToString());
            command.Parameters.AddWithValue("$planned", item.PlannedNewValue);
            command.Parameters.AddWithValue("$reason", (object?)item.PlannedValueReason ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<StoredSnapshot?> GetByIdAsync(string snapshotId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);

        var (snapshot, restoreStatus, rollbackStatus) = await ReadHeaderAsync(connection, snapshotId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var (entries, reasons) = await ReadItemsAsync(connection, snapshotId, ct).ConfigureAwait(false);

        SystemSnapshot full = snapshot with { Entries = entries };
        return new StoredSnapshot(full, restoreStatus, rollbackStatus, reasons);
    }

    public async Task<IReadOnlyList<StoredSnapshot>> ListAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);

        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT snapshot_id FROM tweak_snapshots ORDER BY created_utc DESC;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var result = new List<StoredSnapshot>(ids.Count);
        foreach (string id in ids)
        {
            StoredSnapshot? stored = await GetByIdAsync(id, ct).ConfigureAwait(false);
            if (stored is not null)
            {
                result.Add(stored);
            }
        }

        return result;
    }

    public async Task MarkRollbackAsync(string snapshotId, SnapshotRollbackStatus status, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tweak_snapshots
            SET was_rolled_back = 1, rollback_status = $status, rolled_back_utc = $now
            WHERE snapshot_id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", snapshotId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAsync(string snapshotId, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tweak_snapshots WHERE snapshot_id = $id; DELETE FROM tweak_snapshot_items WHERE snapshot_id = $id2;";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        cmd.Parameters.AddWithValue("$id2", snapshotId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public string DatabasePathViaInitializer => _database.DatabasePath;

    /// <summary>
    /// Размер файла базы. Если файл не читается — исключение, а не 0: ноль означает пустую базу,
    /// и «не смогли прочитать» не должно выглядеть как «база пуста». Вызывающий решает, что сказать.
    /// </summary>
    public Task<long> GetDatabaseFileSizeAsync(CancellationToken ct = default)
    {
        string? path = _database.DatabasePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.FromResult(0L);
        }

        return Task.FromResult(new FileInfo(path).Length);
    }

    /// <summary>Снапшоты, по которым ещё не был выполнен полный откат — используются при старте службы.</summary>
    public async Task<IReadOnlyList<string>> GetSnapshotIdsWithPendingRollbackAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id FROM tweak_snapshots
            WHERE was_rolled_back = 0 AND rollback_status IS NULL
            ORDER BY created_utc DESC;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var ids = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<(SystemSnapshot? Snapshot, RestorePointResultKind RestoreStatus, SnapshotRollbackStatus? RollbackStatus)>
        ReadHeaderAsync(SqliteConnection connection, string snapshotId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT created_utc, snapshot_label, selected_profile, restore_point_status,
                   payload_sha256, was_rolled_back, rolled_back_utc, rollback_status
            FROM tweak_snapshots WHERE snapshot_id = $id;
            """;
        command.Parameters.AddWithValue("$id", snapshotId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (null, RestorePointResultKind.NotRequested, null);
        }

        DateTime created = EnumCodec.ParseUtc(reader.GetString(0), "created_utc");
        string label = reader.GetString(1);
        TweakProfilePreset profile = EnumCodec.ParseOrThrow<TweakProfilePreset>(reader.GetString(2), "selected_profile");
        RestorePointResultKind restoreStatus = EnumCodec.ParseOrThrow<RestorePointResultKind>(reader.GetString(3), "restore_point_status");
        string hash = reader.GetString(4);
        bool rolledBack = reader.GetInt32(5) == 1;
        DateTime? rolledBackUtc = reader.IsDBNull(6) ? null : EnumCodec.ParseUtc(reader.GetString(6), "rolled_back_utc");
        SnapshotRollbackStatus? rollbackStatus = reader.IsDBNull(7)
            ? null
            : EnumCodec.ParseOrThrow<SnapshotRollbackStatus>(reader.GetString(7), "rollback_status");

        var snapshot = new SystemSnapshot(
            snapshotId, created, label, profile, restoreStatus == RestorePointResultKind.CreatedSuccessfully,
            hash, rolledBack, rolledBackUtc, Array.Empty<SnapshotItemEntry>());

        return (snapshot, restoreStatus, rollbackStatus);
    }

    private static async Task<(List<SnapshotItemEntry> Entries, Dictionary<string, string> Reasons)> ReadItemsAsync(
        SqliteConnection connection, string snapshotId, CancellationToken ct)
    {
        var entries = new List<SnapshotItemEntry>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tweak_code, mechanism_kind, resource_locator, adapter_interface_guid, adapter_display_name,
                   existed_before, captured_raw_value, value_data_type, planned_value, planned_value_reason
            FROM tweak_snapshot_items WHERE snapshot_id = $id ORDER BY item_id ASC;
            """;
        command.Parameters.AddWithValue("$id", snapshotId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string code = reader.GetString(0);
            TweakMechanismKind mechanism = EnumCodec.ParseOrThrow<TweakMechanismKind>(reader.GetString(1), "mechanism_kind");
            string locator = reader.GetString(2);
            string? guid = reader.IsDBNull(3) ? null : reader.GetString(3);
            string? display = reader.IsDBNull(4) ? null : reader.GetString(4);
            bool existed = reader.GetInt32(5) == 1;
            string? raw = reader.IsDBNull(6) ? null : reader.GetString(6);
            TweakValueDataType dataType = EnumCodec.ParseOrThrow<TweakValueDataType>(reader.GetString(7), "value_data_type");
            string planned = reader.GetString(8);
            string? reason = reader.IsDBNull(9) ? null : reader.GetString(9);

            entries.Add(new SnapshotItemEntry(code, mechanism, new TweakTarget(locator, guid, display), existed, raw, dataType, planned)
            {
                PlannedValueReason = reason
            });

            if (!string.IsNullOrWhiteSpace(reason))
            {
                reasons[code] = reason;
            }
        }

        return (entries, reasons);
    }
}
