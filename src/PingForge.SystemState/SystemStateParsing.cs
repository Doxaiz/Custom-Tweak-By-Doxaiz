using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PingForge.SystemState;

/// <summary>
/// Разбор ответов системы. Правило одно: значения читаем, подписи не читаем.
/// Windows печатает подписи на языке системы (у нас — русском), а сами значения всегда английские
/// («enabled», «normal», «1 Gbps»), поэтому разбор ищет значения и не зависит от языка интерфейса.
/// Всё, что разобрать не удалось, возвращает null: состояние не выдумывается, а честно остаётся неизвестным.
/// </summary>
public static class SystemStateParsing
{
    private static readonly Dictionary<string, bool> FlagAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = true,
        ["enable"] = true,
        ["on"] = true,
        ["true"] = true,
        ["yes"] = true,
        ["1"] = true,
        ["disabled"] = false,
        ["disable"] = false,
        ["off"] = false,
        ["false"] = false,
        ["no"] = false,
        ["0"] = false,
    };

    /// <summary>
    /// «Windows 11 Pro · 24H2 · сборка 26100.1742» из четырёх значений реестра. Собираются только
    /// прочитанные части: если номера сборки нет, строка останется без него, а не с выдуманным нулём.
    /// </summary>
    public static string? DescribeWindows(string? productName, string? displayVersion, string? build, string? ubr)
    {
        var parts = new List<string>(3);

        if (IsMeaningful(productName))
        {
            parts.Add(productName.Trim());
        }

        if (IsMeaningful(displayVersion))
        {
            parts.Add(displayVersion.Trim());
        }

        if (IsMeaningful(build))
        {
            parts.Add(IsMeaningful(ubr) ? $"сборка {build.Trim()}.{ubr.Trim()}" : $"сборка {build.Trim()}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Флаг из строки: enabled/disabled, on/off, true/false, 1/0 — в любом регистре.
    /// Всё остальное (например, «default») даёт null: значение не подменяется догадкой.
    /// </summary>
    public static bool? TryParseFlag(string? raw) =>
        IsMeaningful(raw) && FlagAliases.TryGetValue(raw.Trim(), out bool value) ? value : null;

    /// <summary>Строка в число без исключений: мусор даёт null, а не падение.</summary>
    public static int? TryParseInt(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    /// <summary>
    /// Скорость соединения в Мбит/с из строки PowerShell: «1 Gbps» → 1000, «2.5 Gbps» → 2500,
    /// «100 Mbps» → 100. Единицы печатает сама система по-английски; десятичный разделитель — точка.
    /// </summary>
    public static int? ParseLinkSpeedMbps(string? raw)
    {
        if (!IsMeaningful(raw))
        {
            return null;
        }

        Match match = Regex.Match(raw, @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>gbps|mbps|kbps)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        string number = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
        {
            return null;
        }

        double mbps = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "gbps" => speed * 1000,
            "mbps" => speed,
            "kbps" => speed / 1000,
            _ => double.NaN,
        };

        if (double.IsNaN(mbps) || mbps < 1)
        {
            return null;
        }

        return (int)Math.Round(mbps, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Активная схема электропитания из ответа <c>powercfg /getactivescheme</c>:
    /// строка вида «Power Scheme GUID: 381b4222-… (Balanced)». Идентификатор — всегда латиница с цифрами,
    /// имя в скобках система печатает на своём языке, поэтому берём его как есть, а не переводом.
    /// </summary>
    public static (string? Guid, string? Name) ParsePowerScheme(string? output)
    {
        if (!IsMeaningful(output))
        {
            return (null, null);
        }

        Match guid = Regex.Match(output, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");
        if (!guid.Success)
        {
            return (null, null);
        }

        Match name = Regex.Match(output[(guid.Index + guid.Length)..], @"\(([^)]+)\)");
        return (guid.Value.ToLowerInvariant(), name.Success ? name.Groups[1].Value.Trim() : null);
    }

    /// <summary>
    /// «Nagle выключен» — это два значения сразу: TcpAckFrequency и TCPNoDelay должны быть равны 1.
    /// Если выставлено только одно, Nagle остался включённым наполовину: честный ответ — «нет»,
    /// а не «не знаю», потому что отключённым считается только полное отключение.
    /// </summary>
    public static bool? CombineNagleFlags(string? ackFrequency, string? noDelay)
    {
        int? ack = TryParseInt(ackFrequency);
        int? noDelayValue = TryParseInt(noDelay);

        if (ack is null || noDelayValue is null)
        {
            return null;
        }

        return ack == 1 && noDelayValue == 1;
    }

    /// <summary>Доля включённого режима доставки: 0 — обмен между компьютерами выключен, 1–3 — включён.</summary>
    public static bool? ParseDeliveryOptimizationP2p(string? raw)
    {
        int? mode = TryParseInt(raw);
        return mode is null or < 0 ? null : mode != 0;
    }

    /// <summary>Восстановление системы: значение DisableSR (0 — включено, 1 — выключено).</summary>
    public static bool? ParseSystemRestoreEnabled(string? raw)
    {
        int? disabled = TryParseInt(raw);
        return disabled is null ? null : disabled == 0;
    }

    /// <summary>Повышенное разрешение таймера: GlobalTimerResolutionRequests = 1 означает «включено».</summary>
    public static bool? ParseTimerResolutionRaised(string? raw)
    {
        int? value = TryParseInt(raw);
        return value is null or < 0 ? null : value == 1;
    }

    /// <summary>Строки ответа команды: пустые строки и строки-подписи отбрасываются.</summary>
    public static IReadOnlyList<string> MeaningfulLines(string? output)
    {
        if (!IsMeaningful(output))
        {
            return Array.Empty<string>();
        }

        return output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(IsMeaningful)
            .ToList();
    }

    /// <summary>Проверка «строка что-то значит» с подсказкой компилятору: после неё ссылка не пустая.</summary>
    private static bool IsMeaningful([NotNullWhen(true)] string? value) => !string.IsNullOrWhiteSpace(value);
}
