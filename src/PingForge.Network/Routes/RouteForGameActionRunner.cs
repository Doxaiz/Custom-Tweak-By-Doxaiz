using PingForge.Core.Abstractions;
using PingForge.Network.Routes;
using PingForge.Orchestration.Realtime;

namespace PingForge.Network.Actions;

/// <summary>Куда вести трафик игры: конкретный следующий узел — альтернативный шлюз или узел релея.</summary>
public sealed record RouteOverride(string NextHop, int Metric = 1, string DescriptionRu = "");

/// <summary>
/// Действие «маршрут для игрового сервера».
///
/// Принципиальные ограничения, зашитые в код:
///   • маршрут ставится только на конкретный адрес сервера (маска /32), а не «на весь интернет»;
///   • без заданного следующего узла маршрут НЕ меняется: приложение не угадывает, через какой шлюз
///     вести трафик, и не трогает рабочий маршрут по умолчанию без причины;
///   • существующий маршрут к этому адресу не перезаписывается — он мог быть создан другой программой;
///   • после добавления маршрут перечитывается из системы: «применил» без подтверждения не считается.
/// </summary>
public sealed class RouteForGameActionRunner : IOptimizationActionRunner
{
    private readonly ICommandPort? _command;
    private readonly IRouteTableReader? _table;
    private readonly Func<GameFlowInfo?> _currentFlow;
    private readonly Func<RouteOverride?> _routeOverride;
    private readonly Func<string?> _noTargetNoteRu;

    public RouteForGameActionRunner(
        ICommandPort? command,
        IRouteTableReader? table,
        Func<GameFlowInfo?> currentFlow,
        Func<RouteOverride?>? routeOverride = null,
        Func<string?>? noTargetNoteRu = null)
    {
        _command = command;
        _table = table;
        _currentFlow = currentFlow ?? throw new ArgumentNullException(nameof(currentFlow));
        _routeOverride = routeOverride ?? (() => null);
        _noTargetNoteRu = noTargetNoteRu ?? (() => null);
    }

    public async Task<OptimizationActionOutcome> ApplyAsync(
        OptimizationActionDescriptor action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Kind != OptimizationActionKind.RouteForGame)
        {
            return OptimizationActionOutcome.Fail($"Исполнитель маршрутов не умеет действие «{action.TitleRu}».");
        }

        if (_command is null || _table is null)
        {
            return OptimizationActionOutcome.Fail("Порты маршрутизации не подключены: менять маршруты нечем.");
        }

        GameFlowInfo? flow = _currentFlow();
        if (flow is null)
        {
            return OptimizationActionOutcome.Fail("Игровой поток не найден: маршрут ставить не для чего.");
        }

        if (!RouteCommandBuilder.IsValidAddress(flow.RemoteEndpoint))
        {
            return OptimizationActionOutcome.Fail(
                $"Адрес игрового сервера «{flow.RemoteEndpoint}» не подходит для маршрута: поддерживаются только IPv4-адреса.");
        }

        // Узел берём из самого действия, если он там назван (переключение на резервный путь),
        // и только потом — из «текущей цели» платформы. Иначе при фейловере действие уводило бы
        // трафик на прежний узел: цель в платформе обновляется решением автовыбора, а не переходом.
        RouteOverride? target = ResolveTarget(action);
        if (target is null)
        {
            // Причина отказа бывает разная: узла нет вовсе, узлы есть, но не выбраны, или автовыбор
            // измерил и честно сказал «прямой путь не хуже». Последнее — не «нет узла», и врать
            // под общей формулировкой нельзя: провайдер путей пишет заметку, она идёт в отказ.
            string? decisionNote = _noTargetNoteRu();
            if (!string.IsNullOrWhiteSpace(decisionNote))
            {
                return OptimizationActionOutcome.Fail($"Маршрут не изменён: {decisionNote}");
            }

            return OptimizationActionOutcome.Fail(
                "Маршрут не изменён: не задан альтернативный узел. Выберите его в «Настройки» → «Сервер маршрутизации» " +
                "(кнопка «Найти шлюзы» находит реальные шлюзы, узел можно добавить и вручную). Программа не угадывает, " +
                "через какой шлюз вести трафик, и не трогает существующий маршрут по умолчанию без причины — если " +
                "второго канала или настроенного VPN-адаптера нет, менять здесь нечего.");
        }

        if (!RouteCommandBuilder.IsValidAddress(target.NextHop))
        {
            return OptimizationActionOutcome.Fail($"Следующий узел «{target.NextHop}» не является IPv4-адресом.");
        }

        if (!RouteCommandBuilder.IsValidMetric(target.Metric))
        {
            return OptimizationActionOutcome.Fail($"Метрика {target.Metric} вне диапазона 1…9999.");
        }

        IReadOnlyList<RouteTableEntry> before = await _table.ReadAsync(ct).ConfigureAwait(false);
        RouteTableEntry? existing = RouteTableParser.FindExactHostRoute(before, flow.RemoteEndpoint);

        if (existing is not null)
        {
            if (existing.NextHop.Equals(target.NextHop, StringComparison.OrdinalIgnoreCase))
            {
                return OptimizationActionOutcome.Ok(
                    $"Маршрут к {flow.RemoteEndpoint} уже идёт через {target.NextHop} — менять нечего.");
            }

            return OptimizationActionOutcome.Fail(
                $"К {flow.RemoteEndpoint} уже есть маршрут через {existing.NextHop} (метрика {existing.Metric}). " +
                "Он создан не этим сеансом: чужой маршрут программа не перезаписывает. Удалите его вручную, если он мешает.");
        }

        IReadOnlyList<string> arguments = RouteCommandBuilder.AddHostRoute(flow.RemoteEndpoint, target.NextHop, target.Metric);
        CommandResult result = await _command.RunAsync(RouteCommandBuilder.RouteTool, arguments, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OptimizationActionOutcome.Fail(
                $"Маршрут не добавлен: {FirstLine(result.StandardError) ?? "route.exe вернул ошибку без текста"} " +
                "(нужны права администратора).");
        }

        IReadOnlyList<RouteTableEntry> after = await _table.ReadAsync(ct).ConfigureAwait(false);
        RouteTableEntry? verified = RouteTableParser.FindExactHostRoute(after, flow.RemoteEndpoint, target.NextHop);

        if (verified is null)
        {
            // Применение не подтвердилось — убираем за собой сами, а не оставляем мусор в системе.
            await _command.RunAsync(
                RouteCommandBuilder.RouteTool,
                RouteCommandBuilder.DeleteHostRoute(flow.RemoteEndpoint),
                ct).ConfigureAwait(false);

            return OptimizationActionOutcome.Fail(
                "Команда выполнена, но маршрут в таблице не появился: откатили сразу. " +
                "Такое бывает при политике безопасности или чужом клиенте VPN.");
        }

        string suffix = target.DescriptionRu.Length > 0 ? $" ({target.DescriptionRu})" : string.Empty;
        return OptimizationActionOutcome.Ok(
            $"Маршрут к {flow.RemoteEndpoint} добавлен через {target.NextHop}, метрика {target.Metric}{suffix}. " +
            $"Проверено по таблице маршрутов: {RouteCommandBuilder.Describe(arguments)}.");
    }

    public async Task<OptimizationActionOutcome> RevertAsync(
        OptimizationActionDescriptor action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_command is null || _table is null)
        {
            return OptimizationActionOutcome.Fail("Порты маршрутизации не подключены: снять маршрут нечем.");
        }

        GameFlowInfo? flow = _currentFlow();
        if (flow is null)
        {
            return OptimizationActionOutcome.Fail(
                "Снять маршрут не удалось: неизвестен адрес игрового сервера. Проверьте таблицу маршрутов вручную — " +
                "адрес был записан в журнал операций.");
        }

        IReadOnlyList<RouteTableEntry> entries = await _table.ReadAsync(ct).ConfigureAwait(false);
        RouteTableEntry? entry = RouteTableParser.FindExactHostRoute(entries, flow.RemoteEndpoint);

        if (entry is null)
        {
            return OptimizationActionOutcome.Ok("Маршрута к этому адресу уже нет — снимать нечего.");
        }

        CommandResult result = await _command.RunAsync(
            RouteCommandBuilder.RouteTool,
            RouteCommandBuilder.DeleteHostRoute(flow.RemoteEndpoint),
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OptimizationActionOutcome.Fail(
                $"Маршрут не удалён: {FirstLine(result.StandardError) ?? "route.exe вернул ошибку без текста"}.");
        }

        IReadOnlyList<RouteTableEntry> after = await _table.ReadAsync(ct).ConfigureAwait(false);
        return RouteTableParser.FindExactHostRoute(after, flow.RemoteEndpoint) is null
            ? OptimizationActionOutcome.Ok($"Маршрут к {flow.RemoteEndpoint} снят, трафик снова идёт через шлюз по умолчанию.")
            : OptimizationActionOutcome.Fail("Маршрут всё ещё в таблице: снимать пришлось бы вручную.");
    }

    /// <summary>
    /// Куда вести трафик: узел из самого действия, если он там назван, иначе — текущая цель
    /// платформы. Метрика для явного узла — та же, что у временного маршрута замера
    /// (<see cref="RoutePathComparisonService.ProbeMetric"/>): /32 обязан выигрывать у маршрута
    /// по умолчанию, иначе переключение ничего не изменит.
    /// </summary>
    private RouteOverride? ResolveTarget(OptimizationActionDescriptor action) =>
        action.RouteNextHop is { Length: > 0 } hop
            ? new RouteOverride(hop, RoutePathComparisonService.ProbeMetric, action.TitleRu)
            : _routeOverride();

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[0] : null;
    }
}
