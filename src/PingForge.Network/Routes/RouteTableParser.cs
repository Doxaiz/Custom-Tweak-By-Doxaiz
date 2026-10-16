namespace PingForge.Network.Routes;

/// <summary>
/// Разбор вывода <c>route print -4</c>. Разбираем только то, что Windows реально печатает:
/// секцию «Active Routes» (и, если она есть, «Persistent Routes»). Всё остальное — заголовки и украшения —
/// пропускаем, а непонятные строки честно не выдумываем, а игнорируем.
/// </summary>
public static class RouteTableParser
{
    public static IReadOnlyList<RouteTableEntry> Parse(string routePrintOutput)
    {
        var entries = new List<RouteTableEntry>();
        if (string.IsNullOrWhiteSpace(routePrintOutput))
        {
            return entries;
        }

        bool inPersistent = false;

        foreach (string rawLine in routePrintOutput.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("Persistent Routes", StringComparison.OrdinalIgnoreCase))
            {
                inPersistent = true;
                continue;
            }

            if (line.StartsWith("Active Routes", StringComparison.OrdinalIgnoreCase))
            {
                inPersistent = false;
                continue;
            }

            if (line.StartsWith("=", StringComparison.Ordinal)
                || line.StartsWith("Network Destination", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Network Address", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Interface List", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("IPv4 Route Table", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Default Gateway", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("None", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            // В секции «Active Routes» пять колонок (есть адрес интерфейса), в «Persistent Routes» — четыре.
            if (parts.Length < 4 || !LooksLikeAddress(parts[0]) || !LooksLikeAddress(parts[1]))
            {
                continue;
            }

            bool hasInterfaceAddress = parts.Length >= 5;
            bool onLink = parts[2].Equals("On-link", StringComparison.OrdinalIgnoreCase);

            if (!onLink && !LooksLikeAddress(parts[2]))
            {
                continue;
            }

            string metricToken = parts[hasInterfaceAddress ? 4 : 3];
            int metric = metricToken.Equals("Default", StringComparison.OrdinalIgnoreCase)
                ? 0
                : int.TryParse(metricToken, out int parsedMetric) ? parsedMetric : 0;

            entries.Add(new RouteTableEntry(
                Destination: parts[0],
                Netmask: parts[1],
                NextHop: onLink ? "On-link" : parts[2],
                InterfaceAddress: hasInterfaceAddress ? parts[3] : string.Empty,
                Metric: metric,
                IsOnLink: onLink,
                IsPersistent: inPersistent));
        }

        return entries;
    }

    /// <summary>Точка входа для проверки: есть ли уже маршрут для конкретного адреса и какой у него шлюз.</summary>
    public static RouteTableEntry? FindExactHostRoute(
        IReadOnlyList<RouteTableEntry> entries,
        string destinationIp,
        string? nextHop = null)
    {
        foreach (RouteTableEntry entry in entries)
        {
            if (!entry.IsHostRoute || !entry.Destination.Equals(destinationIp, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (nextHop is null || entry.NextHop.Equals(nextHop, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Маршрут по умолчанию — он нужен, чтобы показать пользователю, куда трафик идёт сейчас.</summary>
    public static RouteTableEntry? FindDefaultRoute(IReadOnlyList<RouteTableEntry> entries) =>
        entries.Where(e => e.IsDefaultRoute).OrderBy(e => e.Metric).FirstOrDefault();

    private static bool LooksLikeAddress(string value) =>
        System.Net.IPAddress.TryParse(value, out System.Net.IPAddress? ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}
