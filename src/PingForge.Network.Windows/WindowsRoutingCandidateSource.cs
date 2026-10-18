using System.Runtime.Versioning;
using PingForge.Core.Abstractions;
using PingForge.Network.Routes;

namespace PingForge.Network.Windows;

/// <summary>
/// Список адаптеров и их IPv4-адресов через штатный PowerShell (Get-NetAdapter + Get-NetIPAddress).
///
/// Почему PowerShell, а не разбор «ipconfig»: вывод ipconfig локализован, и ловить в нём IPv4
/// по русским подписям — путь к тихим ошибкам. Имена свойств cmdlet от языка не зависят, а выводим
/// мы ровно те поля, которые парсит <see cref="AdapterDirectoryParser"/>.
///
/// Только чтение. Никаких попыток «подключиться» к чужому VPN здесь нет и быть не может: программе
/// нужно имя адаптера, а не его состояние.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShellAdapterDirectory : IAdapterDirectory
{
    /// <summary>Одной командой: адаптеры («A|…») и IPv4-адреса интерфейсов («I|…»).</summary>
    public const string QueryRu =
        "Get-NetAdapter | ForEach-Object { 'A|' + $_.ifIndex + '|' + $_.Name + '|' + $_.InterfaceDescription + '|' + $_.Status };" +
        " Get-NetIPAddress -AddressFamily IPv4 | ForEach-Object { 'I|' + $_.ifIndex + '|' + $_.IPAddress }";

    private readonly ICommandPort _command;

    public PowerShellAdapterDirectory(ICommandPort command) =>
        _command = command ?? throw new ArgumentNullException(nameof(command));

    public async Task<IReadOnlyList<AdapterInfo>> ReadAsync(CancellationToken ct = default)
    {
        CommandResult result = await _command
            .RunAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command", QueryRu], ct)
            .ConfigureAwait(false);

        // Чтение не удалось — это «неизвестно», а не «адаптеров нет». Пустой список подписей
        // допустим: узлы останутся в списке, просто без имён адаптеров.
        return result.Succeeded ? AdapterDirectoryParser.Parse(result.StandardOutput) : [];
    }
}

/// <summary>
/// Кандидаты-узлы на живой машине: таблица маршрутов (route print -4) и список адаптеров.
/// Используется службой для «Найти шлюзы» — на том же механизме портов, что и всё остальное чтение.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRoutingCandidateSource(IRouteTableReader table, IAdapterDirectory? adapters = null)
{
    private readonly IRouteTableReader _table = table ?? throw new ArgumentNullException(nameof(table));
    private readonly IAdapterDirectory? _adapters = adapters;

    public Task<IReadOnlyList<RoutingNodeEntry>> DiscoverAsync(CancellationToken ct = default) =>
        RoutingNodeDiscovery.DiscoverAsync(_table, _adapters, ct);
}
