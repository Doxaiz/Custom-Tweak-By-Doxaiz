using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingForge.Analysis;

/// <summary>
/// Сетевое окружение машины: адаптеры, их MTU и текущие DNS-серверы.
/// Все поля — «может быть неизвестно»: null честно означает «прочитать не удалось».
/// </summary>
public sealed record AdapterInventoryItem(
    string InterfaceGuid,
    string Name,
    string Description,
    bool IsUp,
    bool IsPhysical,
    bool IsTunnel,
    bool HasIpv4Gateway,
    int? Ipv4MtuBytes,
    bool IsWireless = false,
    string? Ipv4GatewayAddress = null)
{
    /// <summary>
    /// Похоже ли на VPN/виртуальный адаптер. Список намеренно явный: пользователь должен видеть,
    /// ПОЧЕМУ программа считает адаптер виртуальным, а не получить «магический вывод».
    /// </summary>
    public bool LooksLikeVpn => IsTunnel || VpnNameMarkers.Any(marker =>
        Name.Contains(marker, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] VpnNameMarkers =
    {
        "vpn", "wireguard", "openvpn", "tap-", "tap0", "tun0", "tunnel", "tailscale",
        "zerotier", "hamachi", "radmin", "softether", "warp", "windscribe", "proton",
        "nordlynx", "neorouter"
    };
}

public sealed record NetworkContextSnapshot(
    IReadOnlyList<AdapterInventoryItem> Adapters,
    int? ActiveAdapterMtuBytes,
    IReadOnlyList<string> ConfiguredDnsServers,
    string? ActiveAdapterName,
    IReadOnlyList<string> UnresolvedFields)
{
    public IReadOnlyList<AdapterInventoryItem> PhysicalUpAdapters => Adapters
        .Where(a => a.IsUp && !a.IsTunnel && !a.LooksLikeVpn)
        .ToList();

    public IReadOnlyList<AdapterInventoryItem> ActiveVpnAdapters => Adapters
        .Where(a => a.IsUp && a.LooksLikeVpn)
        .ToList();
}

/// <summary>Чтение окружения. Реализация по умолчанию работает через System.Net.NetworkInformation.</summary>
public interface INetworkContextReader
{
    Task<NetworkContextSnapshot> ReadAsync(CancellationToken ct = default);
}

/// <summary>Откуда берётся адрес игрового сервера: MTU и маршрут считаются до конкретного сервера.</summary>
public interface IGameServerEndpointProvider
{
    Task<IPAddress?> TryGetGameServerAddressAsync(CancellationToken ct = default);
}

/// <summary>
/// Кроссплатформенное чтение окружения. На Windows приложение дополняет его тем, чего нет в этом API
/// (права администратора, активный адаптер через таблицу маршрутов) — здесь всё, что можно прочитать честно.
/// </summary>
public sealed class SystemNetworkContextReader : INetworkContextReader
{
    public Task<NetworkContextSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var adapters = new List<AdapterInventoryItem>();
        var unresolved = new List<string>();

        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                IPInterfaceProperties props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                int? mtu = props.GetIPv4Properties()?.Mtu;

                string? gatewayAddress = props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                    .Select(g => g.Address.ToString())
                    .FirstOrDefault();

                adapters.Add(new AdapterInventoryItem(
                    InterfaceGuid: ni.Id,
                    Name: ni.Name,
                    Description: ni.Description,
                    IsUp: ni.OperationalStatus == OperationalStatus.Up,
                    IsPhysical: ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.GigabitEthernet
                        or NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.Ethernet3Megabit
                        or NetworkInterfaceType.FastEthernetFx
                        or NetworkInterfaceType.FastEthernetT,
                    IsTunnel: ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel,
                    HasIpv4Gateway: hasGateway,
                    Ipv4MtuBytes: mtu,
                    IsWireless: ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    Ipv4GatewayAddress: gatewayAddress));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                unresolved.Add($"Адаптер «{ni.Name}»: {ex.Message}");
            }
        }

        AdapterInventoryItem? active = adapters
            .Where(a => a.IsUp && !a.IsTunnel && a.HasIpv4Gateway)
            .OrderBy(a => a.LooksLikeVpn)
            .ThenByDescending(a => a.IsPhysical)
            .FirstOrDefault();

        var dns = new List<string>();
        if (active is not null)
        {
            try
            {
                NetworkInterface? ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Id == active.InterfaceGuid);

                if (ni is not null)
                {
                    foreach (IPAddress address in ni.GetIPProperties().DnsAddresses)
                    {
                        if (address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            dns.Add(address.ToString());
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                unresolved.Add($"DNS активного адаптера: {ex.Message}");
            }
        }

        if (adapters.Count == 0)
        {
            unresolved.Add("Адаптеры не прочитаны: система не отдала список сетевых интерфейсов.");
        }

        return Task.FromResult(new NetworkContextSnapshot(
            Adapters: adapters,
            ActiveAdapterMtuBytes: active?.Ipv4MtuBytes,
            ConfiguredDnsServers: dns,
            ActiveAdapterName: active?.Name,
            UnresolvedFields: unresolved));
    }
}
