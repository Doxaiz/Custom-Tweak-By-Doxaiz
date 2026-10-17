using System.Buffers.Binary;
using System.Text;

namespace PingForge.Service.Protocol;

/// <summary>
/// Обмен приложения со службой по именованному каналу.
///
/// Почему канал, а не «всё в одном процессе»: твики и фильтр пакетов требуют прав LocalSystem,
/// а интерфейс должен работать от обычного пользователя. Служба держит права, приложение — окно.
///
/// Формат кадра нарочно простой: 4 байта длины (little-endian) + JSON в UTF-8. Он читается глазами
/// в отладчике и не требует библиотек сериализации помимо встроенной. Предел кадра — 1 МиБ:
/// больше по этому каналу не передаётся ничего, а бесконечный кадр — это способ уронить службу.
/// </summary>
public static class EngineProtocol
{
    /// <summary>Имя канала. Версия в имени: старое приложение не должно разговаривать с новой службой.</summary>
    public const string PipeName = "PingForge.Engine.v1";

    /// <summary>
    /// Имя канала только для чтения (C2). Его права шире — туда пускают интерактивного пользователя, —
    /// но и команд в нём только просмотр: смотреть состояние, читать журнал и откатывать СВОИ изменения.
    /// Меняющие команды отсюда не выполняются даже для администратора: для них есть основной канал.
    /// </summary>
    public const string ReadOnlyPipeName = "PingForge.Engine.ReadOnly.v1";

    /// <summary>Версия протокола. Клиент обязан сравнить её со своей через команду <c>ping</c>.</summary>
    public const int Version = 1;

    /// <summary>Предел размера одного кадра: 1 МиБ. Больше — только снапшоты, а они идут через файл, не через канал.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>Длина заголовка кадра: 4 байта.</summary>
    public const int HeaderBytes = 4;
}

/// <summary>
/// Какой канал обслуживается. Права канала и список команд у них разные: основной — LocalSystem и
/// Администраторы с полным набором команд; канал чтения — с просмотром для интерактивного пользователя.
/// </summary>
public enum EngineChannelKind
{
    /// <summary>Основной канал: команды, меняющие Windows.</summary>
    Main,

    /// <summary>Канал только для чтения: просмотр состояния и откат своих изменений.</summary>
    ReadOnly,
}

/// <summary>Нарушение протокола: кадр битый, оборванный или слишком большой. Текст — для журнала, не для пользователя.</summary>
public sealed class EngineProtocolViolationException : Exception
{
    public EngineProtocolViolationException(string messageRu, Exception? inner = null)
        : base(messageRu, inner)
    {
    }
}

/// <summary>
/// Кадры поверх <see cref="Stream"/>. Работает с любым потоком: в бою это именованный канал,
/// в тестах — <see cref="MemoryStream"/> или пара связанных потоков, поэтому формат проверяется без Windows.
/// </summary>
public static class EngineFrameCodec
{
    /// <summary>Записать кадр. Если JSON больше предела — кадр не отправляется, вызывающий узнаёт об этом исключением.</summary>
    public static async Task WriteAsync(Stream stream, string payloadJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payloadJson);

        byte[] payload = Encoding.UTF8.GetBytes(payloadJson);
        if (payload.Length > EngineProtocol.MaxFrameBytes)
        {
            throw new EngineProtocolViolationException(
                $"Кадр больше предела: {payload.Length} байт при пределе {EngineProtocol.MaxFrameBytes}.");
        }

        if (payload.Length == 0)
        {
            throw new EngineProtocolViolationException("Пустой кадр отправлять нельзя: сначала JSON, потом длина.");
        }

        byte[] header = new byte[EngineProtocol.HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Прочитать кадр. Возвращает <c>null</c>, если поток аккуратно закрылся до начала кадра — это
    /// нормальный конец связи, а не ошибка.
    /// </summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[EngineProtocol.HeaderBytes];
        int headerRead = await ReadAtMostAsync(stream, header, ct).ConfigureAwait(false);

        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < EngineProtocol.HeaderBytes)
        {
            throw new EngineProtocolViolationException(
                $"Кадр оборван на заголовке: пришло {headerRead} байта из {EngineProtocol.HeaderBytes}.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0)
        {
            throw new EngineProtocolViolationException(
                $"Заголовок кадра объявляет длину {length}: кадр обязан быть непустым.");
        }

        if (length > EngineProtocol.MaxFrameBytes)
        {
            throw new EngineProtocolViolationException(
                $"Заголовок кадра объявляет {length} байт при пределе {EngineProtocol.MaxFrameBytes}: связь будет закрыта.");
        }

        byte[] payload = new byte[length];
        int payloadRead = await ReadAtMostAsync(stream, payload, ct).ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new EngineProtocolViolationException(
                $"Кадр оборван: обещано {length} байт, пришло {payloadRead}.");
        }

        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Прочитать ровно <paramref name="buffer"/>.Length байт или меньше, если поток закрылся.</summary>
    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
