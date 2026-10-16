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

    /// <summary>
    /// Сервер-двойник на <paramref name="connections"/> обменов. Клиент открывает новое соединение
    /// на каждый запрос, поэтому следующий экземпляр канала поднимается заранее — иначе между
    /// «ответил и закрыл» и «снова слушаю» клиент попадает в щёлку и получает отказ, которого в бою нет.
    /// </summary>
    private static async Task ServeAsync(
        string pipeName,
        int connections,
        Func<int, Stream, Task> onConnection,
        CancellationToken ct = default)
    {
        NamedPipeServerStream? pending = NewServer(pipeName, connections);

        for (var index = 0; index < connections; index++)
        {
            NamedPipeServerStream current = pending!;
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(TimeSpan.FromSeconds(10));

            await current.WaitForConnectionAsync(patience.Token);

            pending = index + 1 < connections ? NewServer(pipeName, connections) : null;
            try
            {
                await onConnection(index, current);
            }
            finally
            {
                current.Dispose();
            }
        }
    }

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

    /// <summary>
    /// Один сервер на <paramref name="connections"/> обменов подряд: после ответа он не закрывается,
    /// а отключает клиента и ждёт следующего. Так клиент, который открывает новое соединение на каждый
    /// запрос, попадает не на «только что удалённый адрес», а на живой слушатель.
    /// </summary>
    private static async Task ServeManyAsync(string pipeName, int connections, Func<int, Stream, Task> onConnection)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var server = NewServer(pipeName, 1);
        for (var index = 0; index < connections; index++)
        {
            await server.WaitForConnectionAsync(patience.Token);
            await onConnection(index, server);

            if (index + 1 < connections)
            {
                server.Disconnect();   // на Windows это штатный способ принять следующее соединение
            }
        }
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
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

        Task server = ServeAsync(pipeName, 1, async (_, stream) =>
        {
            await ReadRequestAsync(stream);
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
        string pipeName = NewPipeName();

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        Assert.Null(client.ProtocolVersion);

        Task server = ServeManyAsync(pipeName, 2, (index, stream) => index == 0
            ? ReplyAsync(stream, "ui-1", "Служба жива.", $$"""{"protocolVersion":{{EngineProtocol.Version}}}""")
            : ReplyAsync(stream, "ui-2", "Готово.", """{"tweakCount":47}"""));

        await client.PingAsync();
        Assert.Equal(EngineProtocol.Version, client.ProtocolVersion);

        await client.SendAsync(EngineCommandKind.Capabilities);
        await server;

        Assert.Equal(EngineProtocol.Version, client.ProtocolVersion);   // осталось прежним, а не сбросилось
    }

    [Fact]
    public async Task Номера_запросов_идут_подряд_и_видны_снаружи()
    {
        string pipeName = NewPipeName();
        var ids = new List<string>();

        using var channel = new NamedPipeEngineChannel(pipeName, TimeSpan.FromSeconds(10));
        var client = new EngineClient(channel);

        EngineCommandKind[] commands =
        [
            EngineCommandKind.Ping,
            EngineCommandKind.Status,
            EngineCommandKind.ReadSystemState,
        ];

        Task server = ServeManyAsync(pipeName, commands.Length, async (_, stream) =>
        {
            string? request = await ReadRequestAsync(stream);
            Assert.True(EngineJson.TryDecodeRequest(request!, out EngineRequest? decoded, out string errorRu), errorRu);

            ids.Add(decoded!.Id);
            await ReplyAsync(stream, decoded.Id, "Ок.");
        });

        try
        {
            foreach (EngineCommandKind command in commands)
            {
                await client.SendAsync(command);
            }
        }
        catch (Exception clientError)
        {
            // Диагностика на время отладки: если упал сервер-двойник, это важнее ошибки клиента.
            try
            {
                await server;
            }
            catch (Exception serverError)
            {
                throw new InvalidOperationException($"сервер-двойник упал: {serverError}", clientError);
            }

            throw;
        }

        await server;

        Assert.Equal(new[] { "ui-1", "ui-2", "ui-3" }, ids);
        Assert.Equal("ui-3", client.LastRequestId);
    }
}
