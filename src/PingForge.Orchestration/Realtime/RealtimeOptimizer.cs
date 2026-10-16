using PingForge.Core.Models;

namespace PingForge.Orchestration.Realtime;

/// <summary>
/// Живая оптимизация уже запущенной игры: замер «до» по живому потоку, применение действий
/// (маршрут только для адреса игрового сервера + политика QoS), непрерывный контроль качества
/// и честный итог «до/после» при остановке.
///
/// Принципы, которые нельзя обходить:
///   • числа берутся только из измерений живого потока; нет данных — нет обещаний;
///   • выигрыш показывается как разница медиан, а не «до и после в лучшем кадре»;
///   • физический предел пути (минимальный RTT) показывается рядом, чтобы не обещать невозможное;
///   • при остановке снимается ровно то, что поставили, в обратном порядке.
/// </summary>
public sealed class RealtimeOptimizer
{
    private readonly IGameFlowWatcher _watcher;
    private readonly IRealtimeMeasurementSource _measurement;
    private readonly IOptimizationActionRunner _actions;
    private readonly OptimizationActionPlanner _planner;
    private readonly IRealtimePathOptionsProvider? _pathOptions;
    private readonly Func<DateTime> _clock;

    private readonly List<OptimizationActionDescriptor> _applied = new();
    private readonly List<double> _baselineWindows = new();
    private readonly List<PathMeasurement> _recentWindows = new();
    private readonly List<string> _warnings = new();

    private RealtimeOptions _options = new();
    private RealtimeOptimizationState _state = RealtimeOptimizationState.Idle;
    private string _statusRu = "Оптимизация не запущена.";
    private GameFlowInfo? _flow;
    private double? _baselineRttMs;
    private double? _floorRttMs;
    private double? _confirmationRttMs;
    private double? _lastRttMs;
    private double? _lastJitterMs;
    private double? _lastLossPercent;
    private string? _activePathId;
    private bool _appliedRouteAction;
    private int _consecutiveBadWindows;
    private int _consecutiveGoodWindows;
    private DateTime _startedUtc;

    public RealtimeOptimizer(
        IGameFlowWatcher watcher,
        IRealtimeMeasurementSource measurement,
        IOptimizationActionRunner actions,
        OptimizationActionPlanner? planner = null,
        IRealtimePathOptionsProvider? pathOptions = null,
        Func<DateTime>? clock = null)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _measurement = measurement ?? throw new ArgumentNullException(nameof(measurement));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _planner = planner ?? new OptimizationActionPlanner();
        _pathOptions = pathOptions;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public RealtimeOptimizationState State => _state;

    public RealtimeSnapshot Current => BuildSnapshot();

    /// <summary>Событие для интерфейса: панель обновляется без опроса.</summary>
    public event Action<RealtimeSnapshot>? Updated;

    private const int RecentWindowLimit = 12;

    public async Task<RealtimeSnapshot> StartAsync(
        RealtimeOptions? options = null,
        bool isAdministrator = true,
        string? preferredGame = null,
        CancellationToken ct = default)
    {
        Reset();

        _options = options ?? new RealtimeOptions();
        _startedUtc = _clock();

        SetState(RealtimeOptimizationState.FindingGame, "Ищем запущенную игру и её сетевой поток…");

        IReadOnlyList<GameFlowInfo> flows = await _watcher.FindActiveGameFlowsAsync(ct).ConfigureAwait(false);
        if (flows.Count == 0)
        {
            return Fail(
                "Запущенная игра не найдена. Оптимизировать «интернет вообще» нельзя: у каждой игры свой сервер и свой путь. " +
                "Запустите игру, зайдите в матч и нажмите кнопку снова.");
        }

        _flow = PickFlow(flows, preferredGame);

        SetState(RealtimeOptimizationState.MeasuringBaseline,
            $"Меряем исходный пинг до {_flow.RemoteEndpoint}: {_options.BaselineWindows} окна по {_options.SamplesPerWindow} проб. " +
            "Числа берутся из живого потока игры, лишние пакеты не отправляются.");

        for (int i = 0; i < Math.Max(1, _options.BaselineWindows); i++)
        {
            ct.ThrowIfCancellationRequested();

            PathMeasurement window = await MeasureAsync(ct).ConfigureAwait(false);
            if (!window.IsMeasurable)
            {
                _warnings.Add($"Окно замера «до» не удалось: {window.MethodRu}");
                continue;
            }

            _baselineWindows.Add(window.RttMedianMs);
            UpdateFloor(window.RttMedianMs);
        }

        if (_baselineWindows.Count == 0)
        {
            return Fail(
                "Живой поток игры не измеряется: игра может быть в меню, либо трафик идёт не туда, где мы ищем. " +
                "Зайдите в матч и повторите — выдуманные числа мы не показываем.");
        }

        _baselineRttMs = Median(_baselineWindows);

        if (!isAdministrator)
        {
            SetState(RealtimeOptimizationState.ReadOnlyNoAdmin,
                $"Права администратора не выданы: пинг до сервера {_baselineRttMs:F0} мс измеряется, но менять маршрут и QoS " +
                "программа не будет. Это безопасный режим «только посмотреть».");

            return Current;
        }

        SetState(RealtimeOptimizationState.ChoosingRoute, "Выбираем путь до игрового сервера…");

        List<OptimizationActionDescriptor> planned = await BuildPlannedActionsAsync(_flow, ct).ConfigureAwait(false);
        if (planned.Count == 0)
        {
            return Fail("Нет доступных действий для этой игры: проверьте настройки оптимизации.");
        }

        SetState(RealtimeOptimizationState.ApplyingActions,
            $"Применяем {planned.Count} действий. Каждое действие можно снять одной кнопкой — оно не остаётся в системе после остановки.");

        foreach (OptimizationActionDescriptor action in planned)
        {
            ct.ThrowIfCancellationRequested();

            OptimizationActionOutcome outcome = await _actions.ApplyAsync(action, ct).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                _applied.Add(action);
                if (action.Kind == OptimizationActionKind.RouteForGame)
                {
                    _appliedRouteAction = true;
                }

                continue;
            }

            _warnings.Add($"{action.TitleRu}: {outcome.MessageRu}");
        }

        if (_applied.Count == 0)
        {
            return Fail($"Ни одно действие не применилось. Первая причина: {_warnings.FirstOrDefault() ?? "неизвестна"}");
        }

        // Контрольное окно после применения: выигрыш должен быть подтверждён, а не заявлен.
        PathMeasurement confirmation = await MeasureAsync(ct).ConfigureAwait(false);
        if (confirmation.IsMeasurable)
        {
            _confirmationRttMs = confirmation.RttMedianMs;
            _recentWindows.Add(confirmation);
            UpdateFloor(confirmation.RttMedianMs);
        }

        string verdict;
        if (_confirmationRttMs is not { } confirmed || _baselineRttMs is not { } baselineValue)
        {
            verdict = "Применено. Первое контрольное измерение ещё не пришло.";
        }
        else if (confirmed <= baselineValue + 0.5)
        {
            verdict = $"Применено. Пинг: было {baselineValue:F0} мс, стало {confirmed:F0} мс (медиана живого потока).";
        }
        else
        {
            verdict = $"Применено, но пока хуже: было {baselineValue:F0} мс, стало {confirmed:F0} мс. " +
                      "Дадим маршруту несколько окон: если лучше не станет — откатим и скажем прямо.";
        }

        SetState(RealtimeOptimizationState.ActiveMonitoring, verdict + $" Дальше контроль каждые {_options.EffectiveMonitorInterval.TotalSeconds:F0} с.");
        return Current;
    }

    /// <summary>Один цикл контроля. Вызывается таймером (или тестом) — так поведение проверяемо.</summary>
    public async Task<RealtimeSnapshot> TickOnceAsync(CancellationToken ct = default)
    {
        if (_state is not (RealtimeOptimizationState.ActiveMonitoring
            or RealtimeOptimizationState.Degraded
            or RealtimeOptimizationState.ReadOnlyNoAdmin))
        {
            return Current;
        }

        if (_flow is null)
        {
            return Current;
        }

        PathMeasurement window = await MeasureAsync(ct).ConfigureAwait(false);
        _recentWindows.Add(window);
        if (_recentWindows.Count > RecentWindowLimit)
        {
            _recentWindows.RemoveAt(0);
        }

        if (!window.IsMeasurable)
        {
            _consecutiveBadWindows++;
            SetStatus($"Замер не удался: {window.MethodRu}");

            if (_consecutiveBadWindows >= _options.DegradedWindowsBeforeSwitch)
            {
                await DegradeAsync("Живой поток перестал измеряться: игра может быть в меню или трафик сменил сервер.", ct)
                    .ConfigureAwait(false);
            }

            return Current;
        }

        _lastRttMs = window.RttMedianMs;
        _lastJitterMs = window.JitterMs;
        _lastLossPercent = window.LossPercent;
        UpdateFloor(window.RttMedianMs);

        bool degraded = window.LossPercent > _options.DegradationLossPercent
                        || window.JitterMs > _options.DegradationJitterMs
                        || (_baselineRttMs is { } baseline && window.RttMedianMs > baseline + _options.DegradationRttMarginMs);

        if (degraded)
        {
            _consecutiveGoodWindows = 0;
            _consecutiveBadWindows++;

            if (_consecutiveBadWindows >= _options.DegradedWindowsBeforeSwitch)
            {
                bool switched = await TrySwitchPathAsync(window, ct).ConfigureAwait(false);
                if (!switched)
                {
                    await DegradeAsync(
                        $"Качество просело: {window.RttMedianMs:F0} мс, джиттер {window.JitterMs:F1} мс, потери {window.LossPercent:F1}%. " +
                        "Альтернативного пути у этого сервера нет — твиками такое не лечится.", ct).ConfigureAwait(false);
                }
            }
            else
            {
                SetStatus($"Внимание: окно хуже обычного ({window.RttMedianMs:F0} мс, джиттер {window.JitterMs:F1} мс). " +
                          $"Ждём подтверждения ({_consecutiveBadWindows} из {_options.DegradedWindowsBeforeSwitch}).");
            }

            return Current;
        }

        _consecutiveBadWindows = 0;

        if (_state == RealtimeOptimizationState.Degraded)
        {
            _consecutiveGoodWindows++;
            SetStatus($"Качество восстановилось ({window.RttMedianMs:F0} мс). Подтверждений: {_consecutiveGoodWindows} из {_options.GoodWindowsBeforeReturn}.");

            if (_consecutiveGoodWindows >= _options.GoodWindowsBeforeReturn)
            {
                SetState(RealtimeOptimizationState.ActiveMonitoring,
                    $"Оптимизация снова работает штатно: {window.RttMedianMs:F0} мс.");
            }

            return Current;
        }

        SetState(RealtimeOptimizationState.ActiveMonitoring, BuildRunningStatus(window));
        return Current;
    }

    /// <summary>Цикл контроля для службы: тик + пауза. Останавливается по токену или по StopAsync.</summary>
    public async Task RunMonitorLoopAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && _state is RealtimeOptimizationState.ActiveMonitoring or RealtimeOptimizationState.Degraded)
        {
            await TickOnceAsync(ct).ConfigureAwait(false);

            if (_clock() - _startedUtc > _options.EffectiveMaxSessionDuration)
            {
                _warnings.Add($"Сессия длится больше {_options.EffectiveMaxSessionDuration.TotalHours:F0} ч. Если игра давно закрыта — нажмите «Остановить».");
            }

            try
            {
                await Task.Delay(_options.EffectiveMonitorInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<RealtimeStopReport> StopAsync(CancellationToken ct = default)
    {
        SetState(RealtimeOptimizationState.Stopping, "Снимаем изменения и возвращаем сеть в исходное состояние…");

        var revertMessages = new List<string>();
        bool everythingReverted = true;

        for (int i = _applied.Count - 1; i >= 0; i--)
        {
            OptimizationActionDescriptor action = _applied[i];
            OptimizationActionOutcome outcome = await RevertAsync(action, ct).ConfigureAwait(false);
            revertMessages.Add($"{action.TitleRu}: {outcome.MessageRu}");

            if (!outcome.Succeeded)
            {
                everythingReverted = false;
            }
        }

        double? finalRttMs = _lastRttMs ?? _confirmationRttMs;
        PathMeasurement final = await MeasureAsync(ct).ConfigureAwait(false);
        if (final.IsMeasurable)
        {
            finalRttMs = final.RttMedianMs;
        }

        double? improvement = _baselineRttMs is { } baseline && finalRttMs is { } after
            ? Math.Round(baseline - after, 1)
            : null;

        string summary = BuildHonestSummary(finalRttMs, improvement, everythingReverted);

        var report = new RealtimeStopReport(
            GameTitleRu: _flow?.GameTitleRu ?? "Игра не определена",
            Endpoint: _flow?.RemoteEndpoint ?? "—",
            BaselineRttMs: _baselineRttMs,
            FinalRttMs: finalRttMs,
            FloorRttMs: _floorRttMs,
            ImprovementMs: improvement,
            QualityLabelRu: QualityLabel(finalRttMs),
            Duration: _clock() - _startedUtc,
            AppliedActionTitles: _applied.Select(a => a.TitleRu).ToList(),
            RevertMessagesRu: revertMessages,
            EverythingReverted: everythingReverted,
            HonestSummaryRu: summary);

        _applied.Clear();
        _appliedRouteAction = false;
        _activePathId = null;
        SetState(RealtimeOptimizationState.Stopped, summary);
        return report;
    }

    private async Task<List<OptimizationActionDescriptor>> BuildPlannedActionsAsync(GameFlowInfo flow, CancellationToken ct)
    {
        OptimizationPlan plan = _planner.Plan(flow, _options, isAdministrator: true);
        var actions = plan.Actions.ToList();

        foreach (string note in plan.Notes)
        {
            _warnings.Add(note);
        }

        if (plan.IsBlocked)
        {
            _warnings.Add(plan.BlockReasonRu ?? "Действия недоступны.");
            return new List<OptimizationActionDescriptor>();
        }

        if (_pathOptions is not null)
        {
            IReadOnlyList<RealtimePathOption> options = await _pathOptions.GetOptionsAsync(flow, ct).ConfigureAwait(false);
            RealtimePathOption? preferred = options.FirstOrDefault();

            if (preferred is not null)
            {
                _activePathId = preferred.PathId;
                // Первый путь от провайдера вариантов — уже измеренный и лучший: подменяем общий маршрут на него.
                actions.RemoveAll(a => a.Kind == OptimizationActionKind.RouteForGame);
                if (_options.AllowRoutingChanges)
                {
                    actions.Insert(0, preferred.Action);
                }

                SetStatus($"Выбран путь «{preferred.DisplayName}».");
            }
        }

        return actions;
    }

    private async Task<bool> TrySwitchPathAsync(PathMeasurement current, CancellationToken ct)
    {
        if (_pathOptions is null || _flow is null || !_options.AllowRoutingChanges)
        {
            return false;
        }

        IReadOnlyList<RealtimePathOption> options = await _pathOptions.GetOptionsAsync(_flow, ct).ConfigureAwait(false);
        RealtimePathOption? next = options.FirstOrDefault(o => !string.Equals(o.PathId, _activePathId, StringComparison.OrdinalIgnoreCase));

        if (next is null)
        {
            return false;
        }

        _warnings.Add($"Переключение пути: {current.RttMedianMs:F0} мс на текущем, пробуем «{next.DisplayName}».");

        bool routeWasApplied = _appliedRouteAction;
        OptimizationActionOutcome outcome = await _actions.ApplyAsync(next.Action, ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            _warnings.Add($"Переключиться не удалось: {outcome.MessageRu}");
            return false;
        }

        if (routeWasApplied)
        {
            OptimizationActionDescriptor? previous = _applied.FirstOrDefault(a => a.Kind == OptimizationActionKind.RouteForGame);
            if (previous is not null)
            {
                await RevertAsync(previous, ct).ConfigureAwait(false);
                _applied.Remove(previous);
            }
        }

        _applied.Add(next.Action);
        _appliedRouteAction = true;
        _activePathId = next.PathId;
        _consecutiveBadWindows = 0;
        _consecutiveGoodWindows = 0;
        SetState(RealtimeOptimizationState.ActiveMonitoring,
            $"Переключились на путь «{next.DisplayName}». Смотрим, стало ли лучше: решение принимается по замерам, а не по ожиданиям.");
        return true;
    }

    private async Task DegradeAsync(string reason, CancellationToken ct)
    {
        _consecutiveGoodWindows = 0;
        SetState(RealtimeOptimizationState.Degraded,
            $"{reason} Оптимизация оставлена включённой: если качество восстановится само, мы просто вернёмся в штатный режим. " +
            "Кнопка «Остановить» всегда доступна.");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<OptimizationActionOutcome> RevertAsync(OptimizationActionDescriptor action, CancellationToken ct)
    {
        try
        {
            return await _actions.RevertAsync(action, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OptimizationActionOutcome.Fail($"снять не удалось: {ex.Message}. Сеть надо проверить вручную — «Починить сеть».");
        }
    }

    private async Task<PathMeasurement> MeasureAsync(CancellationToken ct)
    {
        if (_flow is null)
        {
            return PathMeasurement.Unmeasurable("Игра не выбрана.");
        }

        try
        {
            return await _measurement.MeasurePathAsync(_flow.RemoteEndpoint, _flow.RemotePort, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PathMeasurement.Unmeasurable($"Замер не выполнился: {ex.Message}");
        }
    }

    private static GameFlowInfo PickFlow(IReadOnlyList<GameFlowInfo> flows, string? preferredGame)
    {
        if (!string.IsNullOrWhiteSpace(preferredGame))
        {
            GameFlowInfo? match = flows.FirstOrDefault(f =>
                f.ProcessName.Contains(preferredGame, StringComparison.OrdinalIgnoreCase)
                || f.GameTitleRu.Contains(preferredGame, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return flows[0];
    }

    private void UpdateFloor(double rttMs) =>
        _floorRttMs = _floorRttMs is null ? rttMs : Math.Min(_floorRttMs.Value, rttMs);

    private string BuildRunningStatus(PathMeasurement window)
    {
        double? improvement = _baselineRttMs is { } baseline ? Math.Round(baseline - window.RttMedianMs, 1) : null;

        string improvementText = improvement switch
        {
            null => "выигрыш ещё считается",
            > 0.5 => $"выигрыш {improvement:F0} мс против исходных {_baselineRttMs:F0} мс",
            < -0.5 => $"пока хуже на {Math.Abs(improvement.Value):F0} мс",
            _ => "выигрыша нет — пинг как был"
        };

        return $"Пинг {window.RttMedianMs:F0} мс (джиттер {window.JitterMs:F1} мс, потери {window.LossPercent:F1}%), {improvementText}. " +
               $"Физический предел пути: {_floorRttMs:F0} мс.";
    }

    private RealtimeSnapshot BuildSnapshot() => new(
        State: _state,
        StatusRu: _statusRu,
        GameTitleRu: _flow?.GameTitleRu,
        Endpoint: _flow is null ? null : $"{_flow.RemoteEndpoint}:{_flow.RemotePort}",
        BaselineRttMs: _baselineRttMs,
        CurrentRttMs: _lastRttMs ?? _confirmationRttMs ?? Median(_baselineWindows.Count > 0 ? _baselineWindows : null),
        FloorRttMs: _floorRttMs,
        CurrentJitterMs: _lastJitterMs,
        CurrentLossPercent: _lastLossPercent,
        ImprovementMs: _baselineRttMs is { } baseline && (_lastRttMs ?? _confirmationRttMs) is { } now
            ? Math.Round(baseline - now, 1)
            : null,
        QualityLabelRu: QualityLabel(_lastRttMs ?? _confirmationRttMs ?? _baselineRttMs),
        AppliedActions: _applied.ToList(),
        Warnings: _warnings.ToList(),
        UpdatedUtc: _clock());

    private string BuildHonestSummary(double? finalRttMs, double? improvement, bool everythingReverted)
    {
        string revertText = everythingReverted
            ? "Изменения сняты, сеть в исходном состоянии."
            : "Часть изменений снять не удалось — нажмите «Починить сеть» и проверьте маршруты вручную.";

        if (_baselineRttMs is not { } baseline || finalRttMs is not { } after)
        {
            return $"Замеры «до» или «после» недоступны, поэтому выигрыш не заявляем. {revertText}";
        }

        if (improvement is > 0.5)
        {
            string floorText = _floorRttMs is { } floor
                ? $" Лучший замер за сессию — {floor:F0} мс, ниже физического предела пути опуститься нельзя."
                : string.Empty;

            return $"Пинг снизился с {baseline:F0} до {after:F0} мс (медиана живого потока). Это измеренный результат, " +
                   $"а не обещание.{floorText} {revertText}";
        }

        if (improvement is < -0.5)
        {
            return $"Стало хуже: {baseline:F0} → {after:F0} мс. Оптимизацию не оставляем: {revertText} " +
                   "Причина в самом маршруте, а не в настройках Windows — тут помог бы только релей в обход проблемного участка.";
        }

        return $"Заметного выигрыша нет: {baseline:F0} → {after:F0} мс. {revertText} " +
               "Честно: на этом маршруте оптимизация не помогает, а твики пинг на 40 мс не снижают — так не бывает.";
    }

    /// <summary>
    /// Пороги оценки качества для шутеров (мс). Вынесены в данные, чтобы интерфейс и отчёты
    /// показывали те же полосы, что использует движок, а не собственную выдумку.
    /// </summary>
    public static readonly IReadOnlyList<(double UpperBoundMs, string LabelRu)> QualityBands = new[]
    {
        (40.0, "Отлично"),
        (70.0, "Хорошо"),
        (110.0, "Терпимо"),
        (180.0, "Плохо для шутера"),
        (double.PositiveInfinity, "Очень плохо")
    };

    /// <summary>Оценка качества по медиане живого пинга — с порогами для шутеров, а не «для интернета вообще».</summary>
    public static string QualityLabel(double? rttMs)
    {
        if (rttMs is not { } rtt)
        {
            return "нет данных";
        }

        foreach ((double upper, string label) in QualityBands)
        {
            if (rtt <= upper)
            {
                return label;
            }
        }

        return QualityBands[^1].LabelRu;
    }

    private static double? Median(IReadOnlyList<double>? values) =>
        values is null || values.Count == 0 ? null : StatisticalMetricsCalculatorBridge.Median(values);

    public Task<RealtimeSnapshot> StartAsync(CancellationToken ct) => StartAsync(null, true, null, ct);

    private void SetState(RealtimeOptimizationState state, string statusRu)
    {
        _state = state;
        _statusRu = statusRu;
        Updated?.Invoke(Current);
    }

    private void SetStatus(string statusRu)
    {
        _statusRu = statusRu;
        Updated?.Invoke(Current);
    }

    private RealtimeSnapshot Fail(string reason)
    {
        _state = RealtimeOptimizationState.Failed;
        _statusRu = reason;
        Updated?.Invoke(Current);
        return Current;
    }

    private void Reset()
    {
        _applied.Clear();
        _baselineWindows.Clear();
        _recentWindows.Clear();
        _warnings.Clear();
        _flow = null;
        _baselineRttMs = null;
        _floorRttMs = null;
        _confirmationRttMs = null;
        _lastRttMs = null;
        _lastJitterMs = null;
        _lastLossPercent = null;
        _activePathId = null;
        _appliedRouteAction = false;
        _consecutiveBadWindows = 0;
        _consecutiveGoodWindows = 0;
    }
}

/// <summary>Медиана по значениям: маленький хелпер, чтобы движок не зависел от слоя проб.</summary>
internal static class StatisticalMetricsCalculatorBridge
{
    public static double Median(IReadOnlyList<double> values)
    {
        List<double> sorted = values.OrderBy(v => v).ToList();
        int middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
