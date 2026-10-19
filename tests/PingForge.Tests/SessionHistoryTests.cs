using Microsoft.Data.Sqlite;
using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// История сессий оптимизации (задача J): «до → после» в базе, вкладка «Сессии», сравнение двух
/// сессий и график по дням.
///
/// Правила честности, которые здесь и проверяются:
///   • сессия без измеренного «после» остаётся незавершённой — ни нуля, ни выдуманного улучшения;
///   • сессии к разным серверам не сравниваются и не усредняются;
///   • очистка истории не трогает журнал изменений системы;
///   • старая база получает новые колонки миграцией, а не «начинает с нуля».
/// </summary>
public sealed class SessionHistoryTests
{
    private static readonly FakeEngineChannel Fallback = new();

    private const string SameServer = "203.0.113.9";
    private const string OtherServer = "198.51.100.7";

    // ── база ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Начало_сессии_сохраняется_незавершённым_и_не_выдумывает_после()
    {
        string path = TempDatabaseFile.Create("pf_sessions_start");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var repository = new OptimizationSessionRepository(database);

            await repository.SaveAsync(Session("start", DateTime.UtcNow, completed: false, baseline: 120, final: null));

            OptimizationSessionRecord stored = Assert.Single(await repository.GetRecentAsync(10));

            Assert.False(stored.IsCompleted);
            Assert.Equal(SessionOutcomes.NotFinished, stored.Outcome);
            Assert.Null(stored.FinalRttMs);
            Assert.Null(stored.ImprovementMs);
            Assert.Equal(120, stored.BaselineRttMs);
            Assert.Null(stored.StoppedUtc);
            Assert.True(stored.HasGame);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    [Fact]
    public async Task Остановка_дописывает_после_и_считает_разницу()
    {
        string path = TempDatabaseFile.Create("pf_sessions_stop");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var repository = new OptimizationSessionRepository(database);

            DateTime started = DateTime.UtcNow.AddMinutes(-20);
            await repository.SaveAsync(Session("stop", started, completed: false, baseline: 120, final: null));

            // Та же сессия дописывается при остановке: «после», действия и пометка откатa.
            await repository.SaveAsync(Session("stop", started, completed: true, baseline: 120, final: 95) with
            {
                StoppedUtc = DateTime.UtcNow,
                Actions = ["Маршрут для игры", "DSCP"],
                RollbackState = SessionRollbackStates.EverythingReverted,
                StatusNoteRu = "Сессия завершена: «после» измерено, свои изменения сняты.",
            });

            OptimizationSessionRecord stored = Assert.Single(await repository.GetRecentAsync(10));

            Assert.True(stored.IsCompleted);
            Assert.Equal(SessionOutcomes.Completed, stored.Outcome);
            Assert.Equal(120, stored.BaselineRttMs);
            Assert.Equal(95, stored.FinalRttMs);
            Assert.Equal(25, stored.ImprovementMs);
            Assert.Equal(2, stored.Actions.Count);
            Assert.Equal(SessionRollbackStates.EverythingReverted, stored.RollbackState);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    [Fact]
    public async Task Очистка_истории_не_трогает_журнал_изменений()
    {
        string path = TempDatabaseFile.Create("pf_sessions_clear");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var repository = new OptimizationSessionRepository(database);
            var journal = new AuditJournalRepository(database);

            await repository.SaveAsync(Session("keep", DateTime.UtcNow, completed: false, baseline: null, final: null));

            await using (SqliteConnection connection = await database.OpenAsync())
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO system_modification_log (
                        logged_utc, event_kind, tweak_code, tweak_title, resource_locator,
                        previous_value_text, applied_value_text, verified_actual_text,
                        succeeded, reboot_needed, explanation)
                    VALUES ($ts, 'TWEAK_APPLY', 'TCP_NO_DELAY', 'Отключение алгоритма Нейгла', 'Tcpip',
                        '0', '1', '1', 1, 0, 'VerifiedEffective');
                    """;
                command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(1, await journal.CountModificationLogEntriesAsync());

            int deleted = await repository.DeleteAllAsync();

            Assert.Equal(1, deleted);
            Assert.Empty(await repository.GetRecentAsync(10));
            Assert.Equal(1, await journal.CountModificationLogEntriesAsync());
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    [Fact]
    public async Task Старая_база_получает_колонки_истории_миграцией()
    {
        string path = TempDatabaseFile.Create("pf_sessions_migrate");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();

            // Так выглядит база до задачи J: колонок «игра, действия, откат, исход» ещё нет.
            // Удаляем их и инициализируем базу заново — миграция обязана вернуть их, сохранив таблицу.
            await using (SqliteConnection connection = await database.OpenAsync())
            {
                foreach (string column in new[]
                {
                    "game_title_ru", "applied_actions_json", "rollback_state", "outcome", "is_completed",
                })
                {
                    await using SqliteCommand drop = connection.CreateCommand();
                    drop.CommandText = $"ALTER TABLE route_optimization_sessions DROP COLUMN {column};";
                    await drop.ExecuteNonQueryAsync();
                }
            }

            await database.EnsureInitializedAsync();

            await using (SqliteConnection connection = await database.OpenAsync())
            {
                var columns = new List<string>();
                await using SqliteCommand info = connection.CreateCommand();
                info.CommandText = "SELECT name FROM pragma_table_info('route_optimization_sessions');";
                await using SqliteDataReader reader = await info.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(0));
                }

                Assert.Contains("game_title_ru", columns);
                Assert.Contains("applied_actions_json", columns);
                Assert.Contains("rollback_state", columns);
                Assert.Contains("outcome", columns);
                Assert.Contains("is_completed", columns);
            }

            Assert.Contains(SqliteDatabaseInitializer.SchemaVersion, database.GetSchemaVersions());

            var repository = new OptimizationSessionRepository(database);
            await repository.SaveAsync(Session("migrated", DateTime.UtcNow, completed: false, baseline: 100, final: null));

            Assert.Single(await repository.GetRecentAsync(10));
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    // ── вкладка «Сессии» ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Вкладка_сессий_читает_историю_и_называет_незавершённую_сессию()
    {
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.Sessions
            ? EngineResponse.Success(request.Id, "Сессий: 2, завершено: 1, без итога: 1.", TwoSessionsJson)
            : Fallback.DefaultAnswer(request));

        var sessions = new SessionHistoryViewModel(new EngineClient(channel), new FakeDialogs());
        await sessions.RefreshCommand.ExecuteAsync();

        Assert.Equal("200", Assert.Single(channel.Requests).Argument("limit"));
        Assert.Equal(2, sessions.Rows.Count);
        Assert.True(sessions.HasRows);
        Assert.False(sessions.IsEmpty);

        SessionHistoryRow unfinished = sessions.Rows[0];
        Assert.False(unfinished.IsCompleted);
        Assert.Contains("сессия не завершена", unfinished.ResultRu);
        Assert.DoesNotContain("стало", unfinished.ResultRu);
        Assert.Contains("Сессий: 2", sessions.SummaryRu);
        Assert.Contains("без итога: 1", sessions.SummaryRu);

        SessionHistoryRow completed = sessions.Rows[1];
        Assert.True(completed.IsCompleted);
        Assert.Contains("было 120 мс → стало 95 мс", completed.ResultRu);
        Assert.Contains("лучше на 25", completed.ResultRu);
    }

    [Fact]
    public async Task Пустая_история_не_выдаётся_за_непрочитанную()
    {
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.Sessions
            ? EngineResponse.Success(request.Id, "История сессий пуста: живая оптимизация ещё не запускалась.", """{"count":0,"records":[]}""")
            : Fallback.DefaultAnswer(request));

        var sessions = new SessionHistoryViewModel(new EngineClient(channel), new FakeDialogs());
        await sessions.RefreshCommand.ExecuteAsync();

        Assert.Empty(sessions.Rows);
        Assert.True(sessions.IsEmpty);
        Assert.Contains("пуста", sessions.StatusRu);
    }

    [Fact]
    public async Task Недоступная_служба_не_выдаётся_за_пустую_историю()
    {
        var channel = new FakeEngineChannel { ThrowOnSend = new IOException("нет связи") };
        var sessions = new SessionHistoryViewModel(new EngineClient(channel), new FakeDialogs());

        await sessions.RefreshCommand.ExecuteAsync();

        Assert.False(sessions.IsEmpty);
        Assert.Contains("не отвечает", sessions.StatusRu);
        Assert.Contains("не знаем", sessions.SummaryRu);
    }

    [Fact]
    public void Сравнение_отказывается_сравнивать_разные_серверы()
    {
        var first = Row("a", SameServer, baseline: 120, final: 95, completed: true);
        var second = Row("b", OtherServer, baseline: 100, final: 90, completed: true);

        SessionComparisonResult result = SessionComparison.Compare(first, second);

        Assert.False(result.Comparable);
        Assert.Contains(result.Lines, line => line.Contains("разным серверам", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, line => line.Contains("разошёлся", StringComparison.Ordinal));
    }

    [Fact]
    public void Сравнение_не_выдумывает_результат_для_незавершённой_сессии()
    {
        var first = Row("a", SameServer, baseline: 120, final: null, completed: false);
        var second = Row("b", SameServer, baseline: 118, final: 90, completed: true);

        SessionComparisonResult result = SessionComparison.Compare(first, second);

        Assert.True(result.Comparable);
        Assert.Contains(result.Lines, line => line.Contains("не измерено", StringComparison.Ordinal));
        Assert.DoesNotContain("улучшение", string.Join(" ", result.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void График_по_дням_не_усредняет_разные_серверы()
    {
        DateTime day = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        var sessions = new List<SessionHistoryRow>
        {
            Row("a", SameServer, baseline: 120, final: 100, completed: true, started: day),
            Row("b", SameServer, baseline: 100, final: 90, completed: true, started: day.AddHours(2)),
            Row("c", OtherServer, baseline: 400, final: 390, completed: true, started: day),
            Row("d", SameServer, baseline: null, final: null, completed: false, started: day.AddDays(1)),
        };

        IReadOnlyList<DailyRttPoint> points = DailyRttHistory.Build(sessions, SameServer);

        DailyRttPoint point = Assert.Single(points);
        Assert.Equal(2, point.SessionCount);
        Assert.Equal(110, point.AverageRttMs);
        Assert.Contains("110", point.LineRu);
    }

    [Fact]
    public void График_без_выбранного_сервера_не_строится()
    {
        var sessions = new List<SessionHistoryRow> { Row("a", SameServer, baseline: 120, final: 100, completed: true) };

        Assert.Empty(DailyRttHistory.Build(sessions, null));
        Assert.Empty(DailyRttHistory.Build(sessions, " "));
    }

    [Fact]
    public async Task Очистка_истории_спрашивает_подтверждение_и_не_трогает_журнал()
    {
        bool cleared = false;
        var dialogs = new FakeDialogs { ConfirmAnswer = true };
        var channel = new FakeEngineChannel(request => request.Command switch
        {
            EngineCommandKind.Sessions => EngineResponse.Success(
                request.Id,
                cleared ? "История сессий пуста." : "Сессий: 1.",
                cleared ? """{"count":0,"records":[]}""" : OneSessionJson),
            EngineCommandKind.SessionsClear => ClearAnswer(request, () => cleared = true),
            _ => Fallback.DefaultAnswer(request),
        });

        var sessions = new SessionHistoryViewModel(new EngineClient(channel), dialogs);
        await sessions.RefreshCommand.ExecuteAsync();
        await sessions.ClearCommand.ExecuteAsync();

        var confirmation = Assert.Single(dialogs.Confirmations);
        Assert.Contains("Журнал изменений системы не тронется", confirmation.Message);

        Assert.True(cleared);
        Assert.Contains(channel.Requests, request => request.Command == EngineCommandKind.SessionsClear);
        Assert.Empty(sessions.Rows);
        Assert.Contains("пуста", sessions.StatusRu);
    }

    [Fact]
    public async Task Отказ_от_подтверждения_оставляет_историю_как_была()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.Sessions
            ? EngineResponse.Success(request.Id, "Сессий: 1.", OneSessionJson)
            : Fallback.DefaultAnswer(request));

        var sessions = new SessionHistoryViewModel(new EngineClient(channel), dialogs);
        await sessions.RefreshCommand.ExecuteAsync();
        await sessions.ClearCommand.ExecuteAsync();

        Assert.DoesNotContain(channel.Requests, request => request.Command == EngineCommandKind.SessionsClear);
        Assert.Single(sessions.Rows);
        Assert.Contains("отменена", sessions.StatusRu);
    }

    // ── служба: команды и канал чтения ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Команда_сессий_отдаёт_предел_и_доступна_без_прав()
    {
        var ops = new SessionsOnlyOperations();
        var dispatcher = new EngineDispatcher(ops, EngineChannelKind.ReadOnly);

        EngineResponse response = await dispatcher.HandleJsonAsync(
            """{"id":"1","command":"sessions","args":{"limit":"7"}}""", clientIsAdministrator: false);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Equal(7, ops.LastLimit);
        Assert.Equal("session-1", ops.LastSessionCount);
    }

    [Fact]
    public async Task Очистка_истории_в_канале_чтения_отклоняется()
    {
        var ops = new SessionsOnlyOperations();
        var dispatcher = new EngineDispatcher(ops, EngineChannelKind.ReadOnly);

        EngineResponse response = await dispatcher.HandleJsonAsync(
            """{"id":"1","command":"sessions-clear"}""", clientIsAdministrator: true);

        Assert.False(response.Ok);
        Assert.Contains("только для чтения", response.MessageRu);
        Assert.False(ops.Cleared);
    }

    [Fact]
    public void Каталог_объявляет_историю_сессий_командами()
    {
        EngineCommandDescriptor list = EngineCommandCatalog.Get(EngineCommandKind.Sessions);
        EngineCommandDescriptor clear = EngineCommandCatalog.Get(EngineCommandKind.SessionsClear);

        Assert.Equal("sessions", list.WireName);
        Assert.False(list.NeedsElevation);
        Assert.Contains("limit", list.OptionalArgs);

        Assert.Equal("sessions-clear", clear.WireName);
        Assert.True(clear.NeedsElevation);
        Assert.Contains(EngineCommandKind.Sessions, EngineCommandCatalog.ReadOnlyChannelKinds);
        Assert.DoesNotContain(EngineCommandKind.SessionsClear, EngineCommandCatalog.ReadOnlyChannelKinds);
    }

    // ── вспомогательное ────────────────────────────────────────────────────────────────────────

    private const string OneSessionJson = """
        {"count":1,"records":[
          {"sessionId":"s1","startedUtc":"2026-10-18T12:00:00Z","stoppedUtc":null,"gameTitleRu":"Fortnite",
           "endpoint":"203.0.113.9","hasGame":true,"isMeasurable":true,"baselineRttMs":120,"finalRttMs":null,
           "improvementMs":null,"actions":[],"rollbackState":"Unknown","rollbackStateRu":"Неизвестно (сессия без итога)",
           "isCompleted":false,"statusRu":"Сессия не завершена: итог неизвестен, «после» ещё не измерено.","outcome":"NotFinished"}
        ]}
        """;

    private const string TwoSessionsJson = """
        {"count":2,"records":[
          {"sessionId":"s2","startedUtc":"2026-10-18T12:00:00Z","stoppedUtc":null,"gameTitleRu":"Fortnite",
           "endpoint":"203.0.113.9","hasGame":true,"isMeasurable":true,"baselineRttMs":130,"finalRttMs":null,
           "improvementMs":null,"actions":[],"rollbackState":"Unknown","rollbackStateRu":"Неизвестно (сессия без итога)",
           "isCompleted":false,"statusRu":"Сессия не завершена, итог неизвестен: «после» не измерено.","outcome":"NotFinished"},
          {"sessionId":"s1","startedUtc":"2026-10-17T12:00:00Z","stoppedUtc":"2026-10-17T12:20:00Z","gameTitleRu":"Fortnite",
           "endpoint":"203.0.113.9","hasGame":true,"isMeasurable":true,"baselineRttMs":120,"finalRttMs":95,
           "improvementMs":25,"actions":["Маршрут для игры"],"rollbackState":"EverythingReverted","rollbackStateRu":"Изменения сняты полностью",
           "isCompleted":true,"statusRu":"Сессия завершена: «после» измерено, свои изменения сняты.","outcome":"Completed"}
        ]}
        """;

    private static EngineResponse ClearAnswer(EngineRequest request, Action markCleared)
    {
        markCleared();
        return EngineResponse.Success(request.Id,
            "История сессий очищена: удалено записей — 1. Журнал изменений системы не тронут: он остался как был.");
    }

    private static OptimizationSessionRecord Session(
        string id, DateTime started, bool completed, double? baseline, double? final) =>
        new(
            SessionId: id,
            StartedUtc: started,
            StoppedUtc: completed ? started.AddMinutes(20) : null,
            GameTitleRu: "Fortnite",
            ProcessExeName: "FortniteClient-Win64-Shipping.exe",
            Endpoint: SameServer,
            IsMeasurable: baseline is not null,
            BaselineRttMs: baseline,
            FinalRttMs: final,
            ImprovementMs: baseline is not null && final is not null ? baseline - final : null,
            Actions: [],
            RollbackState: completed ? SessionRollbackStates.EverythingReverted : SessionRollbackStates.Unknown,
            IsCompleted: completed,
            Outcome: completed ? SessionOutcomes.Completed : SessionOutcomes.NotFinished,
            StatusNoteRu: completed ? "Сессия завершена." : "Сессия не завершена, итог неизвестен.");

    private static SessionHistoryRow Row(
        string id, string endpoint, double? baseline, double? final, bool completed, DateTime? started = null) =>
        new(
            SessionId: id,
            StartedUtc: started ?? new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
            StoppedUtc: completed ? (started ?? new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc)).AddMinutes(20) : null,
            GameTitleRu: "Fortnite",
            Endpoint: endpoint,
            HasGame: true,
            IsMeasurable: baseline is not null,
            BaselineRttMs: baseline,
            FinalRttMs: final,
            ImprovementMs: baseline is not null && final is not null ? baseline - final : null,
            Actions: [],
            RollbackStateRu: completed ? "Изменения сняты полностью" : "Неизвестно (сессия без итога)",
            IsCompleted: completed,
            StatusRu: string.Empty,
            Outcome: completed ? "Completed" : "NotFinished");

    /// <summary>Исполнитель только с историей сессий: остальное — честный отказ по умолчанию.</summary>
    private sealed class SessionsOnlyOperations : IEngineOperations
    {
        public int? LastLimit { get; private set; }

        public string? LastSessionCount { get; private set; }

        public bool Cleared { get; private set; }

        public bool IsElevationAvailable => true;

        public Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("жива"));

        public Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("список"));

        public Task<EngineOperationResult> StatusAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("спокойно"));

        public Task<EngineOperationResult> ApplyPresetAsync(TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("применено"));

        public Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("применено"));

        public Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("возвращено"));

        public Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("починено"));

        public Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("[]"));

        public Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success(start ? "запущено" : "остановлено"));

        public Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("профиль сменён"));

        public Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("прочитано"));

        public Task<EngineOperationResult> SessionsAsync(int limit, CancellationToken ct = default)
        {
            LastLimit = limit;
            LastSessionCount = "session-1";
            return Task.FromResult(EngineOperationResult.Success(
                "Сессий: 1.", """{"count":1,"records":[{"sessionId":"session-1"}]}"""));
        }

        public Task<EngineOperationResult> ClearSessionsAsync(CancellationToken ct = default)
        {
            Cleared = true;
            return Task.FromResult(EngineOperationResult.Success("История сессий очищена."));
        }
    }
}
