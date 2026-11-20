using System.Text.Json;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Личность клиента в ответе службы (C7, живая половина — на раннере через EngineSmoke).
///
/// Смысл: раньше тесты диспетчера передавали <c>clientIsAdministrator</c> литералом и проверяли
/// только логику отказа, но не то, откуда флаг берётся. Здесь флаг рождается из переданной
/// личности клиента (<see cref="EnginePeerIdentity"/>) — так же, как его на живой службе
/// заполняет <c>PipeClientInspector</c> по процессу клиента, — и проверяется, что:
///   • личность целиком доезжает до операции ping и в его данные (clientName/clientSid/clientIsAdmin);
///   • не-администратор получает отказ по меняющей команде именно из-за личности, а не литерала;
///   • администратор из личности проходит к операции;
///   • непрочитанная личность — это «не определён» и «прав нет», а не SYSTEM и не «да».
/// </summary>
public sealed class EnginePeerIdentityTests
{
    private static readonly EnginePeerIdentity AdminPeer =
        new("RUNNER\\runneradmin", "S-1-5-21-100-200-300-1001", ClientIsAdmin: true, ClientProcessId: 4242);

    private static readonly EnginePeerIdentity UserPeer =
        new("RUNNER\\pfcheck", "S-1-5-21-100-200-300-1005", ClientIsAdmin: false, ClientProcessId: 5151);

    [Fact]
    public async Task Ping_называет_клиента_по_его_собственной_личности()
    {
        var ops = new PeerRecordingOperations();
        var dispatcher = new EngineDispatcher(ops);

        EngineResponse response = await dispatcher.HandleAsync(
            new EngineRequest("peer-1", EngineCommandKind.Ping),
            peer: AdminPeer, clientIsAdministrator: true);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Equal(AdminPeer, ops.LastPeer);

        Assert.NotNull(response.DataJson);
        using JsonDocument data = JsonDocument.Parse(response.DataJson!);
        JsonElement root = data.RootElement;
        Assert.Equal("RUNNER\\runneradmin", root.GetProperty("clientName").GetString());
        Assert.Equal("S-1-5-21-100-200-300-1001", root.GetProperty("clientSid").GetString());
        Assert.True(root.GetProperty("clientIsAdmin").GetBoolean());
        Assert.Equal(4242, root.GetProperty("clientProcessId").GetInt32());

        // Главное, ради чего всё затевалось: служба не называет клиентом саму себя.
        Assert.NotEqual("NT AUTHORITY\\SYSTEM", root.GetProperty("clientName").GetString());
        Assert.NotEqual("S-1-5-18", root.GetProperty("clientSid").GetString());
    }

    [Fact]
    public async Task Ping_без_личности_честно_говорит_что_клиент_не_определён()
    {
        var ops = new PeerRecordingOperations();
        var dispatcher = new EngineDispatcher(ops);

        EngineResponse response = await dispatcher.HandleAsync(
            new EngineRequest("peer-2", EngineCommandKind.Ping), clientIsAdministrator: true);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Null(ops.LastPeer);

        Assert.NotNull(response.DataJson);
        using JsonDocument data = JsonDocument.Parse(response.DataJson!);
        JsonElement root = data.RootElement;
        Assert.Equal("не определён", root.GetProperty("clientName").GetString());
        Assert.Equal("не определён", root.GetProperty("clientSid").GetString());
        Assert.False(root.GetProperty("clientIsAdmin").GetBoolean());
    }

    [Fact]
    public async Task Меняющая_команда_от_не_администратора_отклоняется_из_личности()
    {
        var ops = new PeerRecordingOperations();
        var dispatcher = new EngineDispatcher(ops);
        string? denial = null;

        // Флаг литералом сюда НЕ передаётся: диспетчер обязан взять его из личности.
        EngineResponse response = await dispatcher.HandleAsync(
            new EngineRequest("peer-3", EngineCommandKind.ApplyTweaks,
                new Dictionary<string, string> { ["tweaks"] = "MENU_SHOW_DELAY_ZERO" }),
            onDenialRu: text => denial = text,
            peer: UserPeer, clientIsAdministrator: true);

        Assert.False(response.Ok);
        Assert.Contains("прав администратора нет", response.MessageRu);
        Assert.NotNull(denial);
        Assert.Empty(ops.Calls);
    }

    [Fact]
    public async Task Меняющая_команда_от_администратора_из_личности_доходит_до_операции()
    {
        var ops = new PeerRecordingOperations();
        var dispatcher = new EngineDispatcher(ops);

        EngineResponse response = await dispatcher.HandleAsync(
            new EngineRequest("peer-4", EngineCommandKind.ApplyTweaks,
                new Dictionary<string, string> { ["tweaks"] = "MENU_SHOW_DELAY_ZERO" }),
            peer: AdminPeer, clientIsAdministrator: true);

        Assert.True(response.Ok, response.MessageRu);
        Assert.Equal(new[] { "apply-tweaks" }, ops.Calls);
    }

    [Fact]
    public async Task Непрочитанная_личность_не_даёт_прав_администратора()
    {
        var ops = new PeerRecordingOperations();
        var dispatcher = new EngineDispatcher(ops);

        EngineResponse response = await dispatcher.HandleAsync(
            new EngineRequest("peer-5", EngineCommandKind.ApplyTweaks,
                new Dictionary<string, string> { ["tweaks"] = "MENU_SHOW_DELAY_ZERO" }),
            peer: EnginePeerIdentity.Unknown("токен процесса клиента не открылся (ошибка 5)"), clientIsAdministrator: true);

        Assert.False(response.Ok);
        Assert.Contains("прав администратора нет", response.MessageRu);
        Assert.Empty(ops.Calls);
    }

    /// <summary>Двойник: записывает пришедшую личность и вызовы меняющих операций.</summary>
    private sealed class PeerRecordingOperations : IEngineOperations
    {
        public EnginePeerIdentity? LastPeer { get; private set; }

        public List<string> Calls { get; } = [];

        public bool IsElevationAvailable => true;

        public Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default)
        {
            LastPeer = peer;
            // Данные ping собирает настоящий LiveEngineOperations; двойник повторяет его форму,
            // чтобы тест выше проверял именно поля clientName/clientSid/clientIsAdmin.
            EnginePeerIdentity client = peer ?? EnginePeerIdentity.Unknown("канал личность не передал");
            string data = JsonSerializer.Serialize(new
            {
                clientName = client.ClientName,
                clientSid = client.ClientSid,
                clientIsAdmin = client.ClientIsAdmin,
                clientProcessId = client.ClientProcessId,
            }, EngineJson.Options);
            return Task.FromResult(EngineOperationResult.Success("Служба жива.", data));
        }

        public Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default)
        {
            Calls.Add("apply-tweaks");
            return Task.FromResult(EngineOperationResult.Success("Применено."));
        }

        public Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Список команд."));

        public Task<EngineOperationResult> StatusAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Спокойно."));

        public Task<EngineOperationResult> ApplyPresetAsync(PingForge.Core.Models.TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Пресет применён."));

        public Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Возвращено."));

        public Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Починено."));

        public Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Журнал."));

        public Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Готово."));

        public Task<EngineOperationResult> SetPerfProfileAsync(PingForge.Orchestration.Realtime.PerfProfile profile, CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Профиль сменён."));

        public Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default) =>
            Task.FromResult(EngineOperationResult.Success("Прочитано."));
    }

    [Fact]
    public async Task Без_личности_клиента_права_по_умолчанию_не_выдаются()
    {
        // Умолчание «администратор: да» было удобством проверок и опасностью в бою: любой новый
        // вызов без личности клиента молча получал право менять Windows от имени системы.
        // Теперь забыть про права можно только в сторону отказа.
        var dispatcher = new EngineDispatcher(new PeerRecordingOperations());

        EngineResponse response = await dispatcher.HandleJsonAsync(
            """{"id":"1","command":"apply-preset","args":{"preset":"low-latency"}}""");

        Assert.False(response.Ok);
        Assert.Contains("только для администратора", response.MessageRu, StringComparison.Ordinal);
    }
}
