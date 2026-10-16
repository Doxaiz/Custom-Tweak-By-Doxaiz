using System.IO;
using System.IO.Pipes;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Клиентский конец канала службы — тот самый, которым пользуется окно программы.
///
/// Переехал сюда из проекта окна, потому что ничего оконного в нём нет: только именованный канал и
/// рамка протокола. За это он теперь проверяется тестами на любой машине (сервер-двойник поднимается
/// в самом тесте) и им же пользуется утилита живой проверки службы
/// <c>tools/PingForge.EngineSmoke</c>.
///
/// Соединение открывается на время одного обмена и закрывается сразу. Так сделано нарочно: держать
/// канал открытым «для скорости» — значит занимать одно из немногих подключений службы, пока окно
/// просто стоит. Замер времени показывает, что подключение к именованному каналу дешевле, чем
/// последствия «висящего» соединения после перезапуска службы.
/// </summary>
public sealed class NamedPipeEngineChannel : IEngineChannel
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    public NamedPipeEngineChannel(string? pipeName = null, TimeSpan? timeout = null)
    {
        _pipeName = pipeName ?? EngineProtocol.PipeName;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(3000, timeoutSource.Token).ConfigureAwait(false);

            await EngineFrameCodec.WriteAsync(pipe, EngineJson.Encode(request), timeoutSource.Token).ConfigureAwait(false);
            string? frame = await EngineFrameCodec.ReadAsync(pipe, timeoutSource.Token).ConfigureAwait(false);

            if (frame is null)
            {
                throw new EngineUnavailableException(
                    "Служба закрыла канал, не ответив. Похоже, она остановилась во время работы.");
            }

            return EngineJson.DecodeResponse(frame);
        }
        catch (TimeoutException)
        {
            throw NoAnswer();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw NoAnswer();
        }
        catch (IOException ex)
        {
            throw new EngineUnavailableException(
                "Служба PingForge не отвечает. Проверьте, что она запущена: без неё настройки не меняются.", ex);
        }
        catch (EngineProtocolViolationException ex)
        {
            throw new EngineUnavailableException(
                $"Служба ответила неразборчиво: {ex.Message}. Это ошибка версий — приложение и служба должны быть из одной сборки.", ex);
        }
    }

    /// <summary>
    /// Один и тот же текст на «не дождались ответа» и на «ответ не пришёл вовремя»: для человека это
    /// одно и то же событие, а разные формулировки заставляли искать разницу там, где её нет.
    /// </summary>
    private EngineUnavailableException NoAnswer() => new(
        $"Служба не ответила за {_timeout.TotalSeconds:0.#} с: она занята другой работой, остановлена или " +
        "связь оборвана. Проверьте, что служба PingForge запущена, и повторите нажатие.");

    public void Dispose()
    {
        // Соединения открываются на один обмен, поэтому закрывать нечего.
    }
}
