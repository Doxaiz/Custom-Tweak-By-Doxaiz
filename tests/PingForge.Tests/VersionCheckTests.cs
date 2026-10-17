using PingForge.App.ViewModels;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сверка версий окна и службы (C5).
///
/// Окно и служба ставятся одной сборкой, но живут отдельно: после частичного обновления служба может
/// остаться старой. Молча работать с такой нельзя — окно и служба говорят на одном протоколе только
/// пока их версии подходят друг другу. Три исхода, и все три проверяются здесь:
///   • версии совпали — работаем как обычно;
///   • протоколы разные — меняющие команды НЕ отправляются вовсе, человек видит обе версии и совет
///     «поставьте установщик заново»;
///   • протокол один, а версии программы различаются — предупреждаем, но работаем.
/// </summary>
public sealed class VersionCheckTests
{
    private sealed class ScriptedChannel : IEngineChannel
    {
        private readonly Func<EngineRequest, EngineResponse> _responder;

        public ScriptedChannel(Func<EngineRequest, EngineResponse> responder) => _responder = responder;

        public List<EngineRequest> Requests { get; } = [];

        public Task<EngineResponse> SendAsync(EngineRequest request, TimeSpan timeout, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }

        public void Dispose()
        {
        }
    }

    private static EngineResponse VersionPing(EngineRequest request, string appVersion, int protocolVersion) =>
        request.Command == EngineCommandKind.Ping
            ? EngineResponse.Success(
                request.Id,
                "Служба жива.",
                $$"""{"product":"Custom Tweak By Doxaiz","appVersion":"{{appVersion}}","protocolVersion":{{protocolVersion}}}""")
            : EngineResponse.Success(request.Id, "Готово.");

    [Fact]
    public async Task При_разном_протоколе_меняющая_команда_не_уходит_вовсе()
    {
        var channel = new ScriptedChannel(request => VersionPing(request, "9.9.9", protocolVersion: 999));
        var client = new EngineClient(channel);

        EngineResponse response = await client.SendAsync(
            EngineCommandKind.ApplyPreset,
            new Dictionary<string, string> { ["preset"] = "low-latency" });

        Assert.False(response.Ok, "меняющая команда при чужом протоколе не должна уходить службе");

        // Обе версии названы прямо: человек должен понимать, что не так, а не гадать.
        Assert.Contains("999", response.MessageRu);
        Assert.Contains(EngineClient.SelfProtocolVersion.ToString(), response.MessageRu);
        Assert.Contains("поставьте установщик заново", response.MessageRu);

        // До службы дошёл только проверочный ping: команда не была отправлена.
        Assert.All(channel.Requests, request => Assert.Equal(EngineCommandKind.Ping, request.Command));
        Assert.Equal(EngineVersionState.ProtocolMismatch, client.VersionState);
    }

    [Fact]
    public async Task При_разном_протоколе_просмотр_продолжает_работать()
    {
        var channel = new ScriptedChannel(request => VersionPing(request, "9.9.9", protocolVersion: 999));
        var client = new EngineClient(channel);

        EngineResponse status = await client.SendAsync(EngineCommandKind.Status);
        EngineResponse stop = await client.SendAsync(EngineCommandKind.RealtimeStop);

        Assert.True(status.Ok, "просмотр обязан работать всегда: он ничего не меняет");
        Assert.True(stop.Ok, "«убрать за собой» обязано работать даже при чужом протоколе");
    }

    [Fact]
    public async Task Разные_версии_программы_предупреждают_но_не_блокируют()
    {
        var channel = new ScriptedChannel(request => VersionPing(request, "0.0.1", EngineClient.SelfProtocolVersion));
        var client = new EngineClient(channel);

        EngineResponse response = await client.SendAsync(
            EngineCommandKind.ApplyPreset,
            new Dictionary<string, string> { ["preset"] = "low-latency" });

        Assert.True(response.Ok, "протокол один — команда работает, а расхождение версий это предупреждение");
        Assert.Contains(channel.Requests, request => request.Command == EngineCommandKind.ApplyPreset);
        Assert.Equal(EngineVersionState.AppDiffers, client.VersionState);

        // Строка идёт в «О программе»: обе версии на виду, и совет — не приказ.
        Assert.Contains("0.0.1", client.VersionsNoteRu);
        Assert.Contains(EngineClient.SelfAppVersion, client.VersionsNoteRu);
        Assert.Contains("различаются", client.VersionsNoteRu);
    }

    [Fact]
    public async Task Совпадение_версий_работает_как_обычно()
    {
        var channel = new ScriptedChannel(request =>
            VersionPing(request, EngineClient.SelfAppVersion, EngineClient.SelfProtocolVersion));
        var client = new EngineClient(channel);

        EngineResponse response = await client.SendAsync(
            EngineCommandKind.ApplyPreset,
            new Dictionary<string, string> { ["preset"] = "low-latency" });

        Assert.True(response.Ok);
        Assert.Equal(EngineVersionState.Match, client.VersionState);
        Assert.Contains("одной сборки", client.VersionsNoteRu);
        Assert.Contains(EngineClient.SelfAppVersion, client.VersionsNoteRu);
    }

    [Fact]
    public async Task Версии_сверяются_один_раз_перед_первой_меняющей_командой()
    {
        var channel = new ScriptedChannel(request => VersionPing(request, EngineClient.SelfAppVersion, EngineClient.SelfProtocolVersion));
        var client = new EngineClient(channel);

        await client.SendAsync(EngineCommandKind.RepairNetwork);
        await client.SendAsync(EngineCommandKind.RepairNetwork);
        await client.SendAsync(EngineCommandKind.RepairNetwork);

        // Проверочный ping — только перед первой меняющей командой: спрашивать заново перед каждым
        // нажатием — это лишний разговор, а не забота.
        Assert.Equal(1, channel.Requests.Count(request => request.Command == EngineCommandKind.Ping));
    }

    [Fact]
    public async Task Без_ответа_о_версиях_окно_не_выдумывает_совпадение()
    {
        // Служба старая и не называет версий — это «неизвестно», а не «совпало». Меняющая команда
        // в этом случае уходит: запрещать работу без данных — тоже вранье, просто другое.
        var channel = new ScriptedChannel(request => EngineResponse.Success(request.Id, "Готово."));
        var client = new EngineClient(channel);

        EngineResponse response = await client.SendAsync(EngineCommandKind.RepairNetwork);

        Assert.True(response.Ok);
        Assert.Equal(EngineVersionState.Unknown, client.VersionState);
        Assert.Contains("ещё не проверена", client.VersionsNoteRu);
    }

    [Fact]
    public async Task Окно_предупреждает_о_разных_версиях_один_раз_и_пишет_о_них_в_о_программе()
    {
        var channel = new ScriptedChannel(request => request.Command == EngineCommandKind.Status
            ? EngineResponse.Success(
                request.Id,
                "Состояние.",
                $$"""{"appVersion":"0.0.1","protocolVersion":{{EngineClient.SelfProtocolVersion}}}""")
            : EngineResponse.Success(request.Id, "Готово."));
        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());

        await main.RefreshStatusCommand.ExecuteAsync();

        Assert.True(main.IsNoticeVisible, "расхождение версий — предупреждение, а не молчание");
        Assert.Equal("Версии различаются", main.NoticeTitleRu);
        Assert.Contains("0.0.1", main.NoticeRu);

        // В «О программе» строка остаётся: предупреждение можно пропустить, а строку — нет.
        Assert.Contains("0.0.1", main.VersionsNoteRu);
        Assert.Contains("различаются", main.VersionsNoteRu);

        // И не дёргает по мелочи: повторное обновление предупреждение не дублирует.
        main.DismissNoticeCommand.Execute(null);
        await main.RefreshStatusCommand.ExecuteAsync();
        Assert.False(main.IsNoticeVisible);
    }
}
