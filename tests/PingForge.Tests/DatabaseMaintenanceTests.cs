using System.Text;
using PingForge.App.ViewModels;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Data;
using PingForge.Orchestration;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Целостность и резервные копии базы (задача U). База хранит снапшоты и журнал: если файл
/// повредился молча, откат «вернуть как было» уже не сработает, а человек узнает об этом в худший
/// момент.
///
/// Что здесь проверяется:
///   • испорченный файл виден как повреждение, а не как исключение;
///   • испорченный файл НЕ удаляется: он откладывается рядом для разбора;
///   • читаемая копия возвращает снапшоты, нечитаемая — не выдаётся за восстановление;
///   • копий не больше трёх, суточная копия не делается на каждом запуске;
///   • копия снимается до применения твиков, а её сбой не блокирует применение.
/// </summary>
public sealed class DatabaseMaintenanceTests
{
    [Fact]
    public async Task Здоровая_база_проходит_проверку_целостности()
    {
        string path = TempDatabaseFile.Create("pf_db_healthy");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var backups = new DatabaseBackupService(database);

            DatabaseIntegrityReport report = backups.CheckIntegrity();

            Assert.True(report.IsHealthy);
            Assert.Null(report.ProblemRu);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Мусор_в_файле_базы_виден_как_повреждение_а_не_как_исключение()
    {
        string path = TempDatabaseFile.Create("pf_db_corrupt");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            Corrupt(path);

            DatabaseIntegrityReport report = new DatabaseBackupService(database).CheckIntegrity();

            Assert.False(report.IsHealthy);
            Assert.False(string.IsNullOrWhiteSpace(report.ProblemRu));
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Испорченная_база_восстанавливается_из_копии_а_испорченный_файл_остаётся()
    {
        string path = TempDatabaseFile.Create("pf_db_restore");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var backups = new DatabaseBackupService(database);

            string? backupPath = await backups.CreateBackupAsync();
            Assert.NotNull(backupPath);
            int versionsBefore = database.GetSchemaVersions().Count;
            long corruptSize = 4096;

            Corrupt(path);
            Assert.False(backups.CheckIntegrity().IsHealthy);

            DatabaseStartupReport report = await backups.PrepareForStartAsync();

            Assert.Equal(DatabaseStartupOutcome.RestoredFromBackup, report.Outcome);
            Assert.False(report.IsDegraded);
            Assert.NotNull(report.QuarantinedPath);
            Assert.Equal(corruptSize, new FileInfo(report.QuarantinedPath!).Length);
            Assert.True(backups.CheckIntegrity().IsHealthy);
            Assert.Equal(versionsBefore, database.GetSchemaVersions().Count);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Без_копии_повреждённая_база_начинается_с_чистой_а_прежний_файл_не_пропадает()
    {
        string path = TempDatabaseFile.Create("pf_db_clean_start");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            Corrupt(path);

            DatabaseStartupReport report = await new DatabaseBackupService(database).PrepareForStartAsync();

            Assert.Equal(DatabaseStartupOutcome.StartedCleanAfterCorruption, report.Outcome);
            Assert.True(report.IsDegraded);
            Assert.False(File.Exists(path));
            Assert.NotNull(report.QuarantinedPath);
            Assert.Equal(4096L, new FileInfo(report.QuarantinedPath!).Length);
            Assert.Contains("невозможен", report.DescribeRu(), StringComparison.Ordinal);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Копий_хранится_не_больше_трёх()
    {
        string path = TempDatabaseFile.Create("pf_db_keep");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var backups = new DatabaseBackupService(database);

            // Пять «старых» копий заведомо старше сегодняшней: проверяется именно чистка.
            string stem = Path.GetFileName(path) ?? "pf_db";
            for (int day = 1; day <= 5; day++)
            {
                File.WriteAllText(
                    Path.Combine(Path.GetDirectoryName(path)!, $"{stem}.bak-2020010{day}-000000-000"),
                    "старая копия");
            }

            string? fresh = await backups.CreateBackupAsync();
            Assert.NotNull(fresh);

            IReadOnlyList<string> kept = backups.ListBackups();
            Assert.Equal(DatabaseBackupService.KeepBackups, kept.Count);
            Assert.Equal(fresh, kept[0]);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Суточная_копия_делается_раз_в_сутки_а_не_на_каждом_запуске()
    {
        string path = TempDatabaseFile.Create("pf_db_daily");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var backups = new DatabaseBackupService(database);

            string? first = await backups.EnsureDailyBackupAsync();
            Assert.NotNull(first);
            Assert.False(backups.IsDailyBackupDue(DateTime.UtcNow));

            // Второй запуск в тот же день копию не делает: она уже есть.
            Assert.Null(await backups.EnsureDailyBackupAsync());

            // Через сутки с лишним — делает.
            Assert.True(backups.IsDailyBackupDue(DateTime.UtcNow.AddHours(25)));
            Assert.NotNull(await backups.EnsureDailyBackupAsync(DateTime.UtcNow.AddHours(25)));
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Копия_базы_снимается_до_применения_твиков()
    {
        string path = TempDatabaseFile.Create("pf_db_before_apply");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var backups = new DatabaseBackupService(database);
            var inner = new RecordingCoordinator();
            var coordinator = new BackupBeforeApplyCoordinator(inner, backups);

            Assert.Empty(backups.ListBackups());

            await coordinator.ExecuteBatchAsync(
                "Применение выбранных твиков (1)",
                TweakProfilePreset.CustomUserPreset,
                [],
                requestWindowsRestorePoint: false);

            Assert.True(inner.Called);
            Assert.Single(backups.ListBackups());
        }
        finally
        {
            TempDatabaseFile.Delete(path);
            DeleteSideFiles(path);
        }
    }

    [Fact]
    public async Task Без_файла_базы_применение_не_блокируется_и_это_видно_в_журнале()
    {
        string path = TempDatabaseFile.Create("pf_db_missing");
        var notes = new List<string>();
        var inner = new RecordingCoordinator();
        var coordinator = new BackupBeforeApplyCoordinator(
            inner,
            new DatabaseBackupService(new SqliteDatabaseInitializer(path)),
            notes.Add);

        await coordinator.ExecuteBatchAsync(
            "Применение пресета",
            TweakProfilePreset.LowLatencyGaming,
            [],
            requestWindowsRestorePoint: false);

        Assert.True(inner.Called);
        Assert.Contains(notes, note => note.Contains("не из чего", StringComparison.Ordinal));
    }

    [Fact]
    public void Окно_предлагает_восстановить_базу_из_копии()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-SettingsView.xaml");
        Assert.True(File.Exists(path), $"нет копии SettingsView.xaml для проверки: {path}");

        string text = File.ReadAllText(path);

        // Требование задачи U: человек видит состояние базы и может вернуть её из копии сам,
        // а не узнаёт о потере снапшотов при попытке отката.
        Assert.Contains("Восстановить базу из копии", text, StringComparison.Ordinal);
        Assert.Contains("Storage.HealthRu", text, StringComparison.Ordinal);
        Assert.Contains("Storage.RestoreCommand", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Окно_показывает_состояние_базы_и_восстанавливает_её_по_кнопке()
    {
        var fallback = new FakeEngineChannel();
        var channel = new FakeEngineChannel(request => request.Command switch
        {
            EngineCommandKind.StorageInfo => EngineResponse.Success(
                request.Id,
                "База прочитана.",
                """{"dbPath":"C:\\ProgramData\\PingForge\\pingforge.db","dbBytes":2048,"snapshots":2,"journalRows":7,"healthy":false,"integrityProblemRu":"database disk image is malformed","backups":1,"latestBackupName":"pingforge.db.bak-20261116-140516-172","corruptFiles":1}"""),
            EngineCommandKind.DbRestore => EngineResponse.Success(
                request.Id,
                "База восстановлена из копии «pingforge.db.bak-20261116-140516-172». Записи журнала и снапшоты, сделанные после этой копии, пропали — вернуть их нечем.",
                "{}"),
            _ => fallback.DefaultAnswer(request),
        });

        var dialogs = new FakeDialogs();
        var storage = new StorageInfoViewModel(new EngineClient(channel));
        storage.AttachDialogs(dialogs);

        await storage.RefreshCommand.ExecuteAsync();

        Assert.True(storage.CanRestore);
        Assert.Contains("не пройдена", storage.HealthRu, StringComparison.Ordinal);
        Assert.Contains("Резервных копий: 1", storage.BackupsRu, StringComparison.Ordinal);
        Assert.Contains("Отложенных испорченных файлов: 1", storage.CorruptRu, StringComparison.Ordinal);

        await storage.RestoreCommand.ExecuteAsync();

        EngineRequest restore = Assert.Single(channel.Requests, request => request.Command == EngineCommandKind.DbRestore);
        Assert.Equal("yes", restore.Argument("confirm"));
        Assert.Single(dialogs.Confirmations);
        Assert.Contains("пропадут", dialogs.Confirmations[0].Message, StringComparison.Ordinal);
        Assert.Contains("восстановлена из копии", storage.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Служба_проверяет_базу_при_старте_и_делает_копию_перед_применением()
    {
        // Сторож от возврата прежнего состояния: класс был, а вызовов не было ни одного.
        string path = Path.Combine(AppContext.BaseDirectory, "checked-ServiceProgram.cs");
        Assert.True(File.Exists(path), $"нет копии ServiceProgram.cs для проверки: {path}");

        string text = File.ReadAllText(path);

        Assert.Contains("PrepareForStartAsync", text, StringComparison.Ordinal);
        Assert.Contains("BackupBeforeApplyCoordinator", text, StringComparison.Ordinal);
        Assert.Contains("databaseNote", text, StringComparison.Ordinal);
    }

    // ── вспомогательное ─────────────────────────────────────────────────────────────────────────

    /// <summary>Испортить файл базы так, как это делает сбой питания: мусор в заголовке.</summary>
    private static void Corrupt(string databasePath)
    {
        SqliteDatabaseInitializer.ClearFileHandles();
        File.WriteAllBytes(databasePath, Encoding.ASCII.GetBytes(new string('x', 4096)));
    }

    /// <summary>Убрать копии и отложенные файлы этой базы: они лежат рядом, а не в самом файле.</summary>
    private static void DeleteSideFiles(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath) ?? ".";
        string stem = Path.GetFileName(databasePath) ?? "pf_db";

        foreach (string pattern in new[] { stem + ".bak-*", stem + ".corrupt-*" })
        {
            foreach (string file in Directory.GetFiles(directory, pattern))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Временный файл остался — это не повод валить тест.
                }
            }
        }
    }

    /// <summary>Координатор-«заглушка»: запоминает вызов и отдаёт правдоподобный отчёт.</summary>
    private sealed class RecordingCoordinator : ITweakCoordinator
    {
        public bool Called { get; private set; }

        public Task<TweakBatchReport> ExecuteBatchAsync(
            string batchTitle,
            TweakProfilePreset preset,
            IReadOnlyList<ITweak> selectedTweaks,
            bool requestWindowsRestorePoint,
            CancellationToken ct = default)
        {
            Called = true;

            var snapshot = new SystemSnapshot(
                "снапшот-1",
                DateTime.UtcNow,
                batchTitle,
                preset,
                false,
                "hash",
                false,
                null,
                []);

            return Task.FromResult(new TweakBatchReport(
                snapshot,
                RestorePointResultKind.NotRequested,
                "Точка восстановления не запрашивалась.",
                [],
                0,
                0,
                0,
                0,
                RebootRequired: false));
        }

        public Task<int> RecoverInterruptedOperationsAsync(CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
