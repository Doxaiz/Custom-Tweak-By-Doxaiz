using System.IO.Pipes;
using PingForge.Service.Protocol;

namespace PingForge.Service.Windows;

/// <summary>
/// Каналы службы: слушает, читает кадры, отдаёт их разборщику и отправляет ответ.
///
/// Каналов два (C2), и обслуживаются они одинаково, но с разными правами и разборщиками:
///   • основной — LocalSystem и Администраторы, полный набор команд;
///   • канал только для чтения — плюс интерактивный вход, но команды только просмотр.
///
/// Каждый канал обслуживается по очереди, а не пачкой потоков. Это осознанное решение: команды применяют
/// твики и ставят маршруты, то есть меняют систему. Две такие операции одновременно — это гонка в реестре
/// и в таблице маршрутов, а не «быстрее». Клиенты, пришедшие вторыми, просто ждут своей очереди.
///
/// У каждого подключения читаются права клиента (<see cref="PipeClientInspector"/>) и передаются
/// разборщику: это третий уровень защиты — даже администратор по каналу чтения не получит меняющую
/// команду, а обычный пользователь по основному каналу не получит ничего вовсе (туда он не подключится).
/// Отказ по правам пишется в журнал службы вместе с именем клиента — следы попытки остаются.
/// </summary>
public sealed class EnginePipeServer
{
    private readonly EngineDispatcher _dispatcher;
    private readonly Action<string> _log;

    public EnginePipeServer(EngineDispatcher dispatcher, Action<string>? log = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Слушать оба канала до отмены. Возврат — только по остановке службы.
    /// Разборщик канала чтения создаётся от того же набора операций: действия те же, права — другие.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log(ServiceSecurityPlan.DescribeRu());

        await Task.WhenAll(
            RunChannelAsync(EngineChannelKind.Main, _dispatcher, ct),
            RunChannelAsync(EngineChannelKind.ReadOnly, _dispatcher.ForChannel(EngineChannelKind.ReadOnly), ct))
            .ConfigureAwait(false);
    }

    private async Task RunChannelAsync(EngineChannelKind channelKind, EngineDispatcher dispatcher, CancellationToken ct)
    {
        string pipeName = PipeNameOf(channelKind);
        string kindRu = channelKind == EngineChannelKind.Main ? "основной" : "только чтение";

        _log($"Слушаю канал {pipeName} ({kindRu}); одновременных подключений: {ServiceSecurityPlan.MaxConnections}.");

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = PipeAclFactory.CreateServerStream(channelKind);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Сюда попадаем, если канал занят другим экземпляром службы — это честная причина остановиться.
                _log($"Канал {pipeName} занят: {ex.Message}. Похоже, служба уже запущена.");
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Права клиента определяются один раз на подключение: процесс за время разговора
                // не меняется, а спрашивать токен на каждую команду — лишняя работа и лишний повод
                // получить отказ там, где его не должно быть.
                PipeClientIdentity client = PipeClientInspector.Inspect(pipe);
                _log($"Подключился клиент ({kindRu}). {client.DescribeRu()}");

                await HandleConnectionAsync(pipe, client, dispatcher, ct).ConfigureAwait(false);
                _log("Клиент отключился.");
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (IOException ex)
            {
                _log($"Связь оборвалась: {ex.Message}");
                pipe.Dispose();
            }
        }

        _log($"Канал {pipeName} закрыт: служба останавливается.");
    }

    private static string PipeNameOf(EngineChannelKind channelKind) =>
        channelKind == EngineChannelKind.Main ? EngineProtocol.PipeName : EngineProtocol.ReadOnlyPipeName;

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        PipeClientIdentity client,
        EngineDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await EngineFrameCodec.ReadAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (EngineProtocolViolationException ex)
                {
                    _log($"Нарушение протокола: {ex.Message}");
                    await TryWriteAsync(pipe, EngineResponse.Failure(string.Empty, $"Служба закрыла связь: {ex.Message}"), ct).ConfigureAwait(false);
                    break;
                }

                if (frame is null)
                {
                    break;
                }

                EngineResponse response = await dispatcher
                    .HandleJsonAsync(
                        frame,
                        client.IsAdministrator,
                        denialRu => _log($"ОТКАЗ ПО ПРАВАМ ({client.DescribeRu()}): {denialRu}"),
                        ct)
                    .ConfigureAwait(false);
                if (!await TryWriteAsync(pipe, response, ct).ConfigureAwait(false))
                {
                    break;
                }

                _log($"Команда «{response.Id}»: {(response.Ok ? "готово" : "отказ")} — {response.MessageRu}");
            }
        }
        catch (IOException ex)
        {
            _log($"Клиент отключился, не дождавшись ответа: {ex.Message}");
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private static async Task<bool> TryWriteAsync(Stream pipe, EngineResponse response, CancellationToken ct)
    {
        try
        {
            await EngineFrameCodec.WriteAsync(pipe, EngineJson.Encode(response), ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (EngineProtocolViolationException)
        {
            // Ответ не влез в предел кадра: молчать об этом нельзя, но и записать уже нечем.
            return false;
        }
    }
}
