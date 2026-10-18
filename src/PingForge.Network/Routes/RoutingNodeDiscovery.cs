using PingForge.Core.Abstractions;

namespace PingForge.Network.Routes;

/// <summary>
/// Активный адаптер машины — то, что программа знает о «чьем» он шлюзе: имя, описание и IPv4-адреса.
/// Настоящий источник — PowerShell (Get-NetAdapter + Get-NetIPAddress) в слое Windows; в тестах — двойник.
/// </summary>
public sealed record AdapterInfo(
    int IfIndex,
    string Alias,
    string Description,
    bool IsUp,
    IReadOnlyList<string> Ipv4Addresses);

/// <summary>Список адаптеров и их IPv4-адресов. Единственный способ назвать шлюз по адаптеру, а не по индексу.</summary>
public interface IAdapterDirectory
{
    Task<IReadOnlyList<AdapterInfo>> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Разбор вывода PowerShell-запроса про адаптеры и IPv4-адреса. Формат стабильно ASCII-ключи,
/// значения — как их выдаёт Windows; локализация свойств не мешает, потому что имена свойств
/// в запросе фиксированы, а выводим мы «A|индекс|имя|описание|статус» и «I|индекс|адрес».
/// </summary>
public static class AdapterDirectoryParser
{
    public static IReadOnlyList<AdapterInfo> Parse(string powershellOutput)
    {
        var byIndex = new Dictionary<int, (string Alias, string Description, bool IsUp, List<string> Addresses)>();

        foreach (string rawLine in powershellOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length < 3 || line[1] != '|')
            {
                continue;
            }

            string[] parts = line.Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[1].Trim(), out int ifIndex) || ifIndex <= 0)
            {
                continue;
            }

            if (parts[0] == "A" && parts.Length >= 5)
            {
                string alias = parts[2].Trim();
                string description = parts[3].Trim();
                bool up = string.Equals(parts[4].Trim(), "Up", StringComparison.OrdinalIgnoreCase);
                byIndex[ifIndex] = (alias, description, up, new List<string>());
            }
            else if (parts[0] == "I")
            {
                string address = parts[2].Trim();
                if (!byIndex.TryGetValue(ifIndex, out var entry))
                {
                    entry = (string.Empty, string.Empty, true, new List<string>());
                }

                entry.Addresses.Add(address);
                byIndex[ifIndex] = entry;
            }
        }

        return byIndex
            .OrderBy(pair => pair.Key)
            .Select(pair => new AdapterInfo(pair.Key, pair.Value.Alias, pair.Value.Description, pair.Value.IsUp, pair.Value.Addresses))
            .ToList();
    }

    /// <summary>
     /// Классификация адаптера по имени/описанию — только чтением, без единой попытки подключиться.
     /// Список ключевых слов — это НЕ «поддержка сервисов»: так программа понимает, что шлюз принадлежит
     /// туннельному адаптеру, и подписывает узел честно: «VPN-адаптер …».
     /// </summary>
    public static bool LooksLikeVpn(AdapterInfo adapter) =>
        Matches(adapter,
            "wireguard", "wintun", "tun", "tap", "openvpn", "ikev2", "l2tp", "pptp", "vpn",
            "tunnel", "expressvpn", "nordvpn", "proton", "tailscale", "amnezia", "впн");

    /// <summary>Петлевой адаптер — не канал: его шлюзы (127.0.0.1) узлами быть не могут.</summary>
    public static bool LooksLikeLoopback(AdapterInfo adapter) =>
        Matches(adapter, "loopback", "петлевой");

    /// <summary>Виртуальный мост/свитч Hyper-V/Direct — не канал наружу: его шлюзы из поиска исключаем.</summary>
    public static bool LooksLikeVirtualBridge(AdapterInfo adapter) =>
        Matches(adapter, "network bridge", "ブリッジ", "direct virtual", "hyper-v", "virtual ethernet switch", "vEthernet");

    private static bool Matches(AdapterInfo adapter, params string[] keywords)
    {
        string haystack = $"{adapter.Alias} {adapter.Description}".ToLowerInvariant();
        return keywords.Any(keyword => haystack.Contains(keyword, StringComparison.Ordinal));
    }
}

/// <summary>
/// Автопоиск узлов маршрутизации: только реальные шлюзы активных адаптеров из таблицы маршрутов,
/// плюс шлюзы VPN-адаптеров — по их имени, без единой попытки подключиться к чужому VPN.
///
/// Чего здесь принципиально нет: никаких «серверов PingForge», «ускорителей» и прочей выдумки.
/// Каждый найденный узел обязан существовать в переданной таблице маршрутов — и тест проверяет
/// именно это: ни один адрес результата не встречается нигде, кроме таблицы (и ручного ввода).
///
/// Источники подписаны честно:
///   • «текущий шлюз» — следующий узел маршрута по умолчанию с наименьшей метрикой;
///   • «VPN-адаптер “WireGuard”» — шлюз туннельного адаптера (по его имени);
///   • «второй шлюз» — любой другой шлюз из таблицы.
/// </summary>
public static class RoutingNodeDiscovery
{
    public static IReadOnlyList<RoutingNodeEntry> Discover(
        IReadOnlyList<RouteTableEntry> routes,
        IReadOnlyList<AdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(adapters);

        RouteTableEntry? defaultRoute = RouteTableParser.FindDefaultRoute(routes);
        string? currentGateway = defaultRoute?.NextHop;

        // Адаптер по IPv4-адресу интерфейса из строки маршрута — так шлюз получает имя и тип.
        var byAddress = new Dictionary<string, AdapterInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (AdapterInfo adapter in adapters)
        {
            foreach (string address in adapter.Ipv4Addresses)
            {
                byAddress[address] = adapter;
            }
        }

        var found = new Dictionary<string, RoutingNodeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (RouteTableEntry route in routes)
        {
            // On-link — не узел: там следующий шаг — сам интерфейс, вести «через него» некуда.
            if (route.IsOnLink || string.IsNullOrWhiteSpace(route.NextHop))
            {
                continue;
            }

            if (!RoutingNodeValidation.NormalizeIpv4(route.NextHop, out string address))
            {
                continue;
            }

            AdapterInfo? adapter = null;
            if (route.InterfaceAddress.Length > 0)
            {
                byAddress.TryGetValue(route.InterfaceAddress, out adapter);
            }

            if (adapter is { } known && (!known.IsUp || AdapterDirectoryParser.LooksLikeLoopback(known) || AdapterDirectoryParser.LooksLikeVirtualBridge(known)))
            {
                continue;
            }

            if (found.ContainsKey(address))
            {
                continue;
            }

            string adapterName = adapter is not null
                ? (adapter.Alias.Length > 0 ? adapter.Alias : adapter.Description)
                : string.Empty;

            string name = adapterName.Length > 0 ? adapterName : address;
            string source;

            if (currentGateway is not null && address.Equals(currentGateway, StringComparison.OrdinalIgnoreCase))
            {
                source = RoutingNodeSourceRu.CurrentGateway;
            }
            else if (adapter is not null && AdapterDirectoryParser.LooksLikeVpn(adapter))
            {
                source = RoutingNodeSourceRu.VpnAdapter(adapterName);
            }
            else
            {
                source = RoutingNodeSourceRu.SecondGateway;
            }

            found[address] = new RoutingNodeEntry(address, name, source);
        }

        // Порядок: текущий шлюз, затем VPN, затем остальные — внутри групп по алфавиту,
        // чтобы список был предсказуем и не зависел от порядка строк в выводе route print.
        return found.Values
            .GroupBy(node => node.SourceRu == RoutingNodeSourceRu.CurrentGateway
                ? 0
                : node.SourceRu.StartsWith("VPN-адаптер ", StringComparison.Ordinal) ? 1 : 2)
            .OrderBy(group => group.Key)
            .SelectMany(group => group.OrderBy(node => node.Address, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Найти кандидатов на живой системе: таблица маршрутов (route print) и список адаптеров.
    /// Ошибка любого из двух чтений не роняет поиск: адаптеры — только подписи, без них список
    /// остаётся честным, просто с адресами вместо имён.
    /// </summary>
    public static async Task<IReadOnlyList<RoutingNodeEntry>> DiscoverAsync(
        IRouteTableReader table,
        IAdapterDirectory? adapters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);

        IReadOnlyList<RouteTableEntry> routes = await table.ReadAsync(ct).ConfigureAwait(false);

        IReadOnlyList<AdapterInfo> adapterList;
        try
        {
            adapterList = adapters is null ? [] : await adapters.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            adapterList = [];
        }

        return Discover(routes, adapterList);
    }
}
