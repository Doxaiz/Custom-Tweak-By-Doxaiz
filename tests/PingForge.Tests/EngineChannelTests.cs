using System.IO;
using System.IO.Pipes;
using System.Text;
using PingForge.App.ViewModels;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Клиентский конец канала службы — тот самый, которым пользуется окно программы. До этого он жил
/// в проекте окна и проверялся только глазами на живой Windows; теперь он в кросс-платформенном проекте,
/// и сервер-двойник поднимается прямо в тесте.
///
/// Проверяется то, на чём спотыкаются живые каналы: чужой номер ответа, обрыв без ответа, мусор вместо
/// рамки, молчание в ответ на запрос и «служба вообще не запущена». Каждый такой случай обязан
/// закончиться понятным русским текстом, а не зависанием окна.
/// </summary>
public class EngineChannelTests
{
    private static string NewPipeName() => "pingforge-test-" + Guid.NewGuid().ToString("N");

    private static NamedPipeServerStream NewServer(string pipeName, int instances) =>
        new(pipeName, PipeDirection.InOut, instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>
    /// Один обмен: сервер поднимается и начинает слушать до того, как клиент подключится. Так проверяется
    /// последовательность номеров запросов без гонки между «закрыл прошлое соединение» и «слушаю новое».
    /// </summary>
    private static async Task ServeOnceAsync(string pipeName, Func<Stream, Task> onConnection)
    {
        // Ожидание ограничено: если клиент не постучался, тест обязан упасть с понятным текстом,
        // а не висеть вечно и невесть почему задерживать весь прогон.
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var server = NewServer(pipeName, 1);
        await server.WaitForConnectionAsync(patience.Token);
        await onConnection(server);
    }

    private static Task<string?> ReadRequestAsync(Stream stream, CancellationToken ct = default) =>
        EngineFrameCodec.ReadAsync(stream, ct);

    private static Task ReplyAsync(Stream stream, string id, string message, string? dataJson = null) =>
        EngineFrameCodec.WriteAsync(stream, EngineJson.Encode(EngineResponse.Success(id, message, dataJson)));

    // ── удачный обмен ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Запрос_уходит_и_ответ_возвращается_разобранным()
    {
        string pipeName = NewPipeName();
        string? seen = null;

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            seen = await ReadRequestAsync(stream);
            await ReplyAsync(stream, "ui-1", "Служба жива.");
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        (bool ok, string message) = await client.PingAsync();
        await server;

        Assert.True(ok);
        Assert.Equal("Служба жива.", message);
        Assert.NotNull(seen);
        Assert.Contains("\"command\":\"ping\"", seen);
        Assert.Contains("\"id\":\"ui-1\"", seen);
    }

    [Fact]
    public async Task Ответ_на_чужой_номер_запроса_не_принимается_за_свой()
    {
        string pipeName = NewPipeName();

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            await ReadRequestAsync(stream);
            await ReplyAsync(stream, "ui-999", "Ответ от прошлого запроса.");
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        EngineUnavailableException error = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => client.PingAsync());
        await server;

        Assert.Contains("чужой запрос", error.Message);
        Assert.Contains("ui-999", error.Message);
    }

    [Fact]
    public async Task Отказ_службы_возвращается_как_ответ_а_не_как_исключение()
    {
        // Отказ — не сбой связи: служба жива и объясняет, почему команду выполнить нельзя.
        // Такое окно обязано показать человеку, а не превращать в «служба недоступна».
        string pipeName = NewPipeName();

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            await ReadRequestAsync(stream);
            await EngineFrameCodec.WriteAsync(
                stream,
                EngineJson.Encode(EngineResponse.Failure("ui-1", "В этой сборке чтение состояния не подключено.")));
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        EngineResponse response = await client.SendAsync(EngineCommandKind.ReadSystemState);
        await server;

        Assert.False(response.Ok);
        Assert.Contains("чтение состояния не подключено", response.MessageRu);
        Assert.Null(response.DataJson);
    }

    // ── отказы ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Служба_закрыла_канал_без_ответа_говорит_об_этом_прямо()
    {
        string pipeName = NewPipeName();

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            await ReadRequestAsync(stream);
            // Ничего не отвечаем и закрываем канал: служба остановилась во время работы.
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        EngineUnavailableException error = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => client.PingAsync());
        await server;

        Assert.Contains("закрыла канал, не ответив", error.Message);
        Assert.Contains("остановилась", error.Message);
    }

    [Fact]
    public async Task Мусор_вместо_рамки_называется_ошибкой_версий()
    {
        string pipeName = NewPipeName();

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            await ReadRequestAsync(stream);

            // Рамка правильная по длине, но вместо JSON — текст: так выглядит «служба другой версии».
            byte[] payload = Encoding.UTF8.GetBytes("не json вовсе");
            await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();

            // Даём клиенту прочитать рамку до того, как закроем соединение: иначе Windows может
            // отдать обрыв связи вместо «битой рамки», и тест будет проверять не то.
            await Task.Delay(200);
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        EngineUnavailableException error = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => client.PingAsync());
        await server;

        Assert.Contains("неразборчиво", error.Message);
        Assert.Contains("одной сборки", error.Message);
    }

    [Fact]
    public async Task Оборванная_рамка_тоже_называется_ошибкой_версий()
    {
        string pipeName = NewPipeName();

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            await ReadRequestAsync(stream);

            // Заголовок обещает 100 байт, а приходит 5 — обрыв посреди кадра.
            await stream.WriteAsync(BitConverter.GetBytes(100));
            await stream.WriteAsync(Encoding.UTF8.GetBytes("корот"));
            await stream.FlushAsync();
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        EngineUnavailableException error = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => client.PingAsync());
        await server;

        Assert.Contains("неразборчиво", error.Message);
        Assert.Contains("оборван", error.Message);
    }

    [Fact]
    public async Task Молчание_в_ответ_на_запрос_заканчивается_понятным_отказом()
    {
        string pipeName = NewPipeName();

        Task server = ServeOnceAsync(pipeName, async stream =>
        {
            // Настоящая гонка: клиент истекает по таймауту и обрывает запись посреди кадра,
            // поэтому чтение на этой стороне иногда падает «кадр оборван». Это ожидаемое
            // поведение проверяемого сценария («служба молчит»), а не сбой сервера-двойника —
            // иначе проверка падала бы примерно раз на десять прогонов.
            try
            {
                await ReadRequestAsync(stream);
            }
            catch (PingForge.Service.Protocol.EngineProtocolViolationException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1500));   // «служба занята»: ответа нет
        });

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromMilliseconds(300));
        var client = new EngineClient(channel);

        EngineUnavailableException error = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => client.PingAsync());
        await server;

        Assert.Contains("не ответила за", error.Message);
        Assert.Contains("повторите нажатие", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Служба_не_запущена_отвечает_знакомым_текстом_а_не_исключением_из_глубины()
    {
        // Канал с таким именем никто не слушает — это и есть «служба не установлена».
        using var channel = new NamedPipeEngineChannel(NewPipeName(), TimeSpan.FromMilliseconds(400));
        var client = new EngineClient(channel);

        EngineUnavailableException error = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => client.PingAsync());

        Assert.Contains("Служба", error.Message);
        Assert.Contains("запущена", error.Message);
        await Task.CompletedTask;
    }

    // ── мелочи протокола ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Пинг_запоминает_версию_протокола_а_другие_команды_её_не_выдумывают()
    {
        // Запоминание версии — это решение клиента, а не свойство канала: проверяем его на двойнике,
        // потому что настоящий канал открывается на один обмен, и «второй запрос» в бою — это второе
        // соединение. Транспорт при этом проверен выше, на живых соединениях.
        var channel = new FakeEngineChannel(request => request.Command switch
        {
            EngineCommandKind.Ping => EngineResponse.Success(
                request.Id, "Служба жива.", $$"""{"protocolVersion":{{EngineProtocol.Version}}}"""),
            _ => EngineResponse.Success(request.Id, "Готово.", """{"tweakCount":47}"""),
        });

        var client = new EngineClient(channel);

        Assert.Null(client.ProtocolVersion);
        await client.PingAsync();
        Assert.Equal(EngineProtocol.Version, client.ProtocolVersion);

        await client.SendAsync(EngineCommandKind.Capabilities);
        Assert.Equal(EngineProtocol.Version, client.ProtocolVersion);   // осталось прежним, а не сбросилось
    }

    [Fact]
    public async Task Номера_запросов_идут_подряд_и_видны_снаружи()
    {
        var channel = new FakeEngineChannel(request => EngineResponse.Success(request.Id, "Ок."));
        var client = new EngineClient(channel);

        await client.SendAsync(EngineCommandKind.Ping);
        await client.SendAsync(EngineCommandKind.Status);
        await client.SendAsync(EngineCommandKind.ReadSystemState);

        Assert.Equal(new[] { "ui-1", "ui-2", "ui-3" }, channel.Requests.Select(request => request.Id));
        Assert.Equal("ui-3", client.LastRequestId);

        // И команды уходят теми именами, которые знает служба: это ловило настоящую ошибку.
        Assert.Equal(
            new[] { "ping", "status", "system-state" },
            channel.Requests.Select(request => EngineCommandCatalog.Get(request.Command).WireName));
    }

}
