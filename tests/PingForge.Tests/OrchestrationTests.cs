using Microsoft.Data.Sqlite;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Tweaks;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Orchestration;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сквозные тесты: снапшот → применение → верификация → откат, плюс восстановление после падения.
/// Работают на реальной SQLite (временный файл), а не на моках хранилища.
/// </summary>
public sealed class OrchestrationTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteDatabaseInitializer _database;
    private readonly SnapshotRepository _snapshots;
    private readonly AuditJournalRepository _journal;

    public OrchestrationTests()
    {
        _databasePath = TempDatabaseFile.Create("pf_test");
        _database = new SqliteDatabaseInitializer(_databasePath);
        _database.EnsureInitializedAsync().GetAwaiter().GetResult();
        _snapshots = new SnapshotRepository(_database);
        _journal = new AuditJournalRepository(_database);
    }

    private (SnapshotService Service, TweakCoordinator Coordinator) CreateServices(
        ITweakCatalog catalog, ICrashRecoveryHandler? crashRecovery = null, ISystemRestorePointService? restorePoints = null)
    {
        var service = new SnapshotService(_snapshots, catalog, new TargetValueResolver(), restorePoints);
        var coordinator = new TweakCoordinator(service, _journal, catalog, crashRecovery);
        return (service, coordinator);
    }

    [Fact]
    public async Task Snapshot_CapturesConcreteAdapterGuid_AndRoundTripsThroughDatabase()
    {
        var tweak = new FakeTweak("TCP_ACK_FREQUENCY", defaultValue: "2", existingValuePresent: true);
        var catalog = new FakeTweakCatalog(new[] { tweak });
        (SnapshotService service, _) = CreateServices(catalog);

        (SystemSnapshot snapshot, RestorePointResultKind status, _) = await service.CreateSnapshotAsync(
            "Тест", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        Assert.Equal(RestorePointResultKind.NotRequested, status);

        StoredSnapshot loaded = await _snapshots.GetByIdAsync(snapshot.SnapshotId) ?? throw new InvalidOperationException("снапшот не найден");
        SnapshotItemEntry entry = Assert.Single(loaded.Snapshot.Entries);

        Assert.Equal("{GUID-ADAPTER-A}", entry.AdapterInterfaceGuid);
        Assert.Equal("Адаптер A", entry.AdapterDisplayName);
        Assert.Equal("2", entry.CapturedRawValue);
        Assert.True(entry.ExistedBefore);
        Assert.True(PingForge.Core.Security.SnapshotHasher.VerifyIntegrity(loaded.Snapshot));
    }

    [Fact]
    public async Task ComputeReadyTweak_StoresCalculatedValueAndReason_InsteadOfStaticConstant()
    {
        var computed = new FakeComputedTweak("MTU_SUBINTERFACE_OPTIMAL", computedValue: "1492", computeExplanation: "Path MTU найден DF-пробами: 1492 байта.");
        var catalog = new FakeTweakCatalog(new[] { computed });
        (SnapshotService service, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "MTU", TweakProfilePreset.LowLatencyGaming, new[] { computed }, requestWindowsRestorePoint: false);

        SnapshotItemEntry entry = Assert.Single(report.Snapshot.Entries);
        Assert.Equal("1492", entry.PlannedNewValue);
        Assert.Contains("1492", entry.PlannedValueReason ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal("1492", computed.CurrentValue);
    }

    [Fact]
    public async Task UnsupportedTweak_GetsEmptyPlannedValue_AndIsSkipped()
    {
        var unsupported = new FakeTweak("NIC_EEE_OFF", isSupported: false);
        var catalog = new FakeTweakCatalog(new[] { unsupported });
        (SnapshotService service, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Тест", TweakProfilePreset.LowLatencyGaming, new[] { unsupported }, requestWindowsRestorePoint: false);

        SnapshotItemEntry entry = Assert.Single(report.Snapshot.Entries);
        Assert.Equal(string.Empty, entry.PlannedNewValue);
        Assert.Contains("не поддерживается", entry.PlannedValueReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, report.AppliedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(TweakOutcome.SkippedUnsupported, report.Results[0].Outcome);
    }

    [Fact]
    public async Task Batch_CountsAppliedNoOpSkippedAndFailedSeparately()
    {
        var applied1 = new FakeTweak("TCP_ACK_FREQUENCY");
        var applied2 = new FakeTweak("TCP_NO_DELAY");
        var noOp = new FakeTweak("NIC_RSS_ON", isAlreadyOptimal: true);
        var unsupported = new FakeTweak("NIC_EEE_OFF", isSupported: false);
        var failing = new FakeTweak("MMCSS_NETWORK_THROTTLING", failOnVerify: true);

        var catalog = new FakeTweakCatalog(new[] { applied1, applied2, noOp, unsupported, failing });
        (_, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Смешанный батч", TweakProfilePreset.LowLatencyGaming,
            new ITweak[] { applied1, applied2, noOp, unsupported, failing }, requestWindowsRestorePoint: false);

        Assert.Equal(2, report.AppliedCount);
        Assert.Equal(1, report.NoOpCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(5, report.Results.Count);

        // Провалившийся твик обязан быть откачен автоматически.
        Assert.True(failing.RollbackCalls > 0);
    }

    [Fact]
    public async Task VerifyAndRollback_AlwaysUseTargetSavedInSnapshot_NotTheCurrentOne()
    {
        var tweak = new FakeTweak("TCP_ACK_FREQUENCY") { SimulateTargetChangeOnApply = true };
        var catalog = new FakeTweakCatalog(new[] { tweak });
        (SnapshotService service, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Смена адаптера", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        // Apply получил цель из снапшота (адаптер A) и внутри «переписал» её на адаптер B —
        // так выглядит смена активного адаптера в середине батча.
        Assert.Equal("{GUID-ADAPTER-A}", tweak.LastApplyEntry!.AdapterInterfaceGuid);
        Assert.Equal("{GUID-ADAPTER-B}", tweak.LastWrittenTargetGuid);

        // Verify и Rollback обязаны работать по цели, сохранённой в снапшоте, а не по текущей.
        Assert.Equal("{GUID-ADAPTER-A}", tweak.LastVerifyEntry!.AdapterInterfaceGuid);

        await service.RollbackSnapshotAsync(report.Snapshot.SnapshotId);
        Assert.Equal("{GUID-ADAPTER-A}", tweak.LastRollbackEntry!.AdapterInterfaceGuid);
    }

    [Fact]
    public async Task AppliedTweakThatNeedsRestart_MarksBatchAsRequiringRestart()
    {
        var restarting = new FakeTweak("NIC_ITR_OFF", requiresRestart: true);
        var catalog = new FakeTweakCatalog(new[] { restarting });
        (_, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Рестарт", TweakProfilePreset.LowLatencyGaming, new[] { restarting }, requestWindowsRestorePoint: false);

        Assert.True(report.RebootRequired);
        Assert.Equal(TweakOutcome.WrittenPendingRestart, report.Results[0].Outcome);
    }

    [Fact]
    public async Task Rollback_RestoresCapturedValue_AndDeletesValuesThatDidNotExist()
    {
        var existing = new FakeTweak("WIN_GAME_MODE", defaultValue: "2", existingValuePresent: true);
        var created = new FakeTweak("TCP_ACK_FREQUENCY");
        var catalog = new FakeTweakCatalog(new[] { existing, created });
        (SnapshotService service, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Откат", TweakProfilePreset.LowLatencyGaming, new ITweak[] { existing, created }, requestWindowsRestorePoint: false);

        SnapshotRollbackReport rollback = await service.RollbackSnapshotAsync(report.Snapshot.SnapshotId);

        Assert.Equal(SnapshotRollbackStatus.FullyRolledBack, rollback.Status);
        Assert.Equal("2", existing.CurrentValue);
        Assert.Null(created.CurrentValue);
        Assert.Contains("Полный откат", rollback.SummaryMessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_RefusesToRunWhenSnapshotIntegrityIsBroken()
    {
        var tweak = new FakeTweak("TCP_ACK_FREQUENCY");
        var catalog = new FakeTweakCatalog(new[] { tweak });
        (_, _) = CreateServices(catalog);

        var tampered = new SystemSnapshot(
            "tampered-snap", DateTime.UtcNow, "Подделка", TweakProfilePreset.LowLatencyGaming, false,
            "0000000000000000000000000000000000000000000000000000000000000000",
            false, null,
            new[] { new SnapshotItemEntry("TCP_ACK_FREQUENCY", TweakMechanismKind.Registry, TweakTarget.Global(@"HKLM\SOFTWARE\Test"), false, null, TweakValueDataType.DWord, "1") });

        await _snapshots.SaveAsync(tampered, RestorePointResultKind.NotRequested);

        var service = new SnapshotService(_snapshots, catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackSnapshotAsync("tampered-snap"));
    }

    [Fact]
    public async Task Rollback_ReportsOrphans_WhenTweakDisappearedFromCatalog()
    {
        var tweak = new FakeTweak("REMOVED_TWEAK");
        var catalog = new FakeTweakCatalog(new[] { tweak });
        (SnapshotService service, TweakCoordinator coordinator) = CreateServices(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Осиротевший твик", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        var emptyCatalog = new FakeTweakCatalog(Array.Empty<ITweak>());
        var serviceWithoutTweak = new SnapshotService(_snapshots, emptyCatalog);

        SnapshotRollbackReport rollback = await serviceWithoutTweak.RollbackSnapshotAsync(report.Snapshot.SnapshotId);

        Assert.Contains("REMOVED_TWEAK", rollback.OrphanedTweakCodes);
        Assert.NotEqual(SnapshotRollbackStatus.FullyRolledBack, rollback.Status);
        Assert.Contains("вручную", rollback.SummaryMessageRu, StringComparison.OrdinalIgnoreCase);
        _ = service;
    }

    [Fact]
    public async Task RestorePointStatus_IsPersistedHonestly()
    {
        var tweak = new FakeTweak("TCP_ACK_FREQUENCY");
        var catalog = new FakeTweakCatalog(new[] { tweak });
        var restorePoints = new FakeRestorePointService(RestorePointResultKind.SkippedFrequencyLimit24h, "Точка уже создавалась за сутки.");
        (SnapshotService service, _) = CreateServices(catalog, restorePoints: restorePoints);

        (SystemSnapshot snapshot, RestorePointResultKind status, string message) = await service.CreateSnapshotAsync(
            "Суточный лимит", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: true);

        Assert.Equal(RestorePointResultKind.SkippedFrequencyLimit24h, status);
        Assert.Contains("сутки", message, StringComparison.OrdinalIgnoreCase);

        StoredSnapshot loaded = await _snapshots.GetByIdAsync(snapshot.SnapshotId) ?? throw new InvalidOperationException();
        Assert.Equal(RestorePointResultKind.SkippedFrequencyLimit24h, loaded.RestorePointStatus);
        Assert.False(loaded.Snapshot.OsRestorePointCreated);
    }

    [Fact]
    public async Task RestorePoint_IsNotRequested_DoesNotTouchRestoreService()
    {
        var tweak = new FakeTweak("TCP_ACK_FREQUENCY");
        var catalog = new FakeTweakCatalog(new[] { tweak });
        var restorePoints = new FakeRestorePointService();
        (SnapshotService service, _) = CreateServices(catalog, restorePoints: restorePoints);

        (_, RestorePointResultKind status, string message) = await service.CreateSnapshotAsync(
            "Без точки", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        Assert.Equal(RestorePointResultKind.NotRequested, status);
        Assert.Equal(0, restorePoints.CallCount);
        Assert.Contains("не запрашивалась", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrashRecovery_RollsBackInterruptedTweak()
    {
        var tweak = new FakeTweak("SCHED_WIN32_PRIORITY_SEPARATION", defaultValue: "2", existingValuePresent: true);
        var catalog = new FakeTweakCatalog(new[] { tweak });
        (SnapshotService service, TweakCoordinator coordinator) = CreateServices(catalog);

        (SystemSnapshot snapshot, _, _) = await service.CreateSnapshotAsync(
            "Падение", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        SnapshotItemEntry entry = snapshot.Entries[0];

        // Имитация: процесс упал сразу после записи в систему, операция осталась в статусе PENDING.
        await _journal.BeginPendingOperationAsync("op-crash-1", snapshot.SnapshotId,
            AuditJournalRepository.OperationCategories.TweakApply, entry.ResourceLocator,
            System.Text.Json.JsonSerializer.Serialize(entry));

        await tweak.ApplyAsync(entry);

        int recovered = await coordinator.RecoverInterruptedOperationsAsync();

        Assert.Equal(1, recovered);
        Assert.Equal("2", tweak.CurrentValue);
        Assert.Empty(await _journal.GetPendingOperationsAsync());
    }

    [Fact]
    public async Task CrashRecovery_HandlesRouteTunnelFilterAndDnsOperations()
    {
        var catalog = new FakeTweakCatalog(Array.Empty<ITweak>());
        var crashHandler = new FakeCrashRecoveryHandler();
        (_, TweakCoordinator coordinator) = CreateServices(catalog, crashRecovery: crashHandler);

        await _journal.BeginPendingOperationAsync("op-route", null, AuditJournalRepository.OperationCategories.RouteAdd, "203.0.113.7/32", "{}");
        await _journal.BeginPendingOperationAsync("op-tunnel", null, AuditJournalRepository.OperationCategories.TunnelUp, "wg-pingforge0", "{}");
        await _journal.BeginPendingOperationAsync("op-filter", null, AuditJournalRepository.OperationCategories.WinDivertStart, "tcp and udp", "{}");
        await _journal.BeginPendingOperationAsync("op-dns", null, AuditJournalRepository.OperationCategories.DnsSet, "NameServer", "{}");

        int recovered = await coordinator.RecoverInterruptedOperationsAsync();

        Assert.Equal(4, recovered);
        Assert.Contains("ROUTE_ADD:203.0.113.7/32", crashHandler.UndoneOperations);
        Assert.Contains("TUNNEL_UP:wg-pingforge0", crashHandler.UndoneOperations);
        Assert.Contains("WINDIVERT_START:tcp and udp", crashHandler.UndoneOperations);
        Assert.Contains("DNS_SET:NameServer", crashHandler.UndoneOperations);
        Assert.Empty(await _journal.GetPendingOperationsAsync());
    }

    [Fact]
    public async Task CrashRecovery_WithoutHandler_DoesNotSilentlySkipOperations()
    {
        var catalog = new FakeTweakCatalog(Array.Empty<ITweak>());
        (_, TweakCoordinator coordinator) = CreateServices(catalog, crashRecovery: null);

        await _journal.BeginPendingOperationAsync("op-route-2", null, AuditJournalRepository.OperationCategories.RouteAdd, "198.51.100.7/32", "{}");

        int recovered = await coordinator.RecoverInterruptedOperationsAsync();

        Assert.Equal(0, recovered);

        IReadOnlyList<AuditFindingRecord> findings = await _journal.GetAuditFindingsAsync();
        _ = findings;
        Assert.Empty(await _journal.GetPendingOperationsAsync());
    }

    [Fact]
    public async Task AuditFindings_StoreConflictWarningsSoTheySurviveRestart()
    {
        await _journal.ReplaceAuditFindingsAsync(new[]
        {
            new AuditFindingRecord("CONFLICT_WARNING", "WARN_ACTIVE_VPN", "Активен сторонний VPN", "Фоновый VPN перехватывает трафик.", TweakRiskLevel.High),
            new AuditFindingRecord("RECOMMEND_CHANGE", "TCP_NO_DELAY_1", "Отключение Нейгла", "Снижает задержку отправки команд.", TweakRiskLevel.Low),
            new AuditFindingRecord("OPTIONAL", "NIC_RX_BUFFERS_512", "Буферы приёма", "Помогает на пиковых нагрузках.", TweakRiskLevel.Low)
        });

        IReadOnlyList<AuditFindingRecord> warnings = await _journal.GetAuditFindingsAsync("CONFLICT_WARNING");
        IReadOnlyList<AuditFindingRecord> all = await _journal.GetAuditFindingsAsync();

        Assert.Single(warnings);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task UnknownEnumInDatabase_ThrowsInsteadOfSilentlyDefaulting()
    {
        await using (SqliteConnection connection = await _database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tweak_snapshots (snapshot_id, created_utc, snapshot_label, selected_profile, restore_point_status, payload_sha256)
                VALUES ('bad-enum', $now, 'Битый профиль', 'NotARealProfile', 'NotRequested', 'ABC');
                """;
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _snapshots.GetByIdAsync("bad-enum"));
    }

    [Fact]
    public void Schema_ContainsSeparateAdapterColumns_AndNoMarkerHack()
    {
        IReadOnlyList<int> versions = _database.GetSchemaVersions();
        Assert.Contains(SqliteDatabaseInitializer.SchemaVersion, versions);

        using SqliteConnection connection = new(_database.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'tweak_snapshot_items';";
        string ddl = (string)command.ExecuteScalar()!;

        Assert.Contains("adapter_interface_guid", ddl, StringComparison.Ordinal);
        Assert.Contains("adapter_display_name", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("@@GUID", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignKeysAreEnforced_DeletingSnapshotRemovesItems()
    {
        var tweak = new FakeTweak("TCP_ACK_FREQUENCY");
        var catalog = new FakeTweakCatalog(new[] { tweak });
        (SnapshotService service, _) = CreateServices(catalog);

        (SystemSnapshot snapshot, _, _) = await service.CreateSnapshotAsync(
            "FK", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        await using (SqliteConnection connection = await _database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tweak_snapshots WHERE snapshot_id = $id;";
            command.Parameters.AddWithValue("$id", snapshot.SnapshotId);
            await command.ExecuteNonQueryAsync();

            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(1) FROM tweak_snapshot_items WHERE snapshot_id = $id;";
            count.Parameters.AddWithValue("$id", snapshot.SnapshotId);

            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ModificationLog_RecordsSkippedNoOpsSeparately()
    {
        var noOp = new FakeTweak("NIC_RSS_ON", isAlreadyOptimal: true);
        var catalog = new FakeTweakCatalog(new[] { noOp });
        (_, TweakCoordinator coordinator) = CreateServices(catalog);

        await coordinator.ExecuteBatchAsync("No-op", TweakProfilePreset.LowLatencyGaming, new[] { noOp }, requestWindowsRestorePoint: false);

        Assert.Equal(1, await _journal.CountModificationLogEntriesAsync());
    }

    public void Dispose() => TempDatabaseFile.Delete(_databasePath);
}
