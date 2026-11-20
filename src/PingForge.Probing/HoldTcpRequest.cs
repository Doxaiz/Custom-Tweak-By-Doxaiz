namespace PingForge.Probing;

/// <summary>
/// Служебный режим «подержать соединение»: дочерний процесс открывает TCP-соединение и шлёт данные,
/// пока родитель читает статистику этого соединения.
///
/// Зачем это нужно: служба читает статистику iphlpapi по соединению <b>чужого</b> процесса — той самой
/// игры, которую она нашла. Проверить это на своём же соединении нельзя: непонятно, работает ли чтение
/// по чужому потоку. Дочерний процесс и есть «чужой»: у него свой номер, своё соединение, свой трафик.
/// </summary>
public sealed record HoldTcpRequest(string Address, int Port, int Seconds)
{
    /// <summary>Имя режима в командной строке.</summary>
    public const string Switch = "--hold-tcp";

    /// <summary>Адрес обязателен и должен быть числовым: имя разрешает родитель, чтобы оба говорили об одном адресе.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Address)
        && Port is >= 1 and <= 65535
        && Seconds is >= 1 and <= 600;

    /// <summary>
    /// Разобрать аргументы. Возвращает false, если режима нет или он назван неполно: тогда приложение
    /// запускается как обычно, а не пытается что-то держать.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string>? args, out HoldTcpRequest? request)
    {
        request = null;

        if (args is null || args.Count == 0)
        {
            return false;
        }

        int index = -1;
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || args.Count < index + 4)
        {
            return false;
        }

        if (!int.TryParse(args[index + 2], out int port) || port is < 1 or > 65535)
        {
            return false;
        }

        if (!int.TryParse(args[index + 3], out int seconds) || seconds is < 1 or > 600)
        {
            return false;
        }

        // Отказ не оставляет «полуразобранного» запроса: снаружи false значит «режима нет»,
        // и вызывающий не должен получить объект, с которым непонятно что делать.
        var candidate = new HoldTcpRequest(args[index + 1], port, seconds);
        if (!candidate.IsComplete)
        {
            return false;
        }

        request = candidate;
        return true;
    }
}
