using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Канал службы только для чтения (C2).
///
/// Зачем это проверять отдельно. Канал, который умеет менять настройки Windows, не должен быть
/// доступен каждому залогиненному пользователю — но и лишать обычное окно просмотра нельзя. Поэтому
/// у службы два канала: основной (LocalSystem и Администраторы, все команды) и канал чтения
/// (плюс интерактивный вход, но команды только просмотр). Здесь проверяется, что канал чтения
/// не превращается в лазейку: меняющие команды в нём не выполняются ДАЖЕ для администратора —
/// изменения обязаны проходить через основной канал со всеми проверками прав.
/// </summary>
public sealed class ReadOnlyChannelTests
{
    private static EngineDispatcher ReadOnlyDispatcher(RecordingEngineOperations? ops = null) =>
        new(ops ?? new RecordingEngineOperations(), EngineChannelKind.ReadOnly);

    /// <summary>Команды, которые меняют систему или её настройки: в канале чтения им нет места.</summary>
    public static TheoryData<string, string> ЗакрытыеКоманды => new()
    {
        { "apply-preset", """{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""" },
        { "apply-tweaks", """{"id":"2","command":"apply-tweaks","args":{"tweaks":"TCP_NO_DELAY"}}""" },
        { "rollback", """{"id":"3","command":"rollback"}""" },
        { "repair-network", """{"id":"4","command":"repair-network"}""" },
        { "realtime-start", """{"id":"5","command":"realtime-start"}""" },
        { "perf-profile", """{"id":"6","command":"perf-profile","args":{"profile":"economy"}}""" },
    };

    [Theory]
    [MemberData(nameof(ЗакрытыеКоманды))]
    public async Task Канал_чтения_отклоняет_меняющие_команды_даже_от_администратора(string wire, string json)
    {
        var ops = new RecordingEngineOperations();

        EngineResponse response = await ReadOnlyDispatcher(ops)
            .HandleJsonAsync(json, clientIsAdministrator: true);

        Assert.False(response.Ok, $"команда «{wire}» обязана получить отказ в канале чтения");

        // Отказ объясняет три вещи: где проблема, что доступно и что делать.
        Assert.Contains("только для чтения", response.MessageRu);
        Assert.Contains(wire, response.MessageRu);
        Assert.Contains("от имени администратора", response.MessageRu);

        // И главное: до операции дело не дошло — служба ничего не меняла.
        Assert.Empty(ops.Calls);
    }

    [Theory]
    [MemberData(nameof(ЗакрытыеКоманды))]
    public async Task Отказ_по_каналу_виден_в_журнале_службы(string wire, string json)
    {
        var journal = new List<string>();

        await ReadOnlyDispatcher()
            .HandleJsonAsync(json, clientIsAdministrator: true, onDenialRu: journal.Add);

        Assert.Contains(journal, line => line.Contains(wire) && line.Contains("только для чтения"));
    }

    [Fact]
    public async Task Канал_чтения_отвечает_на_просмотр_без_всяких_прав()
    {
        var ops = new RecordingEngineOperations();
        EngineDispatcher dispatcher = ReadOnlyDispatcher(ops);

        foreach (string json in new[]
        {
            """{"id":"1","command":"ping"}""",
            """{"id":"2","command":"capabilities"}""",
            """{"id":"3","command":"status"}""",
            """{"id":"4","command":"journal-tail","args":{"limit":"5"}}""",
            """{"id":"5","command":"system-state"}""",
        })
        {
            EngineResponse response = await dispatcher.HandleJsonAsync(json, clientIsAdministrator: false);

            Assert.True(response.Ok, $"просмотр «{json}» обязан работать в канале чтения без прав: {response.MessageRu}");
        }

        Assert.Equal(5, ops.Calls.Count);
    }

    [Fact]
    public async Task Остановка_своих_изменений_доступна_в_канале_чтения_без_прав()
    {
        // «Убрать за собой» — не изменение системы, а возврат к прежнему: это должно работать без прав,
        // иначе человек запустил оптимизацию от администратора и не может её снять.
        var ops = new RecordingEngineOperations();

        EngineResponse response = await ReadOnlyDispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"realtime-stop"}""", clientIsAdministrator: false);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Contains(ops.Calls, call => call == "realtime-stop:False");
    }

    [Fact]
    public async Task Следы_перед_удалением_видно_в_канале_чтения_без_прав()
    {
        // Задача T: скрипт снятия спрашивает службу ДО удаления. Ответ должен приходить и тогда,
        // когда администратор не в деле, — иначе удаление «не видит» следы и молча их бросает.
        var ops = new RecordingEngineOperations();

        EngineResponse response = await ReadOnlyDispatcher(ops)
            .HandleJsonAsync("""{"id":"1","command":"uninstall-check"}""", clientIsAdministrator: false);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Contains("удалять спокойно", response.MessageRu);
        Assert.Contains(ops.Calls, call => call == "uninstall-check");
    }

    [Fact]
    public async Task Основной_канал_работает_как_прежде()
    {
        // Сторож на то, что сужение канала чтения не задело основной: там меняющие команды выполняются
        // для клиента с правами — ровно как проверяет C1.
        var ops = new RecordingEngineOperations();
        var dispatcher = new EngineDispatcher(ops, EngineChannelKind.Main);

        EngineResponse response = await dispatcher.HandleJsonAsync(
            """{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""",
            clientIsAdministrator: true);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Contains(ops.Calls, call => call.StartsWith("apply-preset", StringComparison.Ordinal));
    }

    [Fact]
    public void Список_команд_канала_чтения_считается_из_одного_места()
    {
        // Имена берутся из таблицы команд, а не пишутся вторым списком: переименование команды
        // не должно разорвать канал чтения молча.
        IReadOnlyList<string> wireNames = EngineCommandCatalog.ReadOnlyChannelWireNames;

        Assert.Equal(wireNames.Count, wireNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(wireNames, name => Assert.NotNull(EngineCommandCatalog.Find(name)));
        Assert.Equal("ping, capabilities, status, journal-tail, system-state, route-discover, realtime-stop, uninstall-check",
            EngineCommandCatalog.ReadOnlyChannelWireNamesRu());
    }

    [Fact]
    public void Список_команд_канала_чтения_совпадает_с_этим_документом()
    {
        // Документы и код расходятся молча и незаметно: человек читает «доступно то-то», а служба
        // отвечает иначе. Поэтому список в docs/КАНАЛЫ-СЛУЖБЫ.md сверяется с кодом вот здесь.
        string doc = CheckedFile("pipes-doc.md");

        Assert.Contains("Команды канала только для чтения", doc);

        string block = ExtractFencedBlockAfter(doc, "Команды канала только для чтения");
        string[] fromDoc = block
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#') && !line.StartsWith("```"))
            .ToArray();

        Assert.Equal(EngineCommandCatalog.ReadOnlyChannelWireNames, fromDoc);
    }

    private static string CheckedFile(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
        Assert.True(File.Exists(path), $"нет файла для проверки: {path} (он копируется в вывод теста из PingForge.Tests.csproj)");
        return File.ReadAllText(path);
    }

    private static string ExtractFencedBlockAfter(string text, string marker)
    {
        int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"в документе нет метки «{marker}»");

        int fenceStart = text.IndexOf("```", markerIndex, StringComparison.Ordinal);
        Assert.True(fenceStart >= 0, $"после метки «{marker}» нет блока с перечнем команд");

        int fenceEnd = text.IndexOf("```", fenceStart + 3, StringComparison.Ordinal);
        Assert.True(fenceEnd > fenceStart, "блок с перечнем команд не закрыт");

        return text[(fenceStart + 3)..fenceEnd];
    }

    /// <summary>Двойник операций: записывает вызовы, чтобы проверить, что до них дело не дошло.</summary>
    private sealed class RecordingEngineOperations : IEngineOperations
    {
        public List<string> Calls { get; } = [];

        public bool IsElevationAvailable => true;

        public Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default) =>
            Record("ping", EngineOperationResult.Success("жива"));

        public Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default) =>
            Record("capabilities", EngineOperationResult.Success("список", EngineCommandCatalog.DescribeRu()));

        public Task<EngineOperationResult> StatusAsync(CancellationToken ct = default) =>
            Record("status", EngineOperationResult.Success("спокойно", """{"optimizing":false}"""));

        public Task<EngineOperationResult> ApplyPresetAsync(TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default) =>
            Record($"apply-preset:{preset}", EngineOperationResult.Success("применено"));

        public Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default) =>
            Record("apply-tweaks", EngineOperationResult.Success("применено"));

        public Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default) =>
            Record("rollback", EngineOperationResult.Success("возвращено"));

        public Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default) =>
            Record("repair-network", EngineOperationResult.Success("починено"));

        public Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default) =>
            Record("journal-tail", EngineOperationResult.Success("[]"));

        public Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default) =>
            Record($"realtime-stop:{start}", EngineOperationResult.Success(start ? "запущено" : "остановлено"));

        public Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default) =>
            Record($"perf-profile:{profile}", EngineOperationResult.Success("профиль сменён"));

        public Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default) =>
            Record("system-state", EngineOperationResult.Success("прочитано"));

        public Task<EngineOperationResult> UninstallCheckAsync(CancellationToken ct = default) =>
            Record("uninstall-check", EngineOperationResult.Success(
                "Применённых изменений нет — можно удалять спокойно.",
                """{"appliedChanges":0,"openOperations":0,"snapshots":0,"optimizationRunning":false,"mustAsk":false,"optionsRu":[],"questionRu":"Применённых изменений нет.","warningRu":""}"""));

        private Task<EngineOperationResult> Record(string call, EngineOperationResult result)
        {
            Calls.Add(call);
            return Task.FromResult(result);
        }
    }
}
