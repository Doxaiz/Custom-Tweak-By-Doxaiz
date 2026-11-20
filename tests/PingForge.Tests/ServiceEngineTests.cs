using System.Text.Json;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// «Мозги» службы: команды, которые реально меняют систему. Проверяются на двойниках портов и на настоящей
/// SQLite — так видно и разбор запросов, и честность отчётов («применено 2 из 3» вместо «готово»).
/// </summary>
public sealed class ServiceEngineTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteDatabaseInitializer _database;

    public ServiceEngineTests()
    {
        _databasePath = TempDatabaseFile.Create("pf_service");
        _database = new SqliteDatabaseInitializer(_databasePath);
        _database.EnsureInitializedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        TempDatabaseFile.Delete(_databasePath);

        // Копии и отложенные файлы базы (задача U) лежат рядом с файлом базы — убираем и их,
        // иначе временный каталог копится копиями от каждого теста.
        string directory = Path.GetDirectoryName(_databasePath) ?? ".";
        string stem = Path.GetFileName(_databasePath) ?? "pf_service";
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
                    // Не удалось убрать временный файл — это не повод валить тест.
                }
            }
        }
    }

    private static FakeTweak Tweak(string code) => new(code);

    // ── связь и описание ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_называет_изделие_версию_и_число_твиков_в_каталоге()
    {
        LiveEngineOperations ops = Build(catalog: new CatalogDouble(Tweak("A"), Tweak("B"), Tweak("C")));

        EngineOperationResult result = await ops.PingAsync();

        Assert.True(result.Ok);
        Assert.Contains("Custom Tweak By Doxaiz", result.MessageRu);
        Assert.Contains("3", result.MessageRu);
        Assert.Contains("права администратора есть", result.MessageRu);
        Assert.Contains("\"tweakCount\":3", result.DataJson);
        Assert.Contains($"\"protocolVersion\":{EngineProtocol.Version}", result.DataJson);
    }

    [Fact]
    public async Task Ping_с_личностью_клиента_называет_её_а_не_службу()
    {
        // C7: настоящая операция ping обязана отдать clientName/clientSid/clientIsAdmin
        // именно подключившегося клиента. Служба сама — SYSTEM/S-1-5-18; если в ответе они,
        // инспектор снова смотрит на токен службы, и живая проверка на раннере это поймает.
        LiveEngineOperations ops = Build();
        var peer = new EnginePeerIdentity("RUNNER\\runneradmin", "S-1-5-21-100-200-300-1001", ClientIsAdmin: true, ClientProcessId: 4242);

        EngineOperationResult result = await ops.PingAsync(peer);

        Assert.True(result.Ok);
        Assert.Contains("\"clientName\":\"RUNNER\\\\runneradmin\"", result.DataJson);
        Assert.Contains("\"clientSid\":\"S-1-5-21-100-200-300-1001\"", result.DataJson);
        Assert.Contains("\"clientIsAdmin\":true", result.DataJson);
        Assert.DoesNotContain("S-1-5-18", result.DataJson);
    }

    [Fact]
    public async Task Ping_без_личности_пишет_что_клиент_не_определён()
    {
        LiveEngineOperations ops = Build();

        EngineOperationResult result = await ops.PingAsync();

        Assert.True(result.Ok);
        Assert.Contains("\"clientSid\":\"не определён\"", result.DataJson);
        Assert.Contains("\"clientIsAdmin\":false", result.DataJson);
    }

    [Fact]
    public async Task Ping_без_прав_честно_говорит_что_менять_нельзя()
    {
        LiveEngineOperations ops = Build(isElevationAvailable: false);

        EngineOperationResult result = await ops.PingAsync();

        Assert.Contains("прав администратора нет", result.MessageRu);
        Assert.Contains("\"isElevationAvailable\":false", result.DataJson);
    }

    [Fact]
    public async Task Capabilities_отдаёт_список_команд_в_данных()
    {
        LiveEngineOperations ops = Build();

        EngineOperationResult result = await ops.CapabilitiesAsync();

        Assert.True(result.Ok);
        foreach (EngineCommandDescriptor item in EngineCommandCatalog.All)
        {
            Assert.Contains(item.WireName, result.DataJson);
        }
    }

    [Fact]
    public async Task Статус_без_подключённого_движка_говорит_об_этом_прямо()
    {
        LiveEngineOperations ops = Build();

        EngineOperationResult result = await ops.StatusAsync();

        Assert.True(result.Ok);
        Assert.Contains("не подключён", result.MessageRu);
        Assert.Contains("\"realtimeState\":\"idle\"", result.DataJson);
    }

    [Fact]
    public async Task Статус_считает_незакрытые_операции_из_журнала()
    {
        var journal = new AuditJournalRepository(_database);
        await journal.BeginPendingOperationAsync("op-1", null, "ROUTE_ADD", "3.4.5.6", "{}");

        LiveEngineOperations ops = Build(journal: journal);

        EngineOperationResult result = await ops.StatusAsync();

        Assert.Contains("Незакрытых операций в журнале: 1", result.MessageRu);
        Assert.Contains("\"pendingOperations\":1", result.DataJson);
    }

    // ── Задача F: график сети питается ответами команды status ─────────────────────────────────

    [Fact]
    public async Task Статус_во_время_оптимизации_отдаёт_точки_графика_а_провалы_без_нулей()
    {
        var optimizer = new RealtimeOptimizer(
            new SingleGameWatcher(new GameFlowInfo(
                ProcessId: 4242,
                ProcessName: "fortniteclient-win64-shipping.exe",
                GameTitleRu: "Fortnite",
                RemoteEndpoint: "3.4.5.6",
                RemotePort: 3074,
                Transport: "UDP",
                AntiCheat: AntiCheatStatus.VerifiedSafe)),
            new QueuedMeasurements(
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                new PathMeasurement(true, 80, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                PathMeasurement.Unmeasurable("поток игры пропал")),
            new NoopActionRunner());

        RealtimeSnapshot started = await optimizer.StartAsync();
        Assert.Equal(RealtimeOptimizationState.ActiveMonitoring, started.State);

        await optimizer.TickOnceAsync(); // окно не измерилось — на графике это разрыв

        LiveEngineOperations ops = Build(realtime: optimizer);

        EngineOperationResult result = await ops.StatusAsync();

        // Точки графика доходят до окна в одном ответе со статусом: график ничего не зондирует сам.
        Assert.Contains("\"chartSamples\":[", result.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"measured\":true", result.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"rttMedianMs\":80", result.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"measured\":false", result.DataJson, StringComparison.Ordinal);

        // Честность: точка без измерения не несёт чисел — null не превращается в ноль.
        Assert.DoesNotContain("\"rttMedianMs\":0,", result.DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rttMedianMs\":null", result.DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"jitterMadMs\":0,", result.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Статус_отдаёт_окну_выигрыш_разброс_потери_и_предел_пути()
    {
        // Раньше окно получало только «до» и «сейчас»: выигрыш, разброс, потери и предел пути
        // оставались внутри снапшота службы, и окно честно писало «заметного выигрыша нет»
        // даже тогда, когда выигрыш был измерен.
        var optimizer = new RealtimeOptimizer(
            new SingleGameWatcher(new GameFlowInfo(
                ProcessId: 4242,
                ProcessName: "fortniteclient-win64-shipping.exe",
                GameTitleRu: "Fortnite",
                RemoteEndpoint: "3.4.5.6",
                RemotePort: 3074,
                Transport: "UDP",
                AntiCheat: AntiCheatStatus.VerifiedSafe)),
            new QueuedMeasurements(
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (пассивные UDP-метки)."),
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (пассивные UDP-метки)."),
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (пассивные UDP-метки)."),
                new PathMeasurement(true, 80, 2, 0, 12, "Замер живого потока игры (пассивные UDP-метки)."),
                new PathMeasurement(true, 80, 2, 0, 12, "Замер живого потока игры (пассивные UDP-метки).")),
            new NoopActionRunner());

        await optimizer.StartAsync();
        await optimizer.TickOnceAsync();

        LiveEngineOperations ops = Build(realtime: optimizer);
        EngineOperationResult result = await ops.StatusAsync();

        Assert.False(string.IsNullOrEmpty(result.DataJson));
        using JsonDocument document = JsonDocument.Parse(result.DataJson!);
        JsonElement root = document.RootElement;

        Assert.Equal(40.0, root.GetProperty("improvementMs").GetDouble(), 3);
        Assert.Equal(80.0, root.GetProperty("floorRttMs").GetDouble(), 3);
        Assert.Equal(2.0, root.GetProperty("currentJitterMs").GetDouble(), 3);
        Assert.Equal(0.0, root.GetProperty("currentLossPercent").GetDouble(), 3);
    }

    [Fact]
    public async Task Неизмеренные_разброс_и_потери_не_уходят_окну_вовсе_а_не_нулём()
    {
        var optimizer = new RealtimeOptimizer(
            new SingleGameWatcher(new GameFlowInfo(
                ProcessId: 4242,
                ProcessName: "fortniteclient-win64-shipping.exe",
                GameTitleRu: "Fortnite",
                RemoteEndpoint: "3.4.5.6",
                RemotePort: 3074,
                Transport: "UDP",
                AntiCheat: AntiCheatStatus.VerifiedSafe)),
            new QueuedMeasurements(
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                new PathMeasurement(true, 120, 2, 0, 12, "Замер живого потока игры (статистика TCP-соединения)."),
                new PathMeasurement(true, 80, null, null, 12, "Замер живого потока игры (статистика TCP-соединения).")),
            new NoopActionRunner());

        await optimizer.StartAsync();

        LiveEngineOperations ops = Build(realtime: optimizer);
        EngineOperationResult result = await ops.StatusAsync();

        Assert.False(string.IsNullOrEmpty(result.DataJson));
        using JsonDocument document = JsonDocument.Parse(result.DataJson!);
        JsonElement root = document.RootElement;

        // Поле, которого нет, окно читает как «не измерено»: ноль в провод не попадает вовсе.
        Assert.False(root.TryGetProperty("currentJitterMs", out _));
        Assert.False(root.TryGetProperty("currentLossPercent", out _));
        Assert.DoesNotContain("\"currentJitterMs\"", result.DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"currentLossPercent\"", result.DataJson, StringComparison.Ordinal);
    }

    // ── способ замера в ответе живой оптимизации ───────────────────────────────────────────────

    [Fact]
    public async Task Живая_оптимизация_называет_способ_замера_вместе_с_числом()
    {
        // Число без способа ничего не доказывает: «12 мс» от статистики соединения и «12 мс» от проб —
        // разные утверждения. Ответ службы обязан называть способ, иначе окно и проверка догадываются.
        var optimizer = new RealtimeOptimizer(
            new SingleGameWatcher(new GameFlowInfo(
                ProcessId: 4242,
                ProcessName: "javaw.exe",
                GameTitleRu: "Minecraft (Java)",
                RemoteEndpoint: "3.4.5.6",
                RemotePort: 25565,
                Transport: "TCP",
                AntiCheat: AntiCheatStatus.VerifiedSafe)),
            new QueuedMeasurements(
                new PathMeasurement(true, 120, 2, null, 12, "Замер живого TCP-потока игры.", Method: ProbeMethodUsed.LiveTcpEstats),
                new PathMeasurement(true, 96, 2, null, 12, "Замер живого TCP-потока игры.", Method: ProbeMethodUsed.LiveTcpEstats)),
            new NoopActionRunner());

        LiveEngineOperations ops = Build(realtime: optimizer);
        EngineOperationResult started = await ops.SetRealtimeAsync(start: true);

        Assert.Contains("\"methodRu\":\"живой поток игры (TCP-статистика)\"", started.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"baselineMethodRu\":\"живой поток игры (TCP-статистика)\"", started.DataJson, StringComparison.Ordinal);

        EngineOperationResult stopped = await ops.SetRealtimeAsync(start: false);

        Assert.Contains("\"methodRu\":\"живой поток игры (TCP-статистика)\"", stopped.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Без_замера_способ_не_подставляется()
    {
        var optimizer = new RealtimeOptimizer(
            new SingleGameWatcher(new GameFlowInfo(
                ProcessId: 4242,
                ProcessName: "javaw.exe",
                GameTitleRu: "Minecraft (Java)",
                RemoteEndpoint: "3.4.5.6",
                RemotePort: 25565,
                Transport: "TCP",
                AntiCheat: AntiCheatStatus.VerifiedSafe)),
            new QueuedMeasurements(PathMeasurement.Unmeasurable("поток игры пропал")),
            new NoopActionRunner());

        LiveEngineOperations ops = Build(realtime: optimizer);
        EngineOperationResult result = await ops.SetRealtimeAsync(start: true);

        // Поля нет вовсе: «способ замера не записан» — это не способ, и придумывать его нельзя.
        Assert.DoesNotContain("\"methodRu\"", result.DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"baselineMethodRu\"", result.DataJson, StringComparison.Ordinal);
    }

    private sealed class SingleGameWatcher(GameFlowInfo flow) : IGameFlowWatcher
    {
        public Task<IReadOnlyList<GameFlowInfo>> FindActiveGameFlowsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameFlowInfo>>([flow]);
    }

    private sealed class QueuedMeasurements(params PathMeasurement[] windows) : IRealtimeMeasurementSource
    {
        private readonly Queue<PathMeasurement> _windows = new(windows);

        public Task<PathMeasurement> MeasurePathAsync(string endpoint, int port, CancellationToken ct = default) =>
            Task.FromResult(_windows.Count > 0 ? _windows.Dequeue() : PathMeasurement.Unmeasurable("источник закончился"));
    }

    private sealed class NoopActionRunner : IOptimizationActionRunner
    {
        public Task<OptimizationActionOutcome> ApplyAsync(OptimizationActionDescriptor action, CancellationToken ct = default) =>
            Task.FromResult(OptimizationActionOutcome.Ok("применено"));

        public Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct = default) =>
            Task.FromResult(OptimizationActionOutcome.Ok("снято"));
    }

    // ── применение пресетов ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Пресет_без_состава_не_применяется_и_называет_причину()
    {
        var coordinator = new CoordinatorDouble();

        LiveEngineOperations ops = Build(catalog: new CatalogDouble(), coordinator: coordinator);

        EngineOperationResult result = await ops.ApplyPresetAsync(TweakProfilePreset.LowLatencyGaming, requestRestorePoint: true);

        Assert.False(result.Ok);
        Assert.Contains("нет ни одного твика", result.MessageRu);
        Assert.Contains("Низкий пинг", result.MessageRu);
        Assert.Empty(coordinator.Batches);
    }

    [Fact]
    public async Task Пресет_собирает_состав_из_каталога_и_подписывается_честными_счётчиками()
    {
        var catalog = new CatalogDouble(Tweak("A"), Tweak("B"))
        {
            PresetMembership = { [TweakProfilePreset.LowLatencyGaming] = ["A"] },
        };
        var coordinator = new CoordinatorDouble { AppliedPerBatch = 1, NoOpPerBatch = 0, FailedPerBatch = 0 };
        LiveEngineOperations ops = Build(catalog: catalog, coordinator: coordinator);

        EngineOperationResult result = await ops.ApplyPresetAsync(TweakProfilePreset.LowLatencyGaming, requestRestorePoint: true);

        Assert.True(result.Ok);
        Assert.Contains("применено 1 из 1", result.MessageRu);
        Assert.Contains("снапшот-1", result.DataJson);

        CoordinatorCall call = Assert.Single(coordinator.Batches);
        Assert.Equal(TweakProfilePreset.LowLatencyGaming, call.Preset);
        Assert.True(call.RestorePoint);
        Assert.Equal(["A"], call.Codes);
    }

    [Fact]
    public async Task Пресет_с_ошибками_отвечает_отказом_и_называет_что_именно_не_вышло()
    {
        var catalog = new CatalogDouble(Tweak("A"), Tweak("B"))
        {
            PresetMembership = { [TweakProfilePreset.MaxThroughput] = ["A", "B"] },
        };
        var coordinator = new CoordinatorDouble { AppliedPerBatch = 1, FailedPerBatch = 1, FailReasonRu = "значение не читается из системы" };
        LiveEngineOperations ops = Build(catalog: catalog, coordinator: coordinator);

        EngineOperationResult result = await ops.ApplyPresetAsync(TweakProfilePreset.MaxThroughput, requestRestorePoint: false);

        Assert.False(result.Ok);
        Assert.Contains("ошибок 1", result.MessageRu);
        Assert.Contains("значение не читается из системы", result.MessageRu);
        Assert.Contains("\"failed\":1", result.DataJson);
    }

    // ── ручной набор твиков ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Неизвестный_код_твика_отклоняется_со_всем_списком_виновных()
    {
        var coordinator = new CoordinatorDouble();
        LiveEngineOperations ops = Build(catalog: new CatalogDouble(Tweak("A")), coordinator: coordinator);

        EngineOperationResult result = await ops.ApplyTweaksAsync(["A", "ЧУЖОЙ", "ЕЩЁ_ОДИН"], requestRestorePoint: true);

        Assert.False(result.Ok);
        Assert.Contains("ЧУЖОЙ", result.MessageRu);
        Assert.Contains("ЕЩЁ_ОДИН", result.MessageRu);
        Assert.Empty(coordinator.Batches);
    }

    [Fact]
    public async Task Повторы_в_ручном_наборе_применяются_один_раз()
    {
        var coordinator = new CoordinatorDouble { AppliedPerBatch = 2 };
        LiveEngineOperations ops = Build(catalog: new CatalogDouble(Tweak("A"), Tweak("B")), coordinator: coordinator);

        EngineOperationResult result = await ops.ApplyTweaksAsync(["A", "B", "A"], requestRestorePoint: true);

        Assert.True(result.Ok);
        CoordinatorCall call = Assert.Single(coordinator.Batches);
        Assert.Equal(["A", "B"], call.Codes);
        Assert.Equal(TweakProfilePreset.CustomUserPreset, call.Preset);
    }

    [Fact]
    public async Task Пустой_ручной_набор_не_превращается_в_применение_всего_каталога()
    {
        var coordinator = new CoordinatorDouble();
        LiveEngineOperations ops = Build(coordinator: coordinator);

        EngineOperationResult result = await ops.ApplyTweaksAsync([], requestRestorePoint: true);

        Assert.False(result.Ok);
        Assert.Empty(coordinator.Batches);
    }

    // ── откат ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Откат_без_снапшотов_объясняет_что_возвращать_нечего()
    {
        LiveEngineOperations ops = Build();

        EngineOperationResult result = await ops.RollbackAsync(null);

        Assert.False(result.Ok);
        Assert.Contains("Снапшотов нет", result.MessageRu);
    }

    [Fact]
    public async Task Откат_без_номера_берёт_самый_свежий_снапшот()
    {
        var snapshots = new SnapshotDouble(
            Snapshot("snap-старый", DateTime.UtcNow.AddHours(-3)),
            Snapshot("snap-свежий", DateTime.UtcNow.AddMinutes(-5)));
        LiveEngineOperations ops = Build(snapshots: snapshots);

        EngineOperationResult result = await ops.RollbackAsync(null);

        Assert.True(result.Ok);
        Assert.Equal("snap-свежий", snapshots.LastRollbackId);
        Assert.Contains("Возвращено параметров: 2", result.MessageRu);
    }

    [Fact]
    public async Task Откат_по_неизвестному_номеру_подсказывает_последний()
    {
        var snapshots = new SnapshotDouble(Snapshot("snap-1", DateTime.UtcNow));
        LiveEngineOperations ops = Build(snapshots: snapshots);

        EngineOperationResult result = await ops.RollbackAsync("snap-нет-такого");

        Assert.False(result.Ok);
        Assert.Contains("snap-нет-такого", result.MessageRu);
        Assert.Contains("snap-1", result.MessageRu);
        Assert.Null(snapshots.LastRollbackId);
    }

    [Fact]
    public async Task Повторный_откат_того_же_снапшота_отклоняется()
    {
        SystemSnapshot rolledBack = Snapshot("snap-1", DateTime.UtcNow) with
        {
            WasRolledBack = true,
            RolledBackUtc = DateTime.UtcNow.AddMinutes(-1),
        };

        LiveEngineOperations ops = Build(snapshots: new SnapshotDouble(rolledBack));

        EngineOperationResult result = await ops.RollbackAsync("snap-1");

        Assert.False(result.Ok);
        Assert.Contains("уже откатывали", result.MessageRu);
    }

    // ── журнал ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Без_журнала_хвост_записей_не_выдумывается()
    {
        LiveEngineOperations ops = Build(journal: null);

        EngineOperationResult result = await ops.JournalTailAsync(10);

        Assert.False(result.Ok);
        Assert.Contains("Журнал не подключён", result.MessageRu);
    }

    [Fact]
    public async Task Пустой_журнал_отвечает_что_программа_ничего_не_меняла()
    {
        LiveEngineOperations ops = Build(journal: new AuditJournalRepository(_database));

        EngineOperationResult result = await ops.JournalTailAsync(10);

        Assert.True(result.Ok);
        Assert.Contains("Журнал пуст", result.MessageRu);
        Assert.Contains("\"count\":0", result.DataJson);
    }

    [Fact]
    public async Task Хвост_журнала_отдаётся_новыми_сверху_и_ограничен_числом()
    {
        var journal = new AuditJournalRepository(_database);
        await journal.AppendModificationLogAsync("APPLY", Result("A", TweakOutcome.VerifiedEffective, DateTime.UtcNow.AddMinutes(-10)));
        await journal.AppendModificationLogAsync("APPLY", Result("B", TweakOutcome.VerifiedEffective, DateTime.UtcNow.AddMinutes(-5)));
        await journal.AppendModificationLogAsync("APPLY", Result("C", TweakOutcome.Failed, DateTime.UtcNow, "политика запретила запись"));

        LiveEngineOperations ops = Build(journal: journal);

        EngineOperationResult result = await ops.JournalTailAsync(2);

        Assert.True(result.Ok);
        Assert.Contains("Последние 2 записей", result.MessageRu);
        Assert.Contains("Не получилось: 1", result.MessageRu);

        int indexB = result.DataJson!.IndexOf("\"B\"", StringComparison.Ordinal);
        int indexC = result.DataJson.IndexOf("\"C\"", StringComparison.Ordinal);
        Assert.True(indexC < indexB, "новые записи обязаны идти раньше старых");
        Assert.DoesNotContain("\"A\"", result.DataJson);
        Assert.Contains("политика запретила запись", result.DataJson);
    }

    // ── починка сети ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Починка_сети_без_журнала_ничего_не_меняет()
    {
        LiveEngineOperations ops = Build(journal: null);

        EngineOperationResult result = await ops.RepairNetworkAsync();

        Assert.False(result.Ok);
        Assert.Contains("Журнал не подключён", result.MessageRu);
    }

    [Fact]
    public async Task Починка_сети_на_чистой_системе_говорит_что_остатков_нет()
    {
        LiveEngineOperations ops = Build(journal: new AuditJournalRepository(_database));

        EngineOperationResult result = await ops.RepairNetworkAsync();

        Assert.True(result.Ok);
        Assert.Contains("Остатков после аварии не найдено", result.MessageRu);
        Assert.Contains("\"pendingOperations\":0", result.DataJson);
    }

    [Fact]
    public async Task Починка_сети_перечисляет_остатки_и_честно_говорит_что_снять_их_пока_нечем()
    {
        var journal = new AuditJournalRepository(_database);
        await journal.BeginPendingOperationAsync("op-1", null, "ROUTE_ADD", "3.4.5.6", "{}");
        await journal.BeginPendingOperationAsync("op-2", null, "ROUTE_ADD", "5.6.7.8", "{}");
        await journal.BeginPendingOperationAsync("op-3", null, "WINDIVERT_START", "game", "{}");

        LiveEngineOperations ops = Build(journal: journal);

        EngineOperationResult result = await ops.RepairNetworkAsync();

        Assert.False(result.Ok);
        Assert.Contains("ROUTE_ADD: 2", result.MessageRu);
        Assert.Contains("WINDIVERT_START: 1", result.MessageRu);
        Assert.Contains("не подключено", result.MessageRu);
        Assert.Contains("\"pendingOperations\":3", result.DataJson);
    }

    // ── живая оптимизация и профиль потребления ─────────────────────────────────────────────────

    [Fact]
    public async Task Без_движка_живой_оптимизации_запуск_отклоняется()
    {
        LiveEngineOperations ops = Build(realtime: null);

        EngineOperationResult result = await ops.SetRealtimeAsync(start: true);

        Assert.False(result.Ok);
        Assert.Contains("не подключён", result.MessageRu);
    }

    [Fact]
    public async Task Профиль_потребления_без_настроек_не_меняется_молча()
    {
        LiveEngineOperations ops = Build();

        EngineOperationResult result = await ops.SetPerfProfileAsync(PerfProfile.Economy);

        Assert.False(result.Ok);
        Assert.Contains("не подключены", result.MessageRu);
    }

    [Fact]
    public async Task Профиль_потребления_проверяется_по_фактическому_значению()
    {
        PerfProfile stored = PerfProfile.Balanced;
        LiveEngineOperations ops = Build(readProfile: () => stored, applyProfile: profile => stored = profile);

        EngineOperationResult ok = await ops.SetPerfProfileAsync(PerfProfile.Economy);
        Assert.True(ok.Ok);
        Assert.Contains("экономно", ok.MessageRu);
        Assert.Contains("\"profileWire\":\"economy\"", ok.DataJson);

        // Настройка, которая не сохранилась, обязана вернуть отказ, а не «готово».
        LiveEngineOperations stuck = Build(readProfile: () => PerfProfile.Balanced, applyProfile: _ => { });
        EngineOperationResult failed = await stuck.SetPerfProfileAsync(PerfProfile.MaxPerformance);

        Assert.False(failed.Ok);
        Assert.Contains("не сохранился", failed.MessageRu);
    }

    // ── вспомогательное ─────────────────────────────────────────────────────────────────────────

    // ── чтение состояния системы ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Чтение_состояния_пересказывает_прочитанное_и_отдаёт_его_как_есть()
    {
        var reader = new FakeSystemStateReader();
        LiveEngineOperations ops = Build(stateReader: reader);

        EngineOperationResult result = await ops.ReadSystemStateAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, reader.CallCount);
        Assert.Contains("Windows 11 Pro 26100", result.MessageRu);
        Assert.Contains("права администратора есть", result.MessageRu);
        Assert.Contains("Ethernet", result.MessageRu);
        Assert.Contains("1000 Мбит/с", result.MessageRu);
        Assert.Contains("MTU 1500", result.MessageRu);
        Assert.Contains("Прочитано всё", result.MessageRu);
        Assert.Contains("\"windowsEditionAndBuild\":\"Windows 11 Pro 26100\"", result.DataJson);
        Assert.Contains("\"tcpAutoTuningLevel\":\"normal\"", result.DataJson);
        Assert.Contains("\"adapterDisplayName\":\"Ethernet\"", result.DataJson);
    }

    [Fact]
    public async Task Чтение_состояния_не_подменяет_неизвестное_нулями()
    {
        var reader = new FakeSystemStateReader
        {
            Reading = new FakeSystemStateReader().Reading with
            {
                ActiveAdapter = null,
                LinkSpeedMbps = null,
                CurrentMtuBytes = null,
                IsNagleDisabledOnActiveNic = null,
                CongestionProvider = null,
                UnresolvedFields = ["Скорость активного адаптера"],
            },
        };

        EngineOperationResult result = await Build(stateReader: reader).ReadSystemStateAsync();

        Assert.True(result.Ok);
        Assert.Contains("активный адаптер не определён", result.MessageRu);
        Assert.Contains("MTU не прочитан", result.MessageRu);
        Assert.Contains("Не прочитано полей: 1", result.MessageRu);
        Assert.DoesNotContain("linkSpeedMbps", result.DataJson);
        Assert.DoesNotContain("currentMtuBytes", result.DataJson);
        Assert.Contains("Скорость активного адаптера", result.DataJson);
    }

    [Fact]
    public async Task Чтение_состояния_без_читателя_честно_отказывает_а_не_молчит()
    {
        EngineOperationResult result = await Build().ReadSystemStateAsync();

        Assert.False(result.Ok);
        Assert.Contains("не подключено", result.MessageRu);
        Assert.Null(result.DataJson);
    }

    // ── задача U: целостность и копии базы видны и восстанавливаются ───────────────────────────

    [Fact]
    public async Task StorageInfo_называет_состояние_базы_и_число_копий()
    {
        var backups = new DatabaseBackupService(_database);
        Assert.NotNull(await backups.CreateBackupAsync());

        EngineOperationResult result = await Build(backups: backups).GetStorageInfoAsync();

        Assert.True(result.Ok);
        Assert.Contains("\"healthy\":true", result.DataJson);
        Assert.Contains("\"backups\":1", result.DataJson);
        Assert.Contains("проверка целостности пройдена", result.MessageRu);
        Assert.Contains("резервных копий: 1", result.MessageRu);
    }

    [Fact]
    public async Task StorageInfo_без_подключённых_копий_говорит_об_этом_прямо()
    {
        EngineOperationResult result = await Build().GetStorageInfoAsync();

        Assert.True(result.Ok);
        Assert.Contains("резервные копии в этой сборке не подключены", result.MessageRu);
        Assert.Contains("\"backups\":0", result.DataJson);
    }

    [Fact]
    public async Task RestoreDatabase_без_подтверждения_отказывает_а_не_восстанавливает()
    {
        var backups = new DatabaseBackupService(_database);
        Assert.NotNull(await backups.CreateBackupAsync());

        EngineOperationResult result = await Build(backups: backups).RestoreDatabaseAsync(confirm: false);

        Assert.False(result.Ok);
        Assert.Contains("confirm=yes", result.MessageRu);
        Assert.Contains("пропадут", result.MessageRu);
    }

    [Fact]
    public async Task RestoreDatabase_ставит_копию_на_место_и_говорит_о_потере_записей()
    {
        var backups = new DatabaseBackupService(_database);
        Assert.NotNull(await backups.CreateBackupAsync());

        // После копии появилась запись — именно её восстановление обязано потерять (и сказать об этом).
        var journal = new AuditJournalRepository(_database);
        await journal.BeginPendingOperationAsync("op-restore", null, "ROUTE_ADD", "203.0.113.7", "{}");
        Assert.Single(await journal.GetPendingOperationsAsync());

        EngineOperationResult result = await Build(backups: backups).RestoreDatabaseAsync(confirm: true);

        Assert.True(result.Ok);
        Assert.Contains("восстановлена из копии", result.MessageRu);
        Assert.Contains("пропали", result.MessageRu);
        Assert.Empty(await journal.GetPendingOperationsAsync());
        Assert.Contains("\"remainingBackups\":", result.DataJson);

        // Читаемая база перед заменой сохранена рядом: у человека остаётся путь назад.
        string? previous = Directory
            .GetFiles(Path.GetDirectoryName(_databasePath)!, Path.GetFileName(_databasePath) + ".before-restore-*")
            .FirstOrDefault();
        Assert.NotNull(previous);
        Assert.Contains("прежний файл базы сохранён", result.MessageRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreDatabase_без_копий_честно_отказывает()
    {
        var backups = new DatabaseBackupService(_database);

        EngineOperationResult result = await Build(backups: backups).RestoreDatabaseAsync(confirm: true);

        Assert.False(result.Ok);
        Assert.Contains("резервных копий базы нет", result.MessageRu);
    }

    private LiveEngineOperations Build(
        ITweakCatalog? catalog = null,
        ISnapshotService? snapshots = null,
        ITweakCoordinator? coordinator = null,
        AuditJournalRepository? journal = null,
        RealtimeOptimizer? realtime = null,
        Func<PerfProfile>? readProfile = null,
        Action<PerfProfile>? applyProfile = null,
        bool isElevationAvailable = true,
        ISystemStateReader? stateReader = null,
        DatabaseBackupService? backups = null) =>
        new(
            catalog ?? new CatalogDouble(Tweak("A"), Tweak("B")),
            snapshots ?? new SnapshotDouble(),
            coordinator ?? new CoordinatorDouble(),
            isElevationAvailable,
            journal,
            realtime,
            readProfile,
            applyProfile,
            stateReader: stateReader,
            backups: backups);

    private static SystemSnapshot Snapshot(string id, DateTime createdUtc) =>
        new(id, createdUtc, "тестовый снапшот", TweakProfilePreset.LowLatencyGaming, false, "hash", false, null, []);

    private static TweakOperationResult Result(string code, TweakOutcome outcome, DateTime timestampUtc, string? failure = null) =>
        new(code, $"Тестовый твик {code}", $@"HKLM\Test\{code}", outcome, "0", "1", outcome == TweakOutcome.Failed ? "0" : "1", failure, false, timestampUtc);

    /// <summary>Каталог с настраиваемым составом пресетов: так проверяется, что состав берётся из каталога, а не выдумывается.</summary>
    private sealed class CatalogDouble : ITweakCatalog
    {
        private readonly List<ITweak> _tweaks;

        public CatalogDouble(params ITweak[] tweaks) => _tweaks = [.. tweaks];

        public Dictionary<TweakProfilePreset, string[]> PresetMembership { get; } = [];

        public IReadOnlyList<ITweak> GetAllTweaks() => _tweaks;

        public IReadOnlyList<IActionTask> GetAllActionTasks() => [];

        public ITweak? GetByCode(string tweakCode) =>
            _tweaks.FirstOrDefault(tweak => string.Equals(tweak.Id, tweakCode, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<ITweak> GetByPreset(TweakProfilePreset preset) =>
            PresetMembership.TryGetValue(preset, out string[]? codes)
                ? _tweaks.Where(tweak => codes.Contains(tweak.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                : _tweaks;
    }

    private sealed class SnapshotDouble(params SystemSnapshot[] snapshots) : ISnapshotService
    {
        private readonly List<SystemSnapshot> _snapshots = [.. snapshots];

        public string? LastRollbackId { get; private set; }

        public Task<(SystemSnapshot Snapshot, RestorePointResultKind RestorePointStatus, string RestorePointMessageRu)>
            CreateSnapshotAsync(string label, TweakProfilePreset preset, IReadOnlyList<ITweak> tweaks, bool requestWindowsRestorePoint, CancellationToken ct = default) =>
            throw new NotSupportedException("В этих тестах снапшоты только читаются.");

        public Task<IReadOnlyList<SystemSnapshot>> GetAllSnapshotsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemSnapshot>>(_snapshots.ToList());

        public Task<SnapshotRollbackReport> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default)
        {
            LastRollbackId = snapshotId;
            var results = new List<TweakOperationResult>
            {
                Result("A", TweakOutcome.RolledBack, DateTime.UtcNow),
                Result("B", TweakOutcome.RolledBack, DateTime.UtcNow),
            };

            return Task.FromResult(new SnapshotRollbackReport(
                snapshotId,
                SnapshotRollbackStatus.FullyRolledBack,
                results,
                [],
                "Прежние настройки возвращены."));
        }
    }

    /// <summary>Что координатор получил от службы: так проверяется, что состав и пресет дошли без подмены.</summary>
    private sealed record CoordinatorCall(string Title, TweakProfilePreset Preset, List<string> Codes, bool RestorePoint);

    private sealed class CoordinatorDouble : ITweakCoordinator
    {
        public List<CoordinatorCall> Batches { get; } = [];

        public int AppliedPerBatch { get; set; } = 1;

        public int NoOpPerBatch { get; set; }

        public int FailedPerBatch { get; set; }

        public int SkippedPerBatch { get; set; }

        public string FailReasonRu { get; set; } = "не удалось";

        public Task<TweakBatchReport> ExecuteBatchAsync(
            string batchTitle,
            TweakProfilePreset preset,
            IReadOnlyList<ITweak> selectedTweaks,
            bool requestWindowsRestorePoint,
            CancellationToken ct = default)
        {
            Batches.Add(new CoordinatorCall(batchTitle, preset, selectedTweaks.Select(tweak => tweak.Id).ToList(), requestWindowsRestorePoint));

            var results = new List<TweakOperationResult>();
            for (int i = 0; i < AppliedPerBatch; i++)
            {
                results.Add(Result($"OK-{i}", TweakOutcome.VerifiedEffective, DateTime.UtcNow));
            }

            for (int i = 0; i < NoOpPerBatch; i++)
            {
                results.Add(Result($"NOOP-{i}", TweakOutcome.SkippedNoOp, DateTime.UtcNow));
            }

            for (int i = 0; i < SkippedPerBatch; i++)
            {
                results.Add(Result($"SKIP-{i}", TweakOutcome.SkippedUnsupported, DateTime.UtcNow));
            }

            for (int i = 0; i < FailedPerBatch; i++)
            {
                results.Add(Result($"FAIL-{i}", TweakOutcome.Failed, DateTime.UtcNow, FailReasonRu));
            }

            SystemSnapshot snapshot = Snapshot("снапшот-1", DateTime.UtcNow);

            return Task.FromResult(new TweakBatchReport(
                snapshot,
                RestorePointResultKind.CreatedSuccessfully,
                "Точка восстановления создана.",
                results,
                AppliedPerBatch,
                NoOpPerBatch,
                SkippedPerBatch,
                FailedPerBatch,
                RebootRequired: false));
        }

        public Task<int> RecoverInterruptedOperationsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
