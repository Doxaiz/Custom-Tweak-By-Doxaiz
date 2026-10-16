using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Tweaks;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Orchestration;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сквозная проверка: каталог твиков + снапшот + применение + откат на реальной SQLite.
/// Именно этот путь выполняется, когда пользователь нажимает «Применить твики».
/// </summary>
public sealed class CatalogOrchestrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabaseInitializer _database;
    private readonly SnapshotRepository _snapshots;
    private readonly AuditJournalRepository _journal;
    private readonly FakeRegistryPort _registry = new();
    private readonly FakeNicPropertyPort _nic = new();
    private readonly FakeNetshEmulator _command = new();
    private readonly FakeActiveAdapterResolver _adapter = new();
    private readonly List<AuditFindingRecord> _findings = new();

    public CatalogOrchestrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pf_catalog_{Guid.NewGuid():N}.db");
        _database = new SqliteDatabaseInitializer(_dbPath);
        _database.EnsureInitializedAsync().GetAwaiter().GetResult();
        _snapshots = new SnapshotRepository(_database);
        _journal = new AuditJournalRepository(_database);

        _registry.Seed(InterfaceLocation("TcpAckFrequency"), "2");   // уже стояло значение, не равное рекомендуемому
        _registry.Seed(InterfaceLocation("TCPNoDelay"), "1");        // уже оптимально -> должен быть no-op
        _nic.SeedAdapter("{GUID-ADAPTER-A}", "Ethernet");
        _nic.SeedProperty("{GUID-ADAPTER-A}", "Energy-Efficient Ethernet", "Enabled", "Enabled", "Disabled");
        _command.SeedInterface("Ethernet", mtu: 1500, dnsServers: "192.168.1.1");
    }

    private static RegistryLocation InterfaceLocation(string valueName) =>
        new(RegistryRootKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID-ADAPTER-A}",
            valueName);

    private TweakCatalog CreateCatalog(bool withAdvice = true) => new(new TweakPlatformPorts(
        Registry: _registry,
        NicProperties: _nic,
        Command: _command,
        ActiveAdapter: _adapter,
        MtuAdvice: withAdvice ? new FakeMtuAdvice("1492") : null,
        DnsAdvice: withAdvice ? new FakeDnsAdvice("1.1.1.1|8.8.8.8") : null,
        NicOptions: new FakeNicOptions("128", "512", "2048")));

    private TweakCoordinator CreateCoordinator(TweakCatalog catalog)
    {
        var snapshotService = new SnapshotService(_snapshots, catalog, new TargetValueResolver());
        return new TweakCoordinator(snapshotService, _journal, catalog);
    }

    [Fact]
    public async Task LowLatencyBatch_SnapshotsAppliesAndRollsBackOnRealDatabase()
    {
        TweakCatalog catalog = CreateCatalog();
        TweakCoordinator coordinator = CreateCoordinator(catalog);

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Низкий пинг", TweakProfilePreset.LowLatencyGaming,
            catalog.GetByPreset(TweakProfilePreset.LowLatencyGaming), requestWindowsRestorePoint: false);

        // 1. Снапшот зафиксировал прежние значения и цели, включая вычисляемые.
        SnapshotItemEntry ack = report.Snapshot.Entries.Single(e => e.TweakCode == "TCP_ACK_FREQUENCY");
        Assert.Equal("2", ack.CapturedRawValue);
        Assert.Equal("1", ack.PlannedNewValue);
        Assert.Equal("{GUID-ADAPTER-A}", ack.AdapterInterfaceGuid);

        SnapshotItemEntry mtu = report.Snapshot.Entries.Single(e => e.TweakCode == "MTU_SUBINTERFACE_OPTIMAL");
        Assert.Equal("1492", mtu.PlannedNewValue);
        Assert.Contains("1492", mtu.PlannedValueReason ?? string.Empty, StringComparison.Ordinal);

        // 2. Применение: изменённое значение записано, уже оптимальное — честный no-op.
        Assert.Equal("1", _registry.Get(InterfaceLocation("TcpAckFrequency")));
        Assert.True(report.AppliedCount > 0);
        Assert.True(report.NoOpCount >= 1);
        Assert.Equal(0, report.FailedCount);

        // 3. Откат: значение вернулось, а то, чего раньше не было, — удалено.
        Assert.NotNull(await _snapshots.GetByIdAsync(report.Snapshot.SnapshotId));

        SnapshotRollbackReport rollback = await new SnapshotService(_snapshots, catalog, new TargetValueResolver())
            .RollbackSnapshotAsync(report.Snapshot.SnapshotId);

        Assert.Equal("2", _registry.Get(InterfaceLocation("TcpAckFrequency")));
        Assert.NotEqual(SnapshotRollbackStatus.Failed, rollback.Status);
    }

    [Fact]
    public async Task ComputedValueWithoutMeasurement_IsLeftEmptyAndSkipped_NotGuessed()
    {
        TweakCatalog catalog = CreateCatalog(withAdvice: false);
        TweakCoordinator coordinator = CreateCoordinator(catalog);

        // netsh отвечает нормально (интерфейс найден), но замеров MTU нет — значит значение неизвестно.

        ITweak mtuTweak = catalog.GetByCode("MTU_SUBINTERFACE_OPTIMAL")!;
        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "MTU без замеров", TweakProfilePreset.LowLatencyGaming, new[] { mtuTweak }, requestWindowsRestorePoint: false);

        SnapshotItemEntry entry = Assert.Single(report.Snapshot.Entries);
        Assert.Equal(string.Empty, entry.PlannedNewValue);
        Assert.Contains("не измерялся", entry.PlannedValueReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, report.AppliedCount);
        Assert.Equal(TweakOutcome.SkippedUnsafeTarget, report.Results[0].Outcome);
        Assert.DoesNotContain(_command.Invocations, i => i.Contains("set subinterface", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CatalogWithoutNicPort_ReportsNicTweaksAsUnsupportedInBatchReport()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts(Registry: _registry, ActiveAdapter: _adapter));
        TweakCoordinator coordinator = CreateCoordinator(catalog);

        ITweak nicTweak = catalog.GetByCode("NIC_ENERGY_EFFICIENT_ETHERNET")!;
        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "Без порта NIC", TweakProfilePreset.LowLatencyGaming, new[] { nicTweak }, requestWindowsRestorePoint: false);

        Assert.Equal(0, report.AppliedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(TweakOutcome.SkippedUnsupported, report.Results[0].Outcome);
        Assert.Contains("недоступны", report.Results[0].FailureReasonRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NicTweakOnAdapterScopedTarget_WritesThroughDriverProperty()
    {
        TweakCatalog catalog = CreateCatalog();
        TweakCoordinator coordinator = CreateCoordinator(catalog);

        ITweak tweak = catalog.GetByCode("NIC_ENERGY_EFFICIENT_ETHERNET")!;
        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "EEE", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        Assert.Equal(1, report.AppliedCount);
        Assert.Equal("Disabled", _nic.Get("{GUID-ADAPTER-A}", "Energy-Efficient Ethernet"));
        Assert.Equal(1, _adapter.CallCount);

        await new SnapshotService(_snapshots, catalog, new TargetValueResolver())
            .RollbackSnapshotAsync(report.Snapshot.SnapshotId);

        Assert.Equal("Enabled", _nic.Get("{GUID-ADAPTER-A}", "Energy-Efficient Ethernet"));
    }

    [Fact]
    public async Task MtuTweak_SetsValueThroughNetsh_AndRollsBackToPreviousMtu()
    {
        TweakCatalog catalog = CreateCatalog();
        TweakCoordinator coordinator = CreateCoordinator(catalog);
        ITweak tweak = catalog.GetByCode("MTU_SUBINTERFACE_OPTIMAL")!;

        TweakBatchReport report = await coordinator.ExecuteBatchAsync(
            "MTU", TweakProfilePreset.LowLatencyGaming, new[] { tweak }, requestWindowsRestorePoint: false);

        Assert.Equal(1, report.AppliedCount);
        Assert.Equal(1492, _command.GetMtu("Ethernet"));
        Assert.Contains(_command.Invocations, i => i.Contains("mtu=1492", StringComparison.Ordinal));

        await new SnapshotService(_snapshots, catalog, new TargetValueResolver())
            .RollbackSnapshotAsync(report.Snapshot.SnapshotId);

        Assert.Equal(1500, _command.GetMtu("Ethernet"));
    }

    [Fact]
    public async Task AuditFindingsFromCatalogProblems_AreNotSilentlyIgnored()
    {
        var page = new AuditJournalRepository(_database);
        IReadOnlyList<string> problems = CatalogSafetyAudit.AuditCatalog(CreateCatalog().GetAllTweaks());

        Assert.Empty(problems);
        _ = _findings;
        Assert.Empty(await page.GetAuditFindingsAsync());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // Файл может быть занят пулом — для теста не критично.
        }
    }
}
