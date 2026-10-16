using System.IO.Pipes;
using PingForge.Service.Protocol;

namespace PingForge.Service.Windows;

/// <summary>
/// Канал службы: слушает, читает кадры, отдаёт их разборщику и отправляет ответ.
///
/// Канал обслуживается по очереди, а не пачкой потоков. Это осознанное решение: команды применяют твики
/// и ставят маршруты, то есть меняют систему. Две такие операции одновременно — это гонка в реестре
/// и в таблице маршрутов, а не «быстрее». Клиенты, пришедшие вторыми, просто ждут своей очереди.
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

    /// <summary>Слушать канал до отмены. Возврат — только по остановке службы.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log($"Слушаю канал {EngineProtocol.PipeName}; одновременных подключений: {ServiceSecurityPlan.MaxConnections}.");
        _log(ServiceSecurityPlan.DescribeRu());

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = PipeAclFactory.CreateServerStream();
            }
            catch (UnauthorizedAccessException ex)
            {
                // Сюда попадаем, если канал занят другим экземпляром службы — это честная причина остановиться.
                _log($"Канал {EngineProtocol.PipeName} занят: {ex.Message}. Похоже, служба уже запущена.");
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _log("Подключился клиент.");
                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
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

        _log("Канал закрыт: служба останавливается.");
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
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

                EngineResponse response = await _dispatcher.HandleJsonAsync(frame, ct).ConfigureAwait(false);
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
