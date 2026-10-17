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
    /// <summary>Явно заданный предел канала: если он есть, он ограничивает сверху любой запрос.</summary>
    private readonly TimeSpan? _cap;

    /// <summary>Сколько ждали в последнем запросе — для честного текста отказа.</summary>
    private TimeSpan _lastTimeout;

    /// <param name="pipeName">
    /// Имя канала. Не задано — выбирается самим по правам процесса (C2): программа от администратора
    /// идёт в основной канал, обычная — в канал только для чтения. Так окно всегда получает ровно те
    /// команды, какие ему доступны, и не стучится в закрытую дверь.
    /// </param>
    /// <param name="timeout">Явный предел ожидания, ограничивает сверху любой запрос.</param>
    public NamedPipeEngineChannel(string? pipeName = null, TimeSpan? timeout = null)
    {
        _pipeName = pipeName ?? DefaultPipeName();
        _cap = timeout;
    }

    /// <summary>Имя канала, к которому подключается этот клиент — для честных сообщений и журнала.</summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Какой канал нужен этому процессу. Основной — только от администратора: там команды, меняющие
    /// Windows, и его права закрыты для обычного пользователя на уровне ACL канала. Остальным доступен
    /// канал чтения — состояние, журнал и остановка своих изменений.
    /// </summary>
    public static string DefaultPipeName() =>
        IsElevatedAdministrator() ? EngineProtocol.PipeName : EngineProtocol.ReadOnlyPipeName;

    private static bool IsElevatedAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is SystemException or UnauthorizedAccessException)
        {
            // Не смогли узнать свои права — значит считаем, что их нет: пойдём в канал чтения.
            // Показать больше, чем есть, хуже, чем показать меньше.
            return false;
        }
    }

    public async Task<EngineResponse> SendAsync(EngineRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Если канал создан с явным пределом (так делают проверки и смоук-тесты), он ограничивает
        // сверху: живой тест «служба молчит» не должен ждать десять секунд только потому,
        // что у команды ping в каталоге такой предел. Без явного предела решает каталог команд.
        TimeSpan asked = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : timeout;
        _lastTimeout = _cap is { } cap && cap < asked ? cap : asked;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(_lastTimeout);

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
        $"Служба не ответила за {_lastTimeout.TotalSeconds:0.#} с: она занята другой работой, остановлена или " +
        "связь оборвана. Проверьте, что служба PingForge запущена, и повторите нажатие.");

    public void Dispose()
    {
        // Соединения открываются на один обмен, поэтому закрывать нечего.
    }
}
