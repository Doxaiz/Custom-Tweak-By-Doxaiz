using System.Net.NetworkInformation;

namespace PingForge.Orchestration;

/// <summary>Смена сети/сон/VPN (задача P): замечает и просит перепроверить, ничего не применяет автоматом.
/// Двойник событий — для тестов без Windows.</summary>
public sealed class NetworkStateMonitor
{
    public enum NetworkEventKind { Sleep, Wake, AdapterChanged, NetworkLost, NetworkRestored, VpnDetected }

    public sealed record NetworkEvent(NetworkEventKind Kind, string MessageRu, DateTime AtUtc);

    private readonly Action<NetworkEvent> _onEvent;
    private readonly Func<bool> _isVpnPresent;

    public NetworkStateMonitor(Action<NetworkEvent> onEvent, Func<bool>? isVpnPresent = null)
    {
        _onEvent = onEvent;
        _isVpnPresent = isVpnPresent ?? (() => NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Contains("VPN", StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == OperationalStatus.Up));
    }

    public void NotifySleep() => _onEvent(new(NetworkEventKind.Sleep, "Сессия на паузе: компьютер заснул — замеры устарели.", DateTime.UtcNow));
    public void NotifyWake() => _onEvent(new(NetworkEventKind.Wake, "Пробуждение: перепроверяю поток игры; старые цифры не показываю.", DateTime.UtcNow));
    public void NotifyAdapterChanged(string? detail = null) => _onEvent(new(NetworkEventKind.AdapterChanged, $"Сменился адаптер/шлюз{(detail is null ? "" : $": {detail}")} — маршрут снят, путь изменился.", DateTime.UtcNow));
    public void NotifyNetworkLost() => _onEvent(new(NetworkEventKind.NetworkLost, "Сеть пропала — новых замеров нет.", DateTime.UtcNow));
    public void NotifyNetworkRestored() => _onEvent(new(NetworkEventKind.NetworkRestored, "Сеть вернулась — перепроверяю поток; старые цифры не показываю.", DateTime.UtcNow));
    public void CheckVpn()
    {
        if (_isVpnPresent()) _onEvent(new(NetworkEventKind.VpnDetected, "Путь идёт через VPN-адаптер — задержка может быть больше из-за третьего узла.", DateTime.UtcNow));
    }
}
