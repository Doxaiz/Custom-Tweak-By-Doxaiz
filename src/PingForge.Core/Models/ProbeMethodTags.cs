namespace PingForge.Core.Models;

/// <summary>
/// Способы замера в виде коротких меток для журнала и человеческих подписей для окна.
///
/// Зачем это: в базе сессий колонки способа замера были заполнены одной строкой «LiveFlow» на любую
/// сессию — и для живого потока, и для ICMP-эха, и для TCP-рукопожатия. Пока способ был один, это
/// сходилось; теперь способов четыре, и постоянная строка стала неправдой. Метка пишется в базу,
/// подпись строится из метки, а старые записи честно называются старыми, а не «живым потоком».
/// </summary>
public static class ProbeMethodTags
{
    /// <summary>Замер не состоялся: числа нет, и способа тоже.</summary>
    public const string Unmeasurable = "unmeasurable";

    /// <summary>Живой TCP-поток игры: статистика уже открытого соединения.</summary>
    public const string LiveTcp = "live-tcp";

    /// <summary>Живой UDP-поток игры: пары «отправка → ответ» из событий сетевого стека.</summary>
    public const string LiveUdp = "live-udp";

    /// <summary>Активные пробы: ICMP-эхо.</summary>
    public const string Icmp = "icmp";

    /// <summary>Активные пробы: время TCP-рукопожатия с портом сервера.</summary>
    public const string TcpHandshake = "tcp-handshake";

    /// <summary>
    /// Так писали до этой волны. Строка осталась в базах у тех, кто уже запускал оптимизацию,
    /// и переписать её нельзя: настоящий способ тех сессий потерян.
    /// </summary>
    public const string LegacyLiveFlow = "LiveFlow";

    /// <summary>Метка для базы. Неизвестный способ становится «не измерено», а не выдуманной строкой.</summary>
    public static string Tag(ProbeMethodUsed method) =>
        method switch
        {
            ProbeMethodUsed.LiveTcpEstats => LiveTcp,
            ProbeMethodUsed.LiveUdpPassiveFlow => LiveUdp,
            ProbeMethodUsed.ActiveIcmpEcho => Icmp,
            ProbeMethodUsed.ActiveTcpHandshake => TcpHandshake,
            _ => Unmeasurable,
        };

    /// <summary>Подпись для человека. Незнакомая метка называется незнакомой, а не «живым потоком».</summary>
    public static string Ru(string? tag) =>
        tag switch
        {
            null or "" => "способ замера не записан",
            LiveTcp => "живой поток игры (TCP-статистика)",
            LiveUdp => "живой поток игры (пассивные метки)",
            Icmp => "ICMP-эхо (активные пробы, пакеты сверх трафика игры)",
            TcpHandshake => "TCP-рукопожатие (активные пробы, пакеты сверх трафика игры)",
            Unmeasurable => "не измерено",
            LegacyLiveFlow => "живой поток, но какой именно — старая запись не уточняет",
            _ => $"незнакомый способ замера «{tag}»",
        };

    /// <summary>
    /// Пара снята разными способами. Тогда разница чисел может быть свойством способа, а не действий
    /// программы, и это говорится словами. «После» не измерено — это не другой способ, а отсутствие замера.
    /// </summary>
    public static bool PairDiffers(string? baselineTag, string? finalTag) =>
        !string.IsNullOrWhiteSpace(baselineTag)
        && !string.IsNullOrWhiteSpace(finalTag)
        && !string.Equals(finalTag, Unmeasurable, StringComparison.Ordinal)
        && !string.Equals(baselineTag, Unmeasurable, StringComparison.Ordinal)
        && !string.Equals(baselineTag, finalTag, StringComparison.Ordinal);

    /// <summary>
    /// Подпись для пары «до» и «после»: способы могли различаться, и тогда это видно.
    /// Одним словом «живой поток» пара не называется, если на самом деле один замер был пробой.
    /// </summary>
    public static string DescribePairRu(string? baselineTag, string? finalTag)
    {
        bool baselineKnown = !string.IsNullOrWhiteSpace(baselineTag);
        bool finalKnown = !string.IsNullOrWhiteSpace(finalTag);

        if (!baselineKnown && !finalKnown)
        {
            return "способ замера не записан";
        }

        if (baselineKnown && finalKnown && string.Equals(baselineTag, finalTag, StringComparison.Ordinal))
        {
            return "способ: " + Ru(baselineTag);
        }

        var parts = new List<string>(2);
        if (baselineKnown)
        {
            parts.Add("«до»: " + Ru(baselineTag));
        }

        if (finalKnown)
        {
            parts.Add("«после»: " + Ru(finalTag));
        }

        return "способ: " + string.Join(", ", parts);
    }
}
