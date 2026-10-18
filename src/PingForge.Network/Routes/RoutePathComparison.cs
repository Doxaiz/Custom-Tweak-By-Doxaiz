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
/// Чисел без измерения не бывает: <paramref name="Measurement"/> null ровно тогда, когда Success false.
/// </summary>
public sealed record RoutePathProbeResult(
    string Address,
    string Name,
    string SourceRu,
    bool Success,
    double? MedianRttMs,
    double? JitterMs,
    double? LossPercent,
    string? ProblemRu);

/// <summary>Итог сравнения путей до одного адреса: честный вердикт и состояние таблицы маршрутов.</summary>
public sealed record RoutePathComparisonReport(
    string TargetIp,
    RoutePathProbeResult? Direct,
    IReadOnlyList<RoutePathProbeResult> ViaNodes,
    RoutingNodeEntry? Winner,
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
                targetIp, null, [], null,
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
                null,
                $"К {target} уже есть маршрут /32, созданный не программой: сравнивать пути не буду, чтобы его не трогать. " +
                "Ничего не меняем.",
                TableRestored: true, TableProblemRu: null);
        }

        RoutePathProbeResult direct = await MeasureDirectAsync(target, port, ct).ConfigureAwait(false);

        var via = new List<RoutePathProbeResult>(nodes.Count);
        string? tableProblem = null;
        bool restored = true;

        // Шлюз маршрута по умолчанию из исходной таблицы: идти «через него» — это прямой путь.
        string defaultGateway = RouteTableParser.FindDefaultRoute(before)?.NextHop ?? string.Empty;

        foreach (RoutingNodeEntry node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            if (!RoutingNodeValidation.NormalizeIpv4(node.Address, out string hop))
            {
                via.Add(new RoutePathProbeResult(node.Address, node.Name, node.SourceRu, false, null, null, null,
                    "адрес узла не похож на строгий IPv4 — пропускаю, угадывать не стану"));
                continue;
            }

            if (defaultGateway.Length > 0 && hop.Equals(defaultGateway, StringComparison.OrdinalIgnoreCase))
            {
                // Идти «через текущий шлюз» — это и есть прямой путь: временный /32 к тому же шлюзу
                // ничего не меняет, а лишний маршрут в системе на время замера не нужен.
                via.Add(new RoutePathProbeResult(hop, node.Name, node.SourceRu, direct.Success,
                    direct.MedianRttMs, direct.JitterMs, direct.LossPercent,
                    direct.Success ? "совпадает с текущим шлюзом — это тот же прямой путь" : direct.ProblemRu));
                continue;
            }

            (bool added, string? addProblem) = await TryAddTempRouteAsync(target, hop, ct).ConfigureAwait(false);
            if (!added)
            {
                via.Add(new RoutePathProbeResult(node.Address, node.Name, node.SourceRu, false, null, null, null, addProblem));
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

            via.Add(new RoutePathProbeResult(
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

        (bool finalRestored, string? finalProblem) = await VerifyTableMatchesAsync(before, ct).ConfigureAwait(false);
        restored = restored && finalRestored;
        tableProblem ??= finalProblem;

        return BuildVerdict(target, direct, via, nodes, restored, tableProblem);
    }

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
            return new RoutePathComparisonReport(target, direct, via, null,
                "Узлов для сравнения нет: искать не среди чего. Добавьте второй шлюз или сохраните узел вручную — " +
                "и только потом измеряйте." + tableTail,
                restored, tableProblem);
        }

        if (!direct.Success)
        {
            string reason = direct.ProblemRu ?? "прямой путь не измерился";
            return new RoutePathComparisonReport(target, direct, via, null,
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

            return new RoutePathComparisonReport(target, direct, via, null,
                $"Ни один узел не измерился до {target} ({(problems.Length > 0 ? problems : "замеры не задались")}). " +
                $"Прямой путь: медиана {FormatMs(direct.MedianRttMs!.Value)} мс — его и оставляем, ничего не меняем." + tableTail,
                restored, tableProblem);
        }

        // Победитель среди узлов: медиана → джиттер → потери.
        RoutePathProbeResult best = measured
            .OrderBy(result => result.MedianRttMs!.Value)
            .ThenBy(result => result.JitterMs ?? double.MaxValue)
            .ThenBy(result => result.LossPercent ?? double.MaxValue)
            .First();

        double gain = direct.MedianRttMs!.Value - best.MedianRttMs!.Value;

        if (gain >= NotableImprovementMs)
        {
            RoutingNodeEntry? winner = nodes.FirstOrDefault(node =>
                node.Address.Equals(best.Address, StringComparison.OrdinalIgnoreCase));

            string tieNote = measured.Count > 1
                ? $" Лучший из {measured.Count} измеренных (сравнение: медиана, при равенстве — джиттер, затем потери)."
                : string.Empty;

            return new RoutePathComparisonReport(target, direct, via, winner,
                $"Через «{best.Name}» ({best.Address}) медиана {FormatMs(best.MedianRttMs!.Value)} мс против " +
                $"{FormatMs(direct.MedianRttMs.Value)} мс напрямую — выигрыш {FormatMs(gain)} мс. Этот путь можно применять." +
                tieNote + tableTail,
                restored, tableProblem);
        }

        if (gain > -NotableImprovementMs)
        {
            return new RoutePathComparisonReport(target, direct, via, null,
                $"Прямой путь не хуже: медиана {FormatMs(direct.MedianRttMs.Value)} мс против " +
                $"{FormatMs(best.MedianRttMs!.Value)} мс через «{best.Name}» — разница в пределах погрешности. Ничего не меняем." + tableTail,
                restored, tableProblem);
        }

        return new RoutePathComparisonReport(target, direct, via, null,
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
/// <see cref="WindowsGameNetworkPlatform.RouteTarget"/> во время оптимизации — так решение
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

    /// <summary>
    /// Сколько живёт решение автовыбора: при деградации оптимизатор спрашивает варианты снова,
    /// и каждый вопрос ценой в полноценное сравнение путей был бы пыткой для сети. Пока ответ свежий
    /// и поток тот же — решение переиспользуется (это всё те же измеренные числа, а не угадка).
    /// </summary>
    public static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(240);

    private string? _cachedFlowKey;
    private DateTime _cachedUtc;

    /// <summary>Забыть решение: новая оптимизация обязана мерить заново, а не жить вчерашней победой.</summary>
    public void Invalidate() => _cachedFlowKey = null;

    public async Task<IReadOnlyList<RealtimePathOption>> GetOptionsAsync(GameFlowInfo flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);

        _applyFlow?.Invoke(flow);

        RoutingSettings settings = _readSettings() ?? RoutingSettings.Empty;
        IReadOnlyList<RoutingNodeEntry> nodes = settings.ValidNodes;

        if (nodes.Count == 0)
        {
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
                _applyTarget(null);
                _applyRefusalNote("узлы сохранены, но ни один не выбран по умолчанию и автовыбор выключен: " +
                    "маршрут не меняется, пока человек не укажет узел.");
                LastDecisionRu = "Узел по умолчанию не выбран — маршруты не меняю.";
                return [];
            }

            _applyTarget(new RouteOverride(chosen.Address, RoutePathComparisonService.ProbeMetric, $"{chosen.SourceRu}: {chosen.Name}"));
            _applyRefusalNote(null);
            LastDecisionRu = $"Веду трафик игры через выбранный узел «{chosen.Name}» ({chosen.Address}).";
            return [];
        }

        if (_comparison is null)
        {
            _applyTarget(null);
            _applyRefusalNote("автовыбор включён, но службы-измерителя нет: сравнивать пути нечем, маршрут не меняется.");
            LastDecisionRu = "Автовыбор пути требует службы: без неё молча не выбираю.";
            return [];
        }

        // Тот же поток и свежее решение — не мерим заново ради каждого контроля качества.
        if (_cachedFlowKey == flow.Key && DateTime.UtcNow - _cachedUtc < DecisionTtl)
        {
            return [];
        }

        RoutePathComparisonReport report = await _comparison
            .CompareAsync(flow.RemoteEndpoint, flow.RemotePort, nodes, ct)
            .ConfigureAwait(false);

        _cachedFlowKey = flow.Key;
        _cachedUtc = DateTime.UtcNow;

        LastDecisionRu = report.VerdictRu;

        if (report.Winner is { } winner)
        {
            _applyTarget(new RouteOverride(winner.Address, RoutePathComparisonService.ProbeMetric, $"{winner.SourceRu}: {winner.Name}"));
            _applyRefusalNote(null);
        }
        else
        {
            // Ни одного изменения: победителя по замерам нет — ни цели, ни маршрутов.
            _applyTarget(null);
            _applyRefusalNote(report.VerdictRu);
        }

        return [];
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
    [property: JsonPropertyName("tableProblem")] string? TableProblemRu);

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
        report.TableProblemRu);

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
