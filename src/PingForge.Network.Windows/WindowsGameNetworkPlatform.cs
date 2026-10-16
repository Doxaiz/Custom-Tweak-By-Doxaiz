using System.Runtime.Versioning;
using PingForge.Core.Abstractions;
using PingForge.Network;
using PingForge.Network.Actions;
using PingForge.Network.Qos;
using PingForge.Network.Routes;
using PingForge.Orchestration.Realtime;
using PingForge.Tweaks.Windows;

namespace PingForge.Network.Windows;

/// <summary>
/// Единственное место, где сетевые действия получают живые доступы: таблицу соединений, имена процессов,
/// команду route и реестр. Всё остальное приложение работает через порты и потому проверяемо тестами.
///
/// Здесь же собраны три честных границы, которые видны пользователю в интерфейсе:
///   • без прав администратора чтение работает, а изменение — нет, и об этом сообщается прямо;
///   • для игр с ядерным анти-читом сетевые действия не запускаются автоматически;
///   • маршрут ставится только при явно заданном альтернативном узле (релей или второй канал).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameNetworkPlatform : IDisposable
{
    private readonly WindowsTweakPlatform _platform;

    public WindowsGameNetworkPlatform(
        WindowsTweakPlatform? platform = null,
        GameProcessCatalog? catalog = null,
        bool includeUnknownProcesses = false,
        IRemotePeerTracker? peerTracker = null,
        bool includeIpv6 = false)
    {
        _platform = platform ?? new WindowsTweakPlatform();

        ConnectionTable = new Win32ConnectionTableProvider(includeIpv6);
        ProcessNames = new Win32ProcessNameResolver();
        RouteTable = new RoutePrintTableReader(_platform.Command);

        Watcher = new WindowsGameFlowWatcher(
            ConnectionTable,
            ProcessNames,
            catalog,
            includeUnknownProcesses,
            peerTracker);

        RouteActions = new RouteForGameActionRunner(
            _platform.Command,
            RouteTable,
            () => ActiveFlow,
            () => RouteTarget);

        QosWriter = new QosDscpPolicyWriter(_platform.Registry);
        DscpActions = new DscpMarkingActionRunner(_platform.Registry, () => ActiveFlow);
    }

    public IRawConnectionTableProvider ConnectionTable { get; }

    public IProcessNameResolver ProcessNames { get; }

    public IRouteTableReader RouteTable { get; }

    public IGameFlowWatcher Watcher { get; }

    public IOptimizationActionRunner RouteActions { get; }

    public IOptimizationActionRunner DscpActions { get; }

    public QosDscpPolicyWriter QosWriter { get; }

    /// <summary>Поток, к которому привязываются действия: его ставит оптимизатор перед применением.</summary>
    public GameFlowInfo? ActiveFlow { get; set; }

    /// <summary>Куда вести трафик игры. null — менять маршрут нечем и не нужно (нет второго канала и релея).</summary>
    public RouteOverride? RouteTarget { get; set; }

    /// <summary>Проверка прав: изменение сети без администратора невозможно, и это надо показать заранее.</summary>
    public static bool IsElevated()
    {
        using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public void Dispose() => _platform.Dispose();
}

/// <summary>
/// Сборка действия «маршрут для игры» и «DSCP» в один исполнитель для оптимизатора: тому нужен один
/// объект, который умеет и применять, и снимать, а не два разных.
/// </summary>
public sealed class GameNetworkActionRunner : IOptimizationActionRunner
{
    private readonly IOptimizationActionRunner _routes;
    private readonly IOptimizationActionRunner _qos;

    public GameNetworkActionRunner(IOptimizationActionRunner routes, IOptimizationActionRunner qos)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _qos = qos ?? throw new ArgumentNullException(nameof(qos));
    }

    public Task<OptimizationActionOutcome> ApplyAsync(OptimizationActionDescriptor action, CancellationToken ct = default) =>
        action.Kind == OptimizationActionKind.DscpMarking
            ? _qos.ApplyAsync(action, ct)
            : _routes.ApplyAsync(action, ct);

    public Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct = default) =>
        action.Kind == OptimizationActionKind.DscpMarking
            ? _qos.RevertAsync(action, ct)
            : _routes.RevertAsync(action, ct);
}
