using System.Globalization;
using System.Text;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Разбор вывода netsh. Русская Windows печатает другие подписи и другие значения
/// («обычный» вместо «normal»), поэтому подписи сравниваются в нормализованном виде,
/// а значения приводятся к каноническому английскому токену.
/// </summary>
public static class NetshOutputParser
{
    private static readonly Dictionary<string, string> LabelAliases = new(StringComparer.Ordinal)
    {
        // англоязычные подписи
        ["receivewindowautotuninglevel"] = "autotuninglevel",
        ["autotuninglevel"] = "autotuninglevel",
        ["ecncapability"] = "ecncapability",
        ["rssreceive"] = "rss",
        ["receivesidescalingstate"] = "rss",
        ["receivesidescaling"] = "rss",
        ["rss"] = "rss",
        ["timestamps"] = "timestamps",
        ["nonsackrttresiliency"] = "nonsackrttresiliency",
        ["maxsynretransmissions"] = "maxsynretransmissions",
        ["initialrto"] = "initialrto",
        ["heuristics"] = "heuristics",
        ["dca"] = "dca",
        ["congestioncontrolprovider"] = "dca",
        ["receivesegmentcoalescingrsc"] = "rsc",
        ["receivesegmentcoalescing"] = "rsc",
        ["rsc"] = "rsc",

        // русские подписи
        ["уровеньавтонастройки"] = "autotuninglevel",
        ["уровеньавтонастройкиокнаприема"] = "autotuninglevel",
        ["получениепоrss"] = "rss",
        ["приёмrss"] = "rss",
        ["приемrss"] = "rss",
        ["возможностьecn"] = "ecncapability",
        ["меткивремени"] = "timestamps",
        ["устойчивостьrttбезsack"] = "nonsackrttresiliency",
        ["максчислоповторныхпередачsyn"] = "maxsynretransmissions",
        ["максимальноечислоповторныхпередачsyn"] = "maxsynretransmissions",
        ["начальноевремяожиданияrto"] = "initialrto",
        ["эвристики"] = "heuristics",
        ["поставщикуправленияперегрузкой"] = "dca",
        ["объединениесегментовполученияrsc"] = "rsc",
        ["объединениесегментовполучения"] = "rsc",
        ["объединениесегментовприёма"] = "rsc",
        ["объединениесегментовприема"] = "rsc"
    };

    private static readonly Dictionary<string, string> ValueAliases = new(StringComparer.Ordinal)
    {
        ["обычный"] = "normal",
        ["ограниченный"] = "restricted",
        ["высокоограниченный"] = "highlyrestricted",
        ["экспериментальный"] = "experimental",
        ["отключено"] = "disabled",
        ["отключен"] = "disabled",
        ["выключено"] = "disabled",
        ["включено"] = "enabled",
        ["включен"] = "enabled",
        ["включена"] = "enabled",
        ["высокий"] = "high",
        ["высокая"] = "high",
        ["средний"] = "medium",
        ["низкий"] = "low",
        ["поумолчанию"] = "default",
        ["включенапомощь"] = "enabled",
        ["никогда"] = "never"
    };

    public static IReadOnlyDictionary<string, string> ParseTcpGlobal(string output)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in SplitLines(output))
        {
            int separator = rawLine.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string label = NormalizeLabel(rawLine[..separator]);
            string? key = ResolveKey(label);
            if (key is null)
            {
                continue;
            }

            parsed[key] = CanonicalizeToken(rawLine[(separator + 1)..]);
        }

        return parsed;
    }

    /// <summary>Переводит значение в канонический токен: «высоко ограниченный» → highlyrestricted.</summary>
    public static string CanonicalizeToken(string raw)
    {
        string value = raw.Trim();

        int bracket = value.IndexOf('(');
        if (bracket > 0)
        {
            value = value[..bracket].Trim();
        }

        string normalized = NormalizeToken(value);
        return ValueAliases.TryGetValue(normalized, out string? canonical) ? canonical : value.ToLowerInvariant();
    }

    public static string? ResolveKey(string normalizedLabel) =>
        LabelAliases.TryGetValue(normalizedLabel, out string? key) ? key : null;

    public static string NormalizeLabel(string label)
    {
        var builder = new StringBuilder(label.Length);
        foreach (char c in label.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Уровень MTU конкретного интерфейса из таблицы `netsh interface ipv4 show subinterfaces`.</summary>
    public static int? ParseSubinterfaceMtu(string output, string interfaceName)
    {
        foreach (string line in SplitLines(output))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || !char.IsDigit(trimmed[0]))
            {
                continue;
            }

            string[] columns = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5 || !int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mtu))
            {
                continue;
            }

            // Формат: MTU  Состояние  Админ.состояние  Тип  Имя интерфейса...
            string name = string.Join(' ', columns[4..]);
            if (string.Equals(name, interfaceName, StringComparison.OrdinalIgnoreCase))
            {
                return mtu;
            }
        }

        return null;
    }

    /// <summary>Список DNS-серверов интерфейса из `netsh interface ipv4 show dnsservers`.</summary>
    public static IReadOnlyList<string> ParseIpv4DnsServers(string output)
    {
        var servers = new List<string>();

        foreach (string line in SplitLines(output))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string[] columns = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length == 0)
            {
                continue;
            }

            if (System.Net.IPAddress.TryParse(columns[0], out System.Net.IPAddress? address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                string text = address.ToString();
                if (!servers.Contains(text, StringComparer.Ordinal))
                {
                    servers.Add(text);
                }
            }
        }

        return servers;
    }

    public static string JoinServers(IReadOnlyList<string> servers) => string.Join("|", servers);

    public static IReadOnlyList<string> SplitServers(string? canonical) =>
        string.IsNullOrWhiteSpace(canonical)
            ? Array.Empty<string>()
            : canonical.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Первая непустая строка — для честного сообщения об ошибке вместо пустого текста netsh.</summary>
    public static string FirstMeaningfulLine(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            foreach (string line in SplitLines(candidate))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
        }

        return "Утилита не сообщила подробностей.";
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
}
