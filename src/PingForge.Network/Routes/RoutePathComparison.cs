using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PingForge.Core.Abstractions;
using PingForge.Network.Actions;
using PingForge.Orchestration.Realtime;

namespace PingForge.Network.Routes;

/// <summary>
/// Результат замера одного пути: прямой или через узел.
/// <paramref name="Success"/> = false — узел не измерился, <paramref name="ProblemRu"/> объясняет почему.
/// Чисел без измерения не бывает: <paramref name="MedianRttMs"/> null ровно тогда, когда Success false.
/// </summary>
public sealed record RoutePathProbeResult(
    string Address,
    string Name,
    string SourceRu,
    bool Success,
    double? MedianRttMs,
    double? JitterMs,
    double? LossPercent,
    string? ProblemRu,
    double? SpreadMs = null);

/// <summary>Итог сравнения путей до одного адреса: честный вердикт и состояние таблицы маршрутов.</summary>
public sealed record RoutePathComparisonReport(
    string TargetIp,
    RoutePathProbeResult? Direct,
    IReadOnlyList<RoutePathProbeResult> ViaNodes,
    RoutingNodeEntry? Winner,
    RoutingNodeEntry? RunnerUp,
    string VerdictRu,
    bool TableRestored,
    string? TableProblemRu)
{
    /// <summary>Есть ли смысл что-то применять: победитель находится только при измеренном выигрыше.</summary>
    public bool HasWinner => Winner is not null;
}

/// <summary>
/// Сравнение путей (задача B2): прямой замер до адреса игрового сервера, затем для каждого узла —
/// временный маршрут /32 ТОЛЬКО на этот адрес, тот же метод замера, снятие временного маршрута
/// в finally при любом исходе. После всех прогонов таблица перечитывается и сверяется с исходной:
/// «убрали за собой» — это проверка, а не обещание.
///
/// Правила, зашитые в код:
///   • чужой маршрут /32 на этот адрес не перезаписывается: если он уже есть — сравнение путей
///     не проводится, это решение другой программы (тот же принцип, что у RouteForGameActionRunner);
///   • победа по медиане RTT, при равенстве медиан — по джиттеру, затем по потерям; узел должен
///     быть лучше прямого пути не менее чем на <see cref="NotableImprovementMs"/>, иначе вердикт
///     честно говорит «прямой путь не хуже, ничего не меняем»;
///   • нет выигрыша — победителя нет: молча «оптимизировать ради надписи» нельзя.
/// </summary>
public sealed class RoutePathComparisonService
{
    /// <summary>Минимальный заметный выигрыш медианы, мс. Меньше — это погрешность измерения, а не победа.</summary>
    public const double NotableImprovementMs = 0.5;

    /// <summary>
    /// Сколько серий замера делает каждый путь. Две серии вперемешку (прямой → узел А → узел Б →
    /// прямой → …) вместо «прямой один раз в начале, узлы после»: сеть дрейфует, и один замер
    /// в начале дал бы одной стороне фору, которую потом нечем заметить. Обе серии обязаны
    /// измериться: одна серия — это не медиана, а один случай.
    /// </summary>
    public const int MeasurementRounds = 2;

    /// <summary>Метрика временного маршрута замера: минимальная, чтобы /32 точно выигрывал у маршрута по умолчанию.</summary>
    public const int ProbeMetric = 1;

    private readonly ICommandPort _command;
    private readonly IRouteTableReader _table;
    private readonly IRealtimeMeasurementSource _measurement;

    public RoutePathComparisonService(ICommandPort command, IRouteTableReader table, IRealtimeMeasurementSource measurement)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _measurement = measurement ?? throw new ArgumentNullException(nameof(measurement));
    }

    public async Task<RoutePathComparisonReport> CompareAsync(
        string targetIp,
        int port,
        IReadOnlyList<RoutingNodeEntry> nodes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (!RoutingNodeValidation.NormalizeIpv4(targetIp, out string target))
        {
            return new RoutePathComparisonReport(
                targetIp, null, [], null, null,
                $"Адрес сервера «{targetIp}» не IPv4: временный маршрут на имя не ставится, сравнивать пути не буду.",
                TableRestored: true, TableProblemRu: null);
        }

        IReadOnlyList<RouteTableEntry> before = await _table.ReadAsync(ct).ConfigureAwait(false);

        if (RouteTableParser.FindExactHostRoute(before, target) is { } foreign)
        {
            // К адресу уже есть /32, созданный не нами: перелезать через него нельзя — ровно то же
            // правило, что у действия «маршрут для игры».
            return new RoutePathComparisonReport(
                target,
                await MeasureDirectAsync(target, port, ct).ConfigureAwait(false),
                nodes.Select(node => new RoutePathProbeResult(
                    node.Address, node.Name, node.SourceRu, false, null, null, null,
                    $"к {target} уже есть маршрут через {foreign.NextHop} (метрика {foreign.Metric}) — чужой маршрут не перезаписываю")).ToList(),
                null, null,
                $"К {target} уже есть маршрут /32, созданный не программой: сравнивать пути не буду, чтобы его не трогать. " +
                "Ничего не меняем.",
                TableRestored: true, TableProblemRu: null);
        }

        // Шлюз маршрута по умолчанию из исходной таблицы: идти «через него» — это прямой путь.
        string defaultGateway = RouteTableParser.FindDefaultRoute(before)?.NextHop ?? string.Empty;

        string? tableProblem = null;
        bool restored = true;

        // Что замеряем временным /32: узлы со строгим IPv4, кроме текущего шлюза (это тот же
        // прямой путь). Остальные узлы получают честный ответ без замеров.
        var pending = new List<(int Index, RoutingNodeEntry Node, string Hop)>();
        var fixedResults = new Dictionary<int, RoutePathProbeResult>();
        var gatewayIndexes = new List<int>();

        for (int i = 0; i < nodes.Count; i++)
        {
            RoutingNodeEntry node = nodes[i];

            if (!RoutingNodeValidation.NormalizeIpv4(node.Address, out string hop))
            {
                fixedResults[i] = new RoutePathProbeResult(node.Address, node.Name, node.SourceRu, false, null, null, null,
                    "адрес узла не похож на строгий IPv4 — пропускаю, угадывать не стану");
                continue;
            }

            if (defaultGateway.Length > 0 && hop.Equals(defaultGateway, StringComparison.OrdinalIgnoreCase))
            {
                // Временный /32 к тому же шлюзу ничего не меняет, а лишний маршрут в системе
                // на время замера не нужен: числа берём у прямого пути.
                gatewayIndexes.Add(i);
                continue;
            }

            pending.Add((i, node, hop));
        }

        var directRounds = new List<RoutePathProbeResult>(MeasurementRounds);
        var nodeRounds = new Dictionary<int, List<RoutePathProbeResult>>();

        for (int round = 0; round < MeasurementRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            // После первой серии чередовать нечего — не тратим замер впустую.
            if (round > 0 && pending.Count == 0)
            {
                break;
            }

            RoutePathProbeResult directRound = await MeasureDirectAsync(target, port, ct).ConfigureAwait(false);
            directRounds.Add(directRound);

            for (int p = 0; p < pending.Count; p++)
            {
                (int index, RoutingNodeEntry node, string hop) = pending[p];

                (bool added, string? addProblem) = await TryAddTempRouteAsync(target, hop, ct).ConfigureAwait(false);
                if (!added)
                {
                    // Маршрут не поставился — повторять в следующей серии бессмысленно: ответ уже честный.
                    // Если первая серия состоялась, вторая «пустая» отменяет путь целиком: одна серия —
                    // это не медиана (MergeRounds так и скажет).
                    var failedRound = new RoutePathProbeResult(node.Address, node.Name, node.SourceRu, false, null, null, null, addProblem);
                    if (nodeRounds.TryGetValue(index, out List<RoutePathProbeResult>? already))
                    {
                        already.Add(failedRound);
                    }
                    else
                    {
                        fixedResults[index] = failedRound;
                    }

                    pending.RemoveAt(p);
                    p--;
                    continue;
                }

                PathMeasurement window;
                string? measureProblem = null;
                try
                {
                    window = await _measurement.MeasurePathAsync(target, Math.Max(0, port), ct).ConfigureAwait(false);
                    if (!window.IsMeasurable)
                    {
                        measureProblem = window.MethodRu;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    window = PathMeasurement.Unmeasurable("замер не выполнился");
                    measureProblem = $"замер упал ({ex.GetType().Name}: {ex.Message})";
                }
                finally
                {
                    // Снятие временного маршрута — в finally при любом исходе: и когда замер ответил,
                    // и когда бросил исключение.
                    (bool removed, string? removeProblem) = await RemoveTempRouteAndVerifyAsync(target, hop, ct).ConfigureAwait(false);
                    if (!removed)
                    {
                        restored = false;
                        tableProblem = removeProblem;
                    }
                }

                if (!nodeRounds.TryGetValue(index, out List<RoutePathProbeResult>? rounds))
                {
                    rounds = new List<RoutePathProbeResult>(MeasurementRounds);
                    nodeRounds[index] = rounds;
                }

                rounds.Add(new RoutePathProbeResult(
                    hop, node.Name, node.SourceRu,
                    window.IsMeasurable,
                    window.IsMeasurable ? Math.Round(window.RttMedianMs, 2) : null,
                    window.IsMeasurable ? Math.Round(window.JitterMs, 2) : null,
                    window.IsMeasurable ? Math.Round(window.LossPercent, 2) : null,
                    measureProblem));

                if (!restored)
                {
                    // Временный /32 не снялся — продолжать замеры нельзя: сначала честно сообщаем о проблеме.
                    break;
                }
            }

            if (!restored)
            {
                break;
            }
        }

        RoutePathProbeResult direct = MergeRounds(target, "прямой путь", "замер без изменения маршрутов", directRounds)
            ?? new RoutePathProbeResult(target, "прямой путь", "замер без изменения маршрутов", false, null, null, null,
                "прямой замер не выполнялся");

        var via = new List<RoutePathProbeResult>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            RoutingNodeEntry node = nodes[i];

            if (fixedResults.TryGetValue(i, out RoutePathProbeResult? fixedResult))
            {
                via.Add(fixedResult);
                continue;
            }

            if (gatewayIndexes.Contains(i))
            {
                via.Add(new RoutePathProbeResult(node.Address, node.Name, node.SourceRu, direct.Success,
                    direct.MedianRttMs, direct.JitterMs, direct.LossPercent,
                    direct.Success ? "совпадает с текущим шлюзом — это тот же прямой путь" : direct.ProblemRu,
                    direct.SpreadMs));
                continue;
            }

            RoutePathProbeResult? merged = nodeRounds.TryGetValue(i, out List<RoutePathProbeResult>? rounds)
                ? MergeRounds(node.Address, node.Name, node.SourceRu, rounds)
                : null;

            via.Add(merged ?? new RoutePathProbeResult(node.Address, node.Name, node.SourceRu, false, null, null, null,
                "замер через этот узел не выполнялся: путь не проверен"));
        }

        (bool finalRestored, string? finalProblem) = await VerifyTableMatchesAsync(before, ct).ConfigureAwait(false);
        restored = restored && finalRestored;
        tableProblem ??= finalProblem;

        return BuildVerdict(target, direct, via, nodes, restored, tableProblem);
    }

    /// <summary>
    /// Свести серии в одно число: медиана медиан и разброс (максимум минус минимум).
    /// Одна неудавшаяся серия отменяет весь путь: выдавать одну серию за медиану нельзя —
    /// именно так и появляются «выигрыши» из случайного скачка.
    /// </summary>
    private static RoutePathProbeResult? MergeRounds(
        string address,
        string name,
        string sourceRu,
        IReadOnlyList<RoutePathProbeResult> rounds)
    {
        if (rounds.Count == 0)
        {
            return null;
        }

        int failedIndex = -1;
        for (int i = 0; i < rounds.Count; i++)
        {
            if (!rounds[i].Success)
            {
                failedIndex = i;
                break;
            }
        }

        if (failedIndex >= 0)
        {
            RoutePathProbeResult failed = rounds[failedIndex];
            string reason = failed.ProblemRu ?? "замер не удался";
            return failed with
            {
                ProblemRu = rounds.Count > 1
                    ? $"{reason} (серия {failedIndex + 1} из {rounds.Count}: путь не подтверждён)"
                    : reason
            };
        }

        List<double> medians = rounds.Select(round => round.MedianRttMs!.Value).ToList();
        double spread = medians.Max() - medians.Min();

        return new RoutePathProbeResult(
            rounds[0].Address,
            name,
            sourceRu,
            true,
            Math.Round(Median(medians), 2),
            Math.Round(Median(rounds.Select(round => round.JitterMs ?? 0).ToList()), 2),
            Math.Round(Median(rounds.Select(round => round.LossPercent ?? 0).ToList()), 2),
            null,
            Math.Round(spread, 2));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        List<double> sorted = values.OrderBy(value => value).ToList();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>
    /// Какой выигрыш медианы считать настоящим: не меньше <see cref="NotableImprovementMs"/>, и
    /// обязательно больше разброса серий обоих путей. Иначе сеть дрейфанула, а мы бы записали это
    /// в победу маршрута.
    /// </summary>
    public static double RequiredGainMs(RoutePathProbeResult first, RoutePathProbeResult second) =>
        Math.Max(NotableImprovementMs, Math.Max(first.SpreadMs ?? 0, second.SpreadMs ?? 0));

    private async Task<RoutePathProbeResult> MeasureDirectAsync(string target, int port, CancellationToken ct)
    {
        try
        {
            PathMeasurement direct = await _measurement.MeasurePathAsync(target, Math.Max(0, port), ct).ConfigureAwait(false);
            return new RoutePathProbeResult(target, "прямой путь", "замер без изменения маршрутов", direct.IsMeasurable,
                direct.IsMeasurable ? Math.Round(direct.RttMedianMs, 2) : null,
                direct.IsMeasurable ? Math.Round(direct.JitterMs, 2) : null,
                direct.IsMeasurable ? Math.Round(direct.LossPercent, 2) : null,
                direct.IsMeasurable ? null : direct.MethodRu);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RoutePathProbeResult(target, "прямой путь", "замер без изменения маршрутов", false, null, null, null,
                $"прямой замер упал ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private async Task<(bool Added, string? Problem)> TryAddTempRouteAsync(string target, string hop, CancellationToken ct)
    {
        CommandResult result = await _command
            .RunAsync(RouteCommandBuilder.RouteTool, RouteCommandBuilder.AddHostRoute(target, hop, ProbeMetric), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            string reason = FirstLine(result.StandardError) ?? "route.exe вернул ошибку без текста";
            return (false, $"временный маршрут не поставлен: {reason}. Для замера нужны права администратора " +
                "(служба делает это от LocalSystem, окно без прав — нет).");
        }

        IReadOnlyList<RouteTableEntry> afterAdd = await _table.ReadAsync(ct).ConfigureAwait(false);
        if (RouteTableParser.FindExactHostRoute(afterAdd, target, hop) is null)
        {
            await _command.RunAsync(RouteCommandBuilder.RouteTool, RouteCommandBuilder.DeleteHostRoute(target), ct).ConfigureAwait(false);
            return (false, "временный маршрут не появился в таблице — замер через этот узел отменён");
        }

        return (true, null);
    }

    private async Task<(bool Restored, string? Problem)> RemoveTempRouteAndVerifyAsync(
        string target,
        string hop,
        CancellationToken ct)
    {
        CommandResult delete = await _command
            .RunAsync(RouteCommandBuilder.RouteTool, RouteCommandBuilder.DeleteHostRoute(target), ct)
            .ConfigureAwait(false);

        IReadOnlyList<RouteTableEntry> now = await _table.ReadAsync(ct).ConfigureAwait(false);
        RouteTableEntry? leftover = RouteTableParser.FindExactHostRoute(now, target, hop);

        if (leftover is null)
        {
            return (true, null);
        }

        // Маршрут всё ещё в таблице — второй заход, и только потом честный сигнал тревоги.
        await _command.RunAsync(RouteCommandBuilder.RouteTool, RouteCommandBuilder.DeleteHostRoute(target), ct).ConfigureAwait(false);
        now = await _table.ReadAsync(ct).ConfigureAwait(false);

        return RouteTableParser.FindExactHostRoute(now, target, hop) is null
            ? (true, null)
            : (false, $"временный /32 к {target} через {hop} НЕ снялся (route delete: {FirstLine(delete.StandardError) ?? "без текста"}): удалите его вручную, чужие маршруты программа не трогает.");
    }

    /// <summary>Финальная сверка: после всех замеров таблица обязана совпадать с исходной.</summary>
    private async Task<(bool Restored, string? Problem)> VerifyTableMatchesAsync(
        IReadOnlyList<RouteTableEntry> before,
        CancellationToken ct)
    {
        IReadOnlyList<RouteTableEntry> now = await _table.ReadAsync(ct).ConfigureAwait(false);

        string[] original = before.Select(entry => entry.Key + "|" + entry.Metric).Distinct().ToArray();
        string[] current = now.Select(entry => entry.Key + "|" + entry.Metric).Distinct().ToArray();

        string[] added = current.Except(original).ToArray();
        string[] removed = original.Except(current).ToArray();

        if (added.Length == 0 && removed.Length == 0)
        {
            return (true, null);
        }

        var parts = new List<string>();
        if (added.Length > 0)
        {
            parts.Add($"остались строки: {string.Join("; ", added)}");
        }

        if (removed.Length > 0)
        {
            parts.Add($"исчезли строки, которых было до замера: {string.Join("; ", removed)}");
        }

        return (false, $"таблица маршрутов после замера не совпадает с исходной — {string.Join("; ", parts)}. Сверьте «route print -4» вручную.");
    }

    private RoutePathComparisonReport BuildVerdict(
        string target,
        RoutePathProbeResult direct,
        List<RoutePathProbeResult> via,
        IReadOnlyList<RoutingNodeEntry> nodes,
        bool restored,
        string? tableProblem)
    {
        string tableTail = restored ? string.Empty : $" ВНИМАНИЕ: {tableProblem}";

        if (nodes.Count == 0)
        {
            return new RoutePathComparisonReport(target, direct, via, null, null,
                "Узлов для сравнения нет: искать не среди чего. Добавьте второй шлюз или сохраните узел вручную — " +
                "и только потом измеряйте." + tableTail,
                restored, tableProblem);
        }

        if (!direct.Success)
        {
            string reason = direct.ProblemRu ?? "прямой путь не измерился";
            return new RoutePathComparisonReport(target, direct, via, null, null,
                $"Прямой путь до {target} не измерился ({reason}): сравнивать честно не с чем, узел не выбираю, " +
                "маршруты не меняю." + tableTail,
                restored, tableProblem);
        }

        List<RoutePathProbeResult> measured = via
            .Where(result => result.Success && result.MedianRttMs.HasValue)
            .ToList();

        if (measured.Count == 0)
        {
            string problems = string.Join("; ", via
                .Where(result => result.ProblemRu is not null)
                .Select(result => $"«{result.Name}»: {result.ProblemRu}")
                .Take(3));

            return new RoutePathComparisonReport(target, direct, via, null, null,
                $"Ни один узел не измерился до {target} ({(problems.Length > 0 ? problems : "замеры не задались")}). " +
                $"Прямой путь: медиана {FormatMs(direct.MedianRttMs!.Value)} мс — его и оставляем, ничего не меняем." + tableTail,
                restored, tableProblem);
        }

        // Победитель среди узлов: медиана → джиттер → потери.
        List<RoutePathProbeResult> sorted = measured
            .OrderBy(result => result.MedianRttMs!.Value)
            .ThenBy(result => result.JitterMs ?? double.MaxValue)
            .ThenBy(result => result.LossPercent ?? double.MaxValue)
            .ToList();
        RoutePathProbeResult best = sorted[0];
        RoutingNodeEntry? winner = nodes.FirstOrDefault(node =>
            node.Address.Equals(best.Address, StringComparison.OrdinalIgnoreCase));
        RoutingNodeEntry? runnerUp = null;
        if (sorted.Count > 1)
        {
            RoutePathProbeResult second = sorted[1];
            runnerUp = nodes.FirstOrDefault(node =>
                node.Address.Equals(second.Address, StringComparison.OrdinalIgnoreCase));
        }

        double gain = direct.MedianRttMs!.Value - best.MedianRttMs!.Value;

        // Порог — не только «0,5 мс», но и разброс серий: сеть дрейфует, и выигрыш меньше
        // собственного разброса замеров — это шум, а не победа маршрута.
        double required = RequiredGainMs(direct, best);
        string spreadNote = required > NotableImprovementMs
            ? $" Разброс серий замера ±{FormatMs(required)} мс, поэтому меньший выигрыш не засчитывается."
            : string.Empty;

        if (gain >= required)
        {
            string tieNote = measured.Count > 1
                ? $" Лучший из {measured.Count} измеренных (сравнение: медиана, при равенстве — джиттер, затем потери)."
                : string.Empty;
            string runnerNote = runnerUp is not null
                ? $" Резерв — «{runnerUp.Name}» ({runnerUp.Address}): горячий шлюз на случай просадки основного."
                : string.Empty;

            return new RoutePathComparisonReport(target, direct, via, winner, runnerUp,
                $"Через «{best.Name}» ({best.Address}) медиана {FormatMs(best.MedianRttMs!.Value)} мс против " +
                $"{FormatMs(direct.MedianRttMs.Value)} мс напрямую — выигрыш {FormatMs(gain)} мс. Этот путь можно применять." +
                tieNote + runnerNote + spreadNote + tableTail,
                restored, tableProblem);
        }

        if (gain > -required)
        {
            return new RoutePathComparisonReport(target, direct, via, null, runnerUp,
                $"Прямой путь не хуже: медиана {FormatMs(direct.MedianRttMs.Value)} мс против " +
                $"{FormatMs(best.MedianRttMs!.Value)} мс через «{best.Name}» — разница в пределах погрешности. Ничего не меняем." +
                spreadNote + tableTail,
                restored, tableProblem);
        }

        return new RoutePathComparisonReport(target, direct, via, null, runnerUp,
            $"Прямой путь короче на {FormatMs(Math.Abs(gain))} мс (медиана {FormatMs(direct.MedianRttMs.Value)} против " +
            $"{FormatMs(best.MedianRttMs!.Value)} через «{best.Name}») — ничего не меняем." + tableTail,
            restored, tableProblem);
    }

    private static string FormatMs(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

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

/// <summary>
/// Проводник настроек маршрутизации до живого оптимизатора. Единственный источник
/// WindowsGameNetworkPlatform во время оптимизации — так решение
/// о пути принимается по замерам и ровно тогда, когда пользователь нажал «Оптимизировать».
///
/// Правила:
///   • узлов нет — целевой узел не задается: действие «маршрут для игры» честно откажет и подскажет,
///     где выбрать узел; маршрут по умолчанию не трогается;
///   • выбран узел по умолчанию (автовыбор выключен) — используется ровно он, без «умников»;
///   • автовыбор — сравнение путей через <see cref="RoutePathComparisonService"/>: победил узел —
///     он становится целью; иначе цель снимается, и причина («прямой путь не хуже…») уходит
///     в отказ действия вместо невнятного «не задан альтернативный узел».
///
/// Сюда же, в единую точку, приходит и поток оптимизации: аргумент applyFlow конструктора привязывает
/// действие к адресу найденной игры (иначе маршруту не для чего применяться). Провайдер вызывает
/// оптимизатор — то есть ровно тогда, когда человек нажал «Оптимизировать», и никогда при запуске.
/// </summary>
public sealed class RoutingPathOptionsProvider : IRealtimePathOptionsProvider
{
    private readonly Func<RoutingSettings> _readSettings;
    private readonly RoutePathComparisonService? _comparison;
    private readonly Action<RouteOverride?> _applyTarget;
    private readonly Action<string?> _applyRefusalNote;
    private readonly Action<GameFlowInfo>? _applyFlow;

    public RoutingPathOptionsProvider(
        Func<RoutingSettings> readSettings,
        Action<RouteOverride?> applyTarget,
        Action<string?> applyRefusalNote,
        RoutePathComparisonService? comparison = null,
        Action<GameFlowInfo>? applyFlow = null)
    {
        _readSettings = readSettings ?? throw new ArgumentNullException(nameof(readSettings));
        _applyTarget = applyTarget ?? throw new ArgumentNullException(nameof(applyTarget));
        _applyRefusalNote = applyRefusalNote ?? throw new ArgumentNullException(nameof(applyRefusalNote));
        _comparison = comparison;
        _applyFlow = applyFlow;
    }

    /// <summary>Последний вердикт сравнения — его показывают статус оптимизации и живые проверки.</summary>
    public string? LastDecisionRu { get; private set; }

    public string? LastMultipathRu { get; private set; }

    public RouteOverride? LastSecondaryRoute { get; private set; }

    /// <summary>
    /// Сколько живёт решение автовыбора: при деградации оптимизатор спрашивает варианты снова,
    /// и каждый вопрос ценой в полноценное сравнение путей был бы пыткой для сети. Пока ответ свежий
    /// и поток тот же — решение переиспользуется (это всё те же измеренные числа, а не угадка).
    /// </summary>
    public static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(240);

    private string? _cachedFlowKey;
    private DateTime _cachedUtc;
    private IReadOnlyList<RealtimePathOption> _cachedOptions = [];

    /// <summary>Забыть решение: новая оптимизация обязана мерить заново, а не жить вчерашней победой.</summary>
    public void Invalidate()
    {
        _cachedFlowKey = null;
        _cachedOptions = [];
    }

    /// <summary>
    /// Ключ решения. В него входят не только процесс и его PID, но и адрес и порт сервера:
    /// матчмейкер выдаёт новый сервер, не перезапуская игру, и «тот же процесс» — это уже другой матч.
    /// Без адреса и порта второй матч получил бы вердикт первого и не мерился бы заново.
    /// </summary>
    public static string DecisionKey(GameFlowInfo flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return $"{flow.Key}|{flow.RemoteEndpoint}:{flow.RemotePort}";
    }

    public async Task<IReadOnlyList<RealtimePathOption>> GetOptionsAsync(GameFlowInfo flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);

        _applyFlow?.Invoke(flow);

        RoutingSettings settings = _readSettings() ?? RoutingSettings.Empty;
        IReadOnlyList<RoutingNodeEntry> nodes = settings.ValidNodes;

        if (nodes.Count == 0)
        {
            _cachedOptions = [];
            _applyTarget(null);
            _applyRefusalNote(null);
            LastDecisionRu = "Узлов маршрутизации нет: действие «маршрут для игры» откажет и подскажет, " +
                "где выбрать узел («Настройки» → «Сервер маршрутизация»).";
            return [];
        }

        if (!settings.AutoSelectBest)
        {
            RoutingNodeEntry? chosen = settings.DefaultNode;
            if (chosen is null)
            {
                _cachedOptions = [];
                _applyTarget(null);
                _applyRefusalNote("узлы сохранены, но ни один не выбран по умолчанию и автовыбор выключен: " +
                    "маршрут не меняется, пока человек не укажет узел.");
                LastDecisionRu = "Узел по умолчанию не выбран — маршруты не меняю.";
                return [];
            }

            _applyTarget(new RouteOverride(chosen.Address, RoutePathComparisonService.ProbeMetric, $"{chosen.SourceRu}: {chosen.Name}"));
            _applyRefusalNote(null);
            LastDecisionRu = $"Веду трафик игры через выбранный узел «{chosen.Name}» ({chosen.Address}).";

            // Ручной выбор — это и основной путь, и (если человек включил мультипуть) резерв:
            // при подтверждённой просадке основного трафик можно увести на второй шлюз.
            var manual = new List<RealtimePathOption> { OptionFor(chosen, isPrimary: true) };
            if (settings.IsMultipathValid && settings.SecondaryNode is { } secondary
                && !secondary.Address.Equals(chosen.Address, StringComparison.OrdinalIgnoreCase))
            {
                manual.Add(OptionFor(secondary, isPrimary: false));
            }

            _cachedOptions = manual;
            return manual;
        }

        if (_comparison is null)
        {
            _cachedOptions = [];
            _applyTarget(null);
            _applyRefusalNote("автовыбор включён, но службы-измерителя нет: сравнивать пути нечем, маршрут не меняется.");
            LastDecisionRu = "Автовыбор пути требует службы: без неё молча не выбираю.";
            return [];
        }

        // Тот же поток, тот же сервер и свежее решение — не мерим заново ради каждого контроля
        // качества. Отдаём те же варианты: контроль при деградации спрашивает их каждые пару секунд,
        // и пустой ответ означал бы «переключаться некуда», хотя резерв уже измерен.
        if (_cachedFlowKey == DecisionKey(flow) && DateTime.UtcNow - _cachedUtc < DecisionTtl)
        {
            return _cachedOptions;
        }

        RoutePathComparisonReport report = await _comparison
            .CompareAsync(flow.RemoteEndpoint, flow.RemotePort, nodes, ct)
            .ConfigureAwait(false);

        LastDecisionRu = report.VerdictRu;

        var options = new List<RealtimePathOption>(2);
        RoutingNodeEntry? primary = null;

        // Только измеренный победитель становится основным путём. «Прямой не хуже» — это не повод
        // вести трафик через узел: пусть даже человек включил мультипуть, переключение без выигрыша
        // было бы оптимизацией ради надписи.
        if (report.Winner is { } winner)
        {
            primary = winner;
        }

        if (primary is not null)
        {
            _applyTarget(new RouteOverride(primary.Address, RoutePathComparisonService.ProbeMetric, $"{primary.SourceRu}: {primary.Name}"));
            _applyRefusalNote(null);
            options.Add(OptionFor(primary, isPrimary: true));
        }
        else
        {
            // Ни одного изменения: победителя по замерам нет — ни цели, ни маршрутов.
            _applyTarget(null);
            _applyRefusalNote(report.VerdictRu);
        }

        // Резерв — только измеренный и только тот, что сам выигрывает у прямого пути.
        // Узел, который хуже направления по умолчанию, — не резерв: переключение на него
        // было бы ухудшением под видом оптимизации.
        RoutingNodeEntry? backup = report.RunnerUp;
        if (backup is null && settings.MultipathEnabled && settings.IsMultipathValid)
        {
            backup = settings.SecondaryNode;
        }

        if (primary is not null && backup is not null
            && !backup.Address.Equals(primary.Address, StringComparison.OrdinalIgnoreCase)
            && IsWorthSwitchingTo(report, backup))
        {
            options.Add(OptionFor(backup, isPrimary: false));
        }

        // Мультипуть на своих линиях: без своей сети дублировать нельзя, но держим горячий резерв.
        if (backup is not null)
        {
            LastSecondaryRoute = new RouteOverride(backup.Address, RoutePathComparisonService.ProbeMetric, $"{backup.SourceRu}: {backup.Name}");

            if (primary is { } main && options.Count > 1)
            {
                LastMultipathRu = $"Мультипуть: основной «{main.Name}» ({main.Address}), резерв — «{backup.Name}» ({backup.Address}). " +
                    "Без своей сети пакеты не дублируются: второй шлюз горячий, и при подтверждённой просадке основного " +
                    "трафик переключается на него — после нескольких подряд плохих окон контроля, а не в тот же миг.";
            }
            else
            {
                LastMultipathRu = $"Резервный шлюз — «{backup.Name}» ({backup.Address}), но он не выигрывает у прямого пути: " +
                    "на него не переключаемся, держим только как адрес для ручного выбора.";
            }
        }
        else
        {
            LastSecondaryRoute = null;
            LastMultipathRu = null;
        }

        // Кэш заполняем только теперь: если сравнение упало на середине, «свежего решения»
        // с пустым списком вариантов быть не должно.
        _cachedFlowKey = DecisionKey(flow);
        _cachedUtc = DateTime.UtcNow;
        _cachedOptions = options;
        return options;
    }

    /// <summary>
    /// Идентификатор пути для оптимизатора: адрес узла, а не подпись. Подпись человек может
    /// переименовать посреди сессии, и «тот же путь» стал бы для контроля новым.
    /// </summary>
    public static string PathIdFor(RoutingNodeEntry node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return $"node:{node.Address}";
    }

    private static RealtimePathOption OptionFor(RoutingNodeEntry node, bool isPrimary) => new(
        PathIdFor(node),
        isPrimary ? $"«{node.Name}» ({node.Address})" : $"«{node.Name}» ({node.Address}) — резерв",
        IsRelay: true,
        NextHop: node.Address,
        Action: new OptimizationActionDescriptor(
            isPrimary ? "route:game-server" : "route:game-server:runner-up",
            OptimizationActionKind.RouteForGame,
            $"Маршрут через «{node.Name}»",
            $"Маршрут /32 только на адрес игрового сервера через «{node.Name}» ({node.Address}, {node.SourceRu}); " +
            "снимается при остановке оптимизации.",
            RouteNextHop: node.Address));

    /// <summary>
    /// Годится ли узел в резерв: он должен быть измерен и сам выигрывать у прямого пути
    /// настолько, чтобы это не было шумом (<see cref="RoutePathComparisonService.RequiredGainMs"/>).
    /// Иначе «переключение на резерв» при просадке основного уводило бы трафик в худший путь.
    /// </summary>
    private static bool IsWorthSwitchingTo(RoutePathComparisonReport report, RoutingNodeEntry? node)
    {
        if (node is null || report.Direct is not { Success: true, MedianRttMs: { } directMs })
        {
            return false;
        }

        RoutePathProbeResult direct = report.Direct;

        RoutePathProbeResult? probe = report.ViaNodes.FirstOrDefault(result =>
            result.Success && result.MedianRttMs.HasValue
            && result.Address.Equals(node.Address, StringComparison.OrdinalIgnoreCase));

        return probe is not null
            && directMs - probe.MedianRttMs!.Value >= RoutePathComparisonService.RequiredGainMs(direct, probe);
    }
}

/// <summary>
/// DTO для канала службы и обратно: узлы-кандидаты (route-discover) и результат сравнения путей
/// (route-measure). Модель пересылается числами из фактических замеров: null — это «не измерено»,
/// и в JSON оно так и едет, а не нулём.
/// </summary>
public sealed record RoutingCandidateDto(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string SourceRu,
    [property: JsonPropertyName("isCurrent")] bool IsCurrentGateway);

public sealed record RoutingMeasureNodeDto(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string SourceRu,
    [property: JsonPropertyName("ok")] bool Success,
    [property: JsonPropertyName("medianMs")] double? MedianRttMs,
    [property: JsonPropertyName("jitterMs")] double? JitterMs,
    [property: JsonPropertyName("lossPercent")] double? LossPercent,
    [property: JsonPropertyName("problem")] string? ProblemRu);

public sealed record RoutingMeasureDto(
    [property: JsonPropertyName("target")] string TargetIp,
    [property: JsonPropertyName("directMedianMs")] double? DirectMedianMs,
    [property: JsonPropertyName("directJitterMs")] double? DirectJitterMs,
    [property: JsonPropertyName("directLossPercent")] double? DirectLossPercent,
    [property: JsonPropertyName("directProblem")] string? DirectProblemRu,
    [property: JsonPropertyName("nodes")] IReadOnlyList<RoutingMeasureNodeDto> Nodes,
    [property: JsonPropertyName("winnerAddress")] string? WinnerAddress,
    [property: JsonPropertyName("verdict")] string VerdictRu,
    [property: JsonPropertyName("tableRestored")] bool TableRestored,
    [property: JsonPropertyName("tableProblem")] string? TableProblemRu,
    [property: JsonPropertyName("runnerUpAddress")] string? RunnerUpAddress = null);

public static class RoutingWireCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string EncodeCandidates(IReadOnlyList<RoutingCandidateDto> candidates) =>
        JsonSerializer.Serialize(candidates, Options);

    public static IReadOnlyList<RoutingCandidateDto> DecodeCandidates(string? json) =>
        Deserialize<IReadOnlyList<RoutingCandidateDto>>(json) ?? [];

    public static string EncodeMeasure(RoutingMeasureDto measure) =>
        JsonSerializer.Serialize(measure, Options);

    public static RoutingMeasureDto? DecodeMeasure(string? json) =>
        Deserialize<RoutingMeasureDto>(json);

    public static RoutingMeasureDto ToDto(this RoutePathComparisonReport report) => new(
        report.TargetIp,
        report.Direct?.MedianRttMs,
        report.Direct?.JitterMs,
        report.Direct?.LossPercent,
        report.Direct?.ProblemRu,
        report.ViaNodes
            .Where(result => result.Name != "прямой путь")
            .Select(result => new RoutingMeasureNodeDto(
                result.Address, result.Name, result.SourceRu, result.Success,
                result.MedianRttMs, result.JitterMs, result.LossPercent, result.ProblemRu))
            .ToList(),
        report.Winner?.Address,
        report.VerdictRu,
        report.TableRestored,
        report.TableProblemRu,
        RunnerUpAddress: report.RunnerUp?.Address);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            // Ответ службы разобрать нельзя — это «данных нет», а не «данные пустые»: молча
            // превращать враньё в ноль не будем, но и ронять интерфейс чужим мусором нельзя.
            return default;
        }
    }
}
