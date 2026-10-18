using PingForge.Network.Routes;
using PingForge.Network.Windows;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;

namespace PingForge.Service.Host;

/// <summary>
/// Реализация команд «Сервер маршрутизации» (задача B) — здесь, в композиционном корне службы,
/// потому что только здесь собираются живые доступы: таблица маршрутов, список адаптеров,
/// замерщик путей и настройки с диска.
///
/// Что эта часть НЕ делает:
///   • не имеет своей сети узлов и не выдаёт выдуманных «серверов PingForge» — кандидаты берутся
///     из таблицы маршрутов и ручного ввода, не больше;
///   • не меняет маршруты без замера: измерение — временные /32 с гарантированным снятием,
///     применение постоянного — только в живой оптимизации по кнопке «Оптимизировать»;
///   • не падает при пустых/битых настройках: чтение терпимое, пустой список — честный отказ с подсказкой.
/// </summary>
public sealed class HostRoutingService : IRoutingService
{
    private readonly WindowsGameNetworkPlatform _network;
    private readonly WindowsRoutingCandidateSource _candidates;
    private readonly RoutePathComparisonService _comparison;
    private readonly Action<string> _log;

    public HostRoutingService(
        WindowsGameNetworkPlatform network,
        WindowsRoutingCandidateSource candidates,
        RoutePathComparisonService comparison,
        Action<string> log)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _comparison = comparison ?? throw new ArgumentNullException(nameof(comparison));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<EngineOperationResult> DiscoverAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RoutingNodeEntry> discovered;
        try
        {
            discovered = await _candidates.DiscoverAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EngineOperationResult.Failure($"Таблица маршрутов не прочиталась: {ex.Message}. Узлы из настроек при этом на месте.");
        }

        // Сохранённые вручную узлы — не «найденные», но человек добавлял их сюда же: показываем
        // их рядом, чтобы окно честно склеило список без второй поездки.
        RoutingSettings saved = ReadSettings();
        IReadOnlyList<RoutingNodeEntry> manual = saved.Nodes
            .Where(node => node.IsManual && discovered.All(found => !found.Address.Equals(node.Address, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        List<RoutingNodeEntry> all = discovered.Concat(manual).ToList();
        string? currentGateway = all.FirstOrDefault(node => node.SourceRu == RoutingNodeSourceRu.CurrentGateway)?.Address;
        int alternatives = all.Count(node => node.SourceRu != RoutingNodeSourceRu.CurrentGateway);

        string message = all.Count == 0
            ? "Ни одного узла не найдено: таблица маршрутов пуста или не прочиталась. Своей сети узлов у программы нет и не будет."
            : alternatives == 0
                ? $"Кандидатов кроме текущего шлюза {(currentGateway is null ? "" : $"({currentGateway}) ")}нет: второго канала или VPN-адаптера в системе не найдено. " +
                  "Сравнивать пути не среди чего — сравнивать имеет смысл только реальные альтернативы."
                : $"Найдено узлов: {all.Count}, из них альтернатив текущему шлюзу: {alternatives}. Источники подписаны честно; «серверов PingForge» здесь не бывает.";

        IReadOnlyList<RoutingCandidateDto> dto = all
            .Select(node => new RoutingCandidateDto(node.Address, node.Name, node.SourceRu, node.SourceRu == RoutingNodeSourceRu.CurrentGateway))
            .ToList();

        _log($"route-discover: узлов {all.Count}, альтернатив {alternatives}.");
        return EngineOperationResult.Success(message, RoutingWireCodec.EncodeCandidates(dto));
    }

    public async Task<EngineOperationResult> MeasureAsync(CancellationToken ct = default)
    {
        RoutingSettings settings = ReadSettings();
        IReadOnlyList<RoutingNodeEntry> nodes = settings.ValidNodes;

        if (nodes.Count == 0)
        {
            return EngineOperationResult.Failure(
                "Измерять нечего: в настройках нет ни одного узла. Нажмите «Найти шлюзы» — программа назовёт реальные " +
                "шлюзы системы (текущий, второй канал, VPN-адаптер) или добавьте IPv4 вручную. Своей сети узлов у нас нет, " +
                "и «ускорителей» мы не выдумываем.");
        }

        IReadOnlyList<GameFlowInfo> flows;
        try
        {
            flows = await _network.Watcher.FindActiveGameFlowsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EngineOperationResult.Failure($"Таблица соединений не прочиталась: {ex.Message}. Пути не измерялись, маршруты не менялись.");
        }

        if (flows.Count == 0)
        {
            return EngineOperationResult.Failure(
                "Запущенная игра не найдена: замер путей делается только до адреса сервера игры — «на весь интернет» " +
                "мы не меряем и пути по умолчанию не трогаем. Запустите игру, зайдите в матч и нажмите кнопку снова.");
        }

        GameFlowInfo flow = flows[0];
        _log($"route-measure: цель {flow.RemoteEndpoint}:{flow.RemotePort}, узлов {nodes.Count}.");

        RoutePathComparisonReport report = await _comparison
            .CompareAsync(flow.RemoteEndpoint, flow.RemotePort, nodes, ct)
            .ConfigureAwait(false);

        RoutingMeasureDto dto = report.ToDto();
        string tail = report.TableRestored
            ? " Временные маршруты сняты: таблица совпадает с исходной."
            : $" ВНИМАНИЕ: {report.TableProblemRu}";

        EngineOperationResult result = new(report.TableRestored, report.VerdictRu + tail, RoutingWireCodec.EncodeMeasure(dto));

        _log($"route-measure: {(report.HasWinner ? $"лучший узел {report.Winner!.Address}" : "без победителя")}; таблица восстановлена: {report.TableRestored}.");
        return result;
    }

    private RoutingSettings ReadSettings()
    {
        RoutingSettings settings = ServiceSettings.ReadRouting(ServiceProgram.DataDirectory, out string? problem);
        if (problem is not null)
        {
            _log($"маршрутизация: {problem}.");
        }

        return settings;
    }
}
