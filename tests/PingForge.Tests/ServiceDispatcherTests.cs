using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Разборщик команд службы. Проверяется на двойнике <see cref="IEngineOperations"/>: интересна не работа
/// твиков (она проверена отдельно), а то, что служба правильно понимает запрос и честно отвечает,
/// когда выполнить его нельзя.
/// </summary>
public class ServiceDispatcherTests
{
    private static EngineDispatcher Dispatcher(FakeEngineOperations ops) => new(ops);

    private static EngineDispatcher Dispatcher() => new(new FakeEngineOperations());

    // ── простые команды ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_отвечает_и_возвращает_тот_же_номер_запроса()
    {
        EngineResponse response = await Dispatcher().HandleJsonAsync("""{"id":"42","command":"ping"}""");

        Assert.True(response.Ok);
        Assert.Equal("42", response.Id);
        Assert.Contains("жива", response.MessageRu);
    }

    [Fact]
    public async Task Capabilities_отдаёт_описание_команд_в_данных()
    {
        EngineResponse response = await Dispatcher().HandleJsonAsync("""{"id":"1","command":"capabilities"}""");

        Assert.True(response.Ok);
        Assert.NotNull(response.DataJson);
        Assert.Contains("repair-network", response.DataJson);
    }

    [Fact]
    public async Task Статус_и_журнал_доступны_без_прав_администратора()
    {
        var ops = new FakeEngineOperations { IsElevationAvailable = false };
        var dispatcher = Dispatcher(ops);

        EngineResponse status = await dispatcher.HandleJsonAsync("""{"id":"1","command":"status"}""");
        EngineResponse journal = await dispatcher.HandleJsonAsync("""{"id":"2","command":"journal-tail"}""");

        Assert.True(status.Ok);
        Assert.True(journal.Ok);
        Assert.Equal(EngineDispatcher.DefaultJournalLimit, ops.LastJournalLimit);
    }

    [Fact]
    public async Task Чтение_состояния_доступно_без_прав_и_ничего_не_меняет()
    {
        var ops = new FakeEngineOperations { IsElevationAvailable = false };

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"12","command":"system-state"}""");

        Assert.True(response.Ok);
        Assert.True(ops.ReadSystemStateCalled);
        Assert.Contains("26100", response.MessageRu);
        Assert.Contains("tcpAutoTuningLevel", response.DataJson);
    }

    // ── права ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Без_прав_команда_применения_твиков_не_выполняется_и_объясняет_почему()
    {
        var ops = new FakeEngineOperations { IsElevationAvailable = false };

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""");

        Assert.False(response.Ok);
        Assert.Contains("администратора", response.MessageRu);
        Assert.Contains("apply-preset", response.MessageRu);
        Assert.Empty(ops.AppliedPresets);
    }

    [Fact]
    public async Task Остановка_оптимизации_разрешена_без_прав_а_запуск_нет()
    {
        var ops = new FakeEngineOperations { IsElevationAvailable = false };
        var dispatcher = Dispatcher(ops);

        EngineResponse stop = await dispatcher.HandleJsonAsync("""{"id":"1","command":"realtime-stop"}""");
        EngineResponse start = await dispatcher.HandleJsonAsync("""{"id":"2","command":"realtime-start"}""");

        Assert.True(stop.Ok);
        Assert.False(start.Ok);
        Assert.False(ops.RealtimeRunning);
    }

    // ── пресеты ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Пресет_без_аргумента_отклоняется_с_примером()
    {
        EngineResponse response = await Dispatcher().HandleJsonAsync("""{"id":"1","command":"apply-preset"}""");

        Assert.False(response.Ok);
        Assert.Contains("preset", response.MessageRu);
        Assert.Contains("low-latency", response.MessageRu);
    }

    [Fact]
    public async Task Неизвестный_пресет_отклоняется_со_списком_доступных()
    {
        EngineResponse response = await Dispatcher()
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"турбо"}}""");

        Assert.False(response.Ok);
        Assert.Contains("турбо", response.MessageRu);
        Assert.Contains("low-latency", response.MessageRu);
        Assert.Contains("max-speed", response.MessageRu);
        Assert.Contains("fps", response.MessageRu);
    }

    [Fact]
    public async Task Откат_и_свой_набор_не_применяются_как_пресеты_и_подсказывают_верную_команду()
    {
        var dispatcher = Dispatcher();

        EngineResponse rollback = await dispatcher
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"from-snapshot"}}""");
        EngineResponse custom = await dispatcher
            .HandleJsonAsync("""{"id":"2","command":"apply-preset","args":{"preset":"custom"}}""");

        Assert.False(rollback.Ok);
        Assert.Contains("rollback", rollback.MessageRu);
        Assert.False(custom.Ok);
        Assert.Contains("apply-tweaks", custom.MessageRu);
    }

    [Fact]
    public async Task Пресет_доходит_до_операции_вместе_с_просьбой_о_точке_восстановления()
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"fps","restorePoint":"true"}}""");

        Assert.True(response.Ok);
        (TweakProfilePreset preset, bool restorePoint) = Assert.Single(ops.AppliedPresets);
        Assert.Equal(TweakProfilePreset.FpsAndResponsiveness, preset);
        Assert.True(restorePoint);
    }

    [Fact]
    public async Task Точку_восстановления_можно_не_просить()
    {
        var ops = new FakeEngineOperations();

        await Dispatcher(ops).HandleJsonAsync(
            """{"id":"1","command":"apply-preset","args":{"preset":"low-latency","restorePoint":"false"}}""");

        Assert.False(Assert.Single(ops.AppliedPresets).RestorePoint);
    }

    // ── выбранные твики ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Список_твиков_доходит_очищенным_от_повторов()
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops).HandleJsonAsync(
            """{"id":"1","command":"apply-tweaks","args":{"tweaks":"TCP_NO_DELAY, TCP_NO_DELAY, GAME_MODE_ON"}}""");

        Assert.True(response.Ok);
        Assert.Equal(["TCP_NO_DELAY", "GAME_MODE_ON"], Assert.Single(ops.AppliedTweakSets));
    }

    [Fact]
    public async Task Пустой_список_твиков_это_отказ_а_не_применение_всего()
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"apply-tweaks","args":{"tweaks":" , , "}}""");

        Assert.False(response.Ok);
        Assert.Contains("ни одного твика", response.MessageRu);
        Assert.Empty(ops.AppliedTweakSets);
    }

    [Fact]
    public async Task Слишком_длинный_список_твиков_отклоняется_с_числами()
    {
        string many = string.Join(',', Enumerable.Range(0, EngineDispatcher.MaxTweaksPerRequest + 5).Select(i => $"T{i}"));

        // Склейка, а не шаблон: в JSON слишком много фигурных скобок, и интерполяция в них путается.
        string json = "{\"id\":\"1\",\"command\":\"apply-tweaks\",\"args\":{\"tweaks\":\"" + many + "\"}}";
        EngineResponse response = await Dispatcher().HandleJsonAsync(json);

        Assert.False(response.Ok);
        Assert.Contains((EngineDispatcher.MaxTweaksPerRequest + 5).ToString(), response.MessageRu);
    }

    // ── откат, журнал, профиль ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Откат_без_номера_снапшота_берёт_последний()
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops).HandleJsonAsync("""{"id":"1","command":"rollback"}""");

        Assert.True(response.Ok);
        Assert.True(ops.RollbackCalled);
        Assert.Null(ops.LastSnapshotId);
    }

    [Fact]
    public async Task Откат_с_номером_снапшота_передаёт_номер()
    {
        var ops = new FakeEngineOperations();

        await Dispatcher(ops).HandleJsonAsync("""{"id":"1","command":"rollback","args":{"snapshot":"snap-007"}}""");

        Assert.Equal("snap-007", ops.LastSnapshotId);
    }

    [Fact]
    public async Task Предел_записей_журнала_не_превышается_и_объясняется()
    {
        EngineResponse response = await Dispatcher()
            .HandleJsonAsync("""{"id":"1","command":"journal-tail","args":{"limit":"5000"}}""");

        Assert.False(response.Ok);
        Assert.Contains("5000", response.MessageRu);
        Assert.Contains(EngineDispatcher.MaxJournalLimit.ToString(), response.MessageRu);
    }

    [Fact]
    public async Task Запрошенное_число_записей_журнала_доходит_до_операции()
    {
        var ops = new FakeEngineOperations();

        await Dispatcher(ops).HandleJsonAsync("""{"id":"1","command":"journal-tail","args":{"limit":"50"}}""");

        Assert.Equal(50, ops.LastJournalLimit);
    }

    [Fact]
    public async Task Неизвестный_профиль_потребления_отклоняется_со_списком()
    {
        EngineResponse response = await Dispatcher()
            .HandleJsonAsync("""{"id":"1","command":"perf-profile","args":{"profile":"турбо"}}""");

        Assert.False(response.Ok);
        Assert.Contains("economy", response.MessageRu);
        Assert.Contains("balanced", response.MessageRu);
        Assert.Contains("max", response.MessageRu);
    }

    [Fact]
    public async Task Профиль_потребления_доходит_до_операции()
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"perf-profile","args":{"profile":"economy"}}""");

        Assert.True(response.Ok);
        Assert.Equal(PerfProfile.Economy, ops.LastProfile);
    }

    // ── поведение при сбоях ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ошибка_внутри_операции_превращается_в_ответ_а_не_в_исключение()
    {
        var ops = new FakeEngineOperations { ThrowOnStatus = new InvalidOperationException("реестр недоступен") };

        EngineResponse response = await Dispatcher(ops).HandleJsonAsync("""{"id":"1","command":"status"}""");

        Assert.False(response.Ok);
        Assert.Contains("реестр недоступен", response.MessageRu);
        Assert.Contains("status", response.MessageRu);
    }

    [Fact]
    public async Task Отмена_объясняется_пользователю_без_кода_отмены()
    {
        var ops = new FakeEngineOperations { ThrowOnStatus = new OperationCanceledException() };

        EngineResponse response = await Dispatcher(ops).HandleJsonAsync("""{"id":"1","command":"status"}""");

        Assert.False(response.Ok);
        Assert.Contains("прервана", response.MessageRu);
        Assert.DoesNotContain("OperationCanceled", response.MessageRu);
    }

    [Fact]
    public async Task Битый_запрос_получает_ответ_с_пустым_номером()
    {
        EngineResponse response = await Dispatcher().HandleJsonAsync("не json");

        Assert.False(response.Ok);
        Assert.Equal(string.Empty, response.Id);
    }

    [Fact]
    public async Task Каждый_ответ_несёт_номер_своего_запроса()
    {
        var dispatcher = Dispatcher();

        foreach (string id in new[] { "a", "b-2", "003" })
        {
            EngineResponse response = await dispatcher.HandleJsonAsync($$"""{"id":"{{id}}","command":"ping"}""");
            Assert.Equal(id, response.Id);
        }
    }

    [Fact]
    public async Task Данные_операции_передаются_клиенту_как_есть()
    {
        var ops = new FakeEngineOperations { StatusDataJson = """{"optimizing":false,"probes":360}""" };

        EngineResponse response = await Dispatcher(ops).HandleJsonAsync("""{"id":"1","command":"status"}""");

        Assert.Equal("""{"optimizing":false,"probes":360}""", response.DataJson);
    }

    // ── права клиента канала (C1: служба от LocalSystem не делает администратором того, кто подключился) ──

    /// <summary>Команды, которые меняют систему: каждой из них клиент без прав обязан получить отказ.</summary>
    public static TheoryData<string, string> МеняющиеКоманды => new()
    {
        { "apply-preset", """{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""" },
        { "apply-tweaks", """{"id":"2","command":"apply-tweaks","args":{"tweaks":"TCP_NO_DELAY"}}""" },
        { "rollback", """{"id":"3","command":"rollback"}""" },
        { "repair-network", """{"id":"4","command":"repair-network"}""" },
        { "realtime-start", """{"id":"5","command":"realtime-start"}""" },
        { "route-measure", """{"id":"6","command":"route-measure"}""" },
        { "sessions-clear", """{"id":"7","command":"sessions-clear"}""" },
    };

    [Theory]
    [MemberData(nameof(МеняющиеКоманды))]
    public async Task Клиент_без_прав_администратора_получает_отказ_а_служба_ничего_не_выполняет(string wire, string json)
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops).HandleJsonAsync(json, clientIsAdministrator: false);

        Assert.False(response.Ok);
        Assert.Contains("администратора", response.MessageRu);
        Assert.Contains(wire, response.MessageRu);

        // Главное: операция не вызвана вовсе. Отказ «на словах» при выполненном изменении был бы хуже дыры.
        Assert.Empty(ops.AppliedPresets);
        Assert.Empty(ops.AppliedTweakSets);
        Assert.False(ops.RollbackCalled);
        Assert.Equal(0, ops.RepairNetworkCalls);
        Assert.False(ops.RealtimeRunning);
    }

    [Fact]
    public async Task Отказ_по_правам_клиента_объясняет_что_права_службы_здесь_не_помогают()
    {
        // Служба идёт от LocalSystem: её права есть всегда. Раньше проверялись только они —
        // и любой пользователь компьютера мог менять Windows через канал без единого запроса UAC.
        var ops = new FakeEngineOperations { IsElevationAvailable = true };

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""", clientIsAdministrator: false);

        Assert.False(response.Ok);
        Assert.Contains("от имени системы", response.MessageRu);
        Assert.Contains("ваша учётная запись", response.MessageRu);
        Assert.Contains("от имени администратора", response.MessageRu);
        Assert.Empty(ops.AppliedPresets);
    }

    [Fact]
    public async Task Чтение_и_остановка_своих_изменений_работают_у_клиента_без_прав()
    {
        var ops = new FakeEngineOperations();
        var dispatcher = Dispatcher(ops);

        EngineResponse ping = await dispatcher.HandleJsonAsync("""{"id":"1","command":"ping"}""", clientIsAdministrator: false);
        EngineResponse capabilities = await dispatcher.HandleJsonAsync("""{"id":"2","command":"capabilities"}""", clientIsAdministrator: false);
        EngineResponse status = await dispatcher.HandleJsonAsync("""{"id":"3","command":"status"}""", clientIsAdministrator: false);
        EngineResponse journal = await dispatcher.HandleJsonAsync("""{"id":"4","command":"journal-tail"}""", clientIsAdministrator: false);
        EngineResponse state = await dispatcher.HandleJsonAsync("""{"id":"5","command":"system-state"}""", clientIsAdministrator: false);
        EngineResponse stop = await dispatcher.HandleJsonAsync("""{"id":"6","command":"realtime-stop"}""", clientIsAdministrator: false);

        Assert.True(ping.Ok);
        Assert.True(capabilities.Ok);
        Assert.True(status.Ok);
        Assert.True(journal.Ok);
        Assert.True(state.Ok);

        // «Вернуть как было» нельзя отбирать у обычного пользователя: иначе снять изменения
        // сможет только тот, кто их и не делал.
        Assert.True(stop.Ok);
        Assert.False(ops.RealtimeRunning);
    }

    [Fact]
    public async Task Клиент_с_правами_получает_прежнее_поведение()
    {
        var ops = new FakeEngineOperations();

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""", clientIsAdministrator: true);

        Assert.True(response.Ok);
        Assert.Single(ops.AppliedPresets);
    }

    [Fact]
    public async Task Права_службы_проверяются_отдельно_от_прав_клиента()
    {
        // Клиент — администратор, а у службы прав нет (её запустили руками без повышения).
        // Это другой отказ: про службу, а не про учётную запись пользователя.
        var ops = new FakeEngineOperations { IsElevationAvailable = false };

        EngineResponse response = await Dispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""", clientIsAdministrator: true);

        Assert.False(response.Ok);
        Assert.Contains("у службы PingForge", response.MessageRu);
        Assert.DoesNotContain("ваша учётная запись", response.MessageRu);
        Assert.Empty(ops.AppliedPresets);
    }

    [Fact]
    public async Task Отказ_по_правам_виден_в_журнале_службы()
    {
        var denials = new List<string>();

        EngineResponse response = await Dispatcher()
            .HandleJsonAsync(
                """{"id":"1","command":"repair-network"}""",
                clientIsAdministrator: false,
                onDenialRu: denials.Add);

        Assert.False(response.Ok);
        string logged = Assert.Single(denials);
        Assert.Contains("repair-network", logged);
    }

    [Fact]
    public async Task Отказа_по_правам_нет_в_журнале_когда_команда_разрешена()
    {
        var denials = new List<string>();

        await Dispatcher().HandleJsonAsync("""{"id":"1","command":"status"}""", clientIsAdministrator: false, onDenialRu: denials.Add);

        Assert.Empty(denials);
    }

    [Fact]
    public void Список_меняющих_команд_в_тесте_совпадает_с_таблицей_службы()
    {
        // Сторож: добавят новую команду, меняющую систему, — тест напомнит проверить её права.
        string[] expected = EngineCommandCatalog.All
            .Where(item => item.NeedsElevation)
            .Select(item => item.WireName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] covered = МеняющиеКоманды
            .Select(row => (string)row[0])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, covered);
    }

    /// <summary>Двойник операций: запоминает, что у него просили, и умеет падать по заказу теста.</summary>
    private sealed class FakeEngineOperations : IEngineOperations
    {
        public bool IsElevationAvailable { get; set; } = true;

        public List<(TweakProfilePreset Preset, bool RestorePoint)> AppliedPresets { get; } = [];

        public List<IReadOnlyList<string>> AppliedTweakSets { get; } = [];

        public bool RollbackCalled { get; private set; }

        public int RepairNetworkCalls { get; private set; }

        public string? LastSnapshotId { get; private set; }

        public int LastJournalLimit { get; private set; }

        public PerfProfile LastProfile { get; private set; } = PerfProfile.Balanced;

        public bool RealtimeRunning { get; private set; }

        public string StatusDataJson { get; set; } = """{"optimizing":false}""";

        public Exception? ThrowOnStatus { get; set; }

        public EnginePeerIdentity? LastPeer { get; private set; }

        public Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default)
        {
            LastPeer = peer;
            return Task.FromResult(EngineOperationResult.Success(
                $"Служба жива, версия протокола {EngineProtocol.Version}."));
        }

        public Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Список команд.", EngineCommandCatalog.DescribeRu()));

        public Task<EngineOperationResult> StatusAsync(CancellationToken ct = default) =>
            ThrowOnStatus is not null
                ? Task.FromException<EngineOperationResult>(ThrowOnStatus)
                : Task.FromResult(EngineOperationResult.Success("Оптимизация не запущена.", StatusDataJson));

        public Task<EngineOperationResult> ApplyPresetAsync(TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default)
        {
            AppliedPresets.Add((preset, requestRestorePoint));
            return Task.FromResult(EngineOperationResult.Success($"Пресет «{EngineNames.PresetTitleRu(preset)}» применён."));
        }

        public Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default)
        {
            AppliedTweakSets.Add(tweakCodes);
            return Task.FromResult(EngineOperationResult.Success($"Применено твиков: {tweakCodes.Count}."));
        }

        public Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default)
        {
            RollbackCalled = true;
            LastSnapshotId = snapshotId;
            return Task.FromResult(EngineOperationResult.Success("Прежние настройки возвращены."));
        }

        public Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default)
        {
            RepairNetworkCalls++;
            return Task.FromResult(EngineOperationResult.Success("Остатков после аварии не найдено."));
        }

        public Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default)
        {
            LastJournalLimit = limit;
            return Task.FromResult(EngineOperationResult.Success($"Последние {limit} записей.", "[]"));
        }

        public Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default)
        {
            RealtimeRunning = start;
            return Task.FromResult(EngineOperationResult.Success(start ? "Оптимизация запущена." : "Оптимизация остановлена."));
        }

        public List<(string TweakCode, string? SnapshotId)> SingleRollbacks { get; } = [];

        public Task<EngineOperationResult> RollbackSingleTweakAsync(string tweakCode, string? snapshotId, CancellationToken ct = default)
        {
            SingleRollbacks.Add((tweakCode, snapshotId));
            return Task.FromResult(EngineOperationResult.Success($"Твик {tweakCode} возвращён."));
        }

        public Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default)
        {
            LastProfile = profile;
            return Task.FromResult(EngineOperationResult.Success($"Профиль потребления: {EngineNames.ProfileWireName(profile)}."));
        }

        public bool ReadSystemStateCalled { get; private set; }

        public Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default)
        {
            ReadSystemStateCalled = true;
            return Task.FromResult(EngineOperationResult.Success(
                "Windows 11 Pro 26100. права администратора есть.",
                """{"tcpAutoTuningLevel":"normal"}"""));
        }
    }
}
