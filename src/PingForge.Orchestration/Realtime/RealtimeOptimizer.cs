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

    /// <summary>
    /// История замеров сессии с метками времени — источник графика сети. Отдельно от
    /// <see cref="_recentWindows"/>: то окно «последних N измерений» для логики контроля,
    /// а здесь — вся сессия с датами, чтобы график показывал динамику, а не последнее значение.
    /// </summary>
    private readonly object _historyGate = new();
    private readonly List<RealtimeMeasurementPoint> _history = new();

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

    /// <summary>
    /// Пути, на которых уже пробовали и качество не улучшилось. Второй раз туда не идём:
    /// переключение между двумя плохими путями — это флаппинг, а не оптимизация.
    /// </summary>
    private readonly HashSet<string> _failedPathIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Почему последнее переключение не состоялось: это уходит в честный статус при деградации.</summary>
    private string? _switchProblemRu;

    /// <summary>
    /// Хороших окон подряд с момента последней проверки «нет ли пути быстрее». Игровые ускорители
    /// постоянно перепроверяют альтернативы; мы делаем это редко и по расписанию, потому что каждый
    /// замер всех путей — это временные /32 в таблице маршрутов, а не бесплатная операция.
    /// </summary>
    private int _healthyWindowsSinceReview;

    /// <summary>Когда реально переключили маршрут: чаще, чем раз в cooldown, не переключаемся.</summary>
    private DateTime? _lastSwitchUtc;
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

    /// <summary>
    /// История замеров этой сессии (с метками времени) — из неё строится график сети в окне.
    /// Оценка сверху по объёму: <see cref="HistoryLimit"/> точек при контроле раз в 2 с — это
    /// 30 минут. Неизмеренные окна лежат здесь точками с null-полями: разрыв на графике, а не ноль.
    /// </summary>
    public IReadOnlyList<RealtimeMeasurementPoint> RecentSamples
    {
        get
        {
            lock (_historyGate)
            {
                return _history.ToList();
            }
        }
    }

    /// <summary>Сколько точек истории храним: 900 окон по 2 с — 30 минут. Дальше старые уходят.</summary>
    private const int HistoryLimit = 900;

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

        // Один проход по таблице соединений. Если наблюдатель умеет объяснять отказ, берём объяснение:
        // человеку с запущенной игрой, которой нет в каталоге, нужно знать, что соединение видно,
        // а не читать «игра не найдена» и думать, что программа сломана.
        IReadOnlyList<GameFlowInfo> flows;
        string? noGameReason = null;

        if (_watcher is IGameFlowReporter reporter)
        {
            (flows, noGameReason) = await reporter.FindGameFlowsWithReasonAsync(ct).ConfigureAwait(false);
        }
        else
        {
            flows = await _watcher.FindActiveGameFlowsAsync(ct).ConfigureAwait(false);
        }

        if (flows.Count == 0)
        {
            string tail = string.IsNullOrWhiteSpace(noGameReason) ? string.Empty : " " + noGameReason;

            return Fail(
                "Запущенная игра не найдена. Оптимизировать «интернет вообще» нельзя: у каждой игры свой сервер и свой путь. " +
                "Запустите игру, зайдите в матч и нажмите кнопку снова." + tail);
        }

        _flow = PickFlow(flows, preferredGame);

        // Лобби/творческий: игра уже запущена, но публичного адреса матча ещё нет (RemotePort 0, endpoint "—").
        // Раньше это считалось “игра не найдена”, хотя процесс Фортнайта уже был в памяти.
        if (_flow.RemotePort == 0 || _flow.RemoteEndpoint == "—" || _flow.RemoteEndpoint == "-")
        {
            string lobbyTail = string.IsNullOrWhiteSpace(noGameReason) ? string.Empty : " " + noGameReason;
            return Fail(
                $"Игра уже запущена: {_flow.GameTitleRu} найден в системе (PID {_flow.ProcessId}). Вы сейчас в лобби/творческом режиме — публичного адреса сервера матча пока нет. " +
                "Твики Windows уже можно применить в разделе «Твики» (снижают задержки ввода и очереди), а маршрут и QoS применятся как только зайдёте в матч с живым адресом сервера — нажмите кнопку снова в матче." + lobbyTail);
        }

        SetState(RealtimeOptimizationState.MeasuringBaseline,
            $"Меряем исходный пинг до {_flow.RemoteEndpoint}: {_options.BaselineWindows} окна по {_options.SamplesPerWindow} проб. " +
            "Числа берутся из живого потока игры, лишние пакеты не отправляются.");

        for (int i = 0; i < Math.Max(1, _options.BaselineWindows); i++)
        {
            ct.ThrowIfCancellationRequested();

            PathMeasurement window = await MeasureAsync(ct).ConfigureAwait(false);
            RecordHistory(window);
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
        RecordHistory(confirmation);
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
        RecordHistory(window);
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
            _healthyWindowsSinceReview = 0;
            _consecutiveBadWindows++;

            if (_consecutiveBadWindows >= _options.DegradedWindowsBeforeSwitch)
            {
                bool switched = await TrySwitchPathAsync(window, ct).ConfigureAwait(false);
                if (!switched)
                {
                    string why = _switchProblemRu ?? "альтернативного пути у этого сервера нет";
                    await DegradeAsync(
                        $"Качество просело: {window.RttMedianMs:F0} мс, джиттер {window.JitterMs:F1} мс, потери {window.LossPercent:F1}%. " +
                        $"Переключиться не вышло: {why} — твиками такое не лечится.", ct).ConfigureAwait(false);
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

        // Штатное окно: раз в N хороших окон проверяем, не нашлось ли пути ощутимо быстрее того,
        // на котором сидим. Решение — только по измеренным числам этой же сессии.
        (bool upgraded, string? reviewNoteRu) = await TryUpgradePathAsync(window, ct).ConfigureAwait(false);
        if (upgraded)
        {
            return Current;
        }

        SetState(RealtimeOptimizationState.ActiveMonitoring, reviewNoteRu ?? BuildRunningStatus(window));
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
        RecordHistory(final);
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
        _failedPathIds.Clear();
        _switchProblemRu = null;
        _healthyWindowsSinceReview = 0;
        _lastSwitchUtc = null;
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
        _switchProblemRu = null;

        if (_pathOptions is null || _flow is null || !_options.AllowRoutingChanges)
        {
            _switchProblemRu = "маршруты менять нельзя: смена пути выключена в настройках";
            return false;
        }

        IReadOnlyList<RealtimePathOption> options = await _pathOptions.GetOptionsAsync(_flow, ct).ConfigureAwait(false);

        // Путь, на котором уже пробовали и стало хуже, второй раз не выбираем: иначе контроль
        // будет прыгать между двумя плохими путями и рвать соединение чаще, чем чинить его.
        RealtimePathOption? next = options.FirstOrDefault(option =>
            !string.Equals(option.PathId, _activePathId, StringComparison.OrdinalIgnoreCase)
            && !_failedPathIds.Contains(option.PathId));

        if (next is null)
        {
            _switchProblemRu = options.Count == 0
                ? "альтернативного пути у этого сервера нет"
                : "других путей не осталось: на всех проверенных уже было хуже";
            return false;
        }

        return await ApplyPathSwitchAsync(
            next,
            $"Переключение пути: {current.RttMedianMs:F0} мс на текущем, пробуем «{next.DisplayName}».",
            $"Переключились на путь «{next.DisplayName}». Смотрим, стало ли лучше: решение принимается по замерам, а не по ожиданиям.",
            markCurrentPathFailed: true,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// «Нет ли пути быстрее?» — то, чем игровые ускорители занимаются постоянно. У нас это делается
    /// редко и честно: раз в <see cref="RealtimeOptions.HealthyWindowsBetweenRouteReviews"/> хороших
    /// окон, не чаще <see cref="RealtimeOptions.EffectiveRouteUpgradeCooldown"/>, и только по числам,
    /// измеренным в той же сессии. Текущий путь в норме — значит, его никто не «спасает»: уходим
    /// только если альтернатива быстрее ощутимо, а не на величину шума.
    /// </summary>
    private async Task<(bool Upgraded, string? NoteRu)> TryUpgradePathAsync(PathMeasurement window, CancellationToken ct)
    {
        if (_pathOptions is null || _flow is null || !_options.AllowRoutingChanges || !_appliedRouteAction)
        {
            return (false, null);
        }

        if (_options.HealthyWindowsBetweenRouteReviews <= 0)
        {
            return (false, null);
        }

        _healthyWindowsSinceReview++;
        if (_healthyWindowsSinceReview < _options.HealthyWindowsBetweenRouteReviews)
        {
            return (false, null);
        }

        _healthyWindowsSinceReview = 0;

        if (_lastSwitchUtc is { } lastSwitch
            && _clock() - lastSwitch < _options.EffectiveRouteUpgradeCooldown)
        {
            // Только что переключились (например, после просадки): даём маршруту пожить,
            // иначе контроль будет дёргать игру переключениями туда-обратно.
            return (false, null);
        }

        IReadOnlyList<RealtimePathOption> options = await _pathOptions.GetOptionsAsync(_flow, ct).ConfigureAwait(false);
        if (options.Count == 0)
        {
            // Свежие замеры не нашли узла, который быстрее прямого пути. Держать маршрут через узел
            // «потому что раньше было лучше» — самообман, но и снять его на ходу нельзя: маршрут
            // снимается остановкой оптимизации. Говорим об этом прямо, а не молчим.
            return (false,
                $"{BuildRunningStatus(window)} Свежие замеры не нашли узла быстрее прямого пути. " +
                "Возможно, маршрут через узел уже не нужен: снять его можно остановкой оптимизации — трафик пойдёт как обычно.");
        }

        RealtimePathOption? active = options.FirstOrDefault(option =>
            string.Equals(option.PathId, _activePathId, StringComparison.OrdinalIgnoreCase));

        if (active is null)
        {
            // Текущий узел выпал из вариантов: в свежих замерах он хуже прямого пути.
            return (false,
                $"{BuildRunningStatus(window)} Текущий маршрут в свежих замерах оказался хуже прямого и выпал из вариантов. " +
                "Снять его и вернуться на прямой можно остановкой оптимизации — сама она маршрут не снимает.");
        }

        // Без измеренных чисел активного пути сравнивать не с чем — молча остаёмся.
        if (active.MedianRttMs is not { } activeMs)
        {
            return (false, null);
        }

        RealtimePathOption? better = options
            .Where(option => !string.Equals(option.PathId, _activePathId, StringComparison.OrdinalIgnoreCase)
                             && !_failedPathIds.Contains(option.PathId)
                             && option.MedianRttMs.HasValue)
            .OrderBy(option => option.MedianRttMs!.Value)
            .FirstOrDefault();

        if (better?.MedianRttMs is not { } betterMs)
        {
            return (false, $"{BuildRunningStatus(window)} Других измеренных путей нет — остаёмся на текущем.");
        }

        // Порог — «не меньше 3 мс» и не меньше разброса серий обоих путей: шум не победа.
        double required = Math.Max(
            _options.RouteUpgradeMarginMs,
            Math.Max(active.SpreadMs ?? 0, better.SpreadMs ?? 0));

        double gain = activeMs - betterMs;

        if (gain < required)
        {
            return (false,
                $"{BuildRunningStatus(window)} Проверили альтернативы: «{better.DisplayName}» не быстрее текущего " +
                $"на ощутимую величину (разница {gain:F1} мс при пороге {required:F1} мс). Остаёмся на текущем пути.");
        }

        bool switched = await ApplyPathSwitchAsync(
            better,
            $"Поиск пути быстрее: «{better.DisplayName}» измерен на {gain:F1} мс лучше текущего (порог {required:F1} мс), пробуем.",
            $"Переключились на путь «{better.DisplayName}»: в этой сессии замеров он на {gain:F1} мс быстрее прежнего. " +
            "Решение по числам, а не по ожиданиям; если станет хуже — вернёмся: прежний путь остаётся рабочим кандидатом.",
            markCurrentPathFailed: false,
            ct).ConfigureAwait(false);

        if (!switched)
        {
            return (false,
                $"{BuildRunningStatus(window)} Путь «{better.DisplayName}» выглядел быстрее ({gain:F1} мс), " +
                $"но переключиться не удалось: {_switchProblemRu ?? "причина неизвестна"}. Остаёмся на текущем.");
        }

        return (true, null);
    }

    /// <summary>
    /// Общее ядро переключения: снять прежний маршрут, поставить новый, при отказе вернуть прежний.
    /// <paramref name="markCurrentPathFailed"/> — путь, с которого уходим, был плохим (тогда в него
    /// больше не возвращаемся) или просто медленнее (тогда он остаётся рабочим кандидатом).
    /// </summary>
    private async Task<bool> ApplyPathSwitchAsync(
        RealtimePathOption next,
        string startMessageRu,
        string successMessageRu,
        bool markCurrentPathFailed,
        CancellationToken ct)
    {
        _switchProblemRu = null;
        _warnings.Add(startMessageRu);

        OptimizationActionDescriptor? previous = _appliedRouteAction
            ? _applied.FirstOrDefault(action => action.Kind == OptimizationActionKind.RouteForGame)
            : null;

        if (previous is not null)
        {
            // Прежний маршрут снимаем ДО добавления нового: исполнитель не перезаписывает /32,
            // который уже стоит в таблице (это защита от чужих маршрутов) — свой прежний он тоже
            // не перезапишет, и «переключение» молча осталось бы на старом узле.
            OptimizationActionOutcome revert = await RevertAsync(previous, ct).ConfigureAwait(false);
            _applied.Remove(previous);
            _appliedRouteAction = false;

            if (!revert.Succeeded)
            {
                // Маршрут не снялся: добавлять второй /32 на тот же адрес нельзя — в таблице
                // окажется два пути, и какой победит, решит метрика, а не наши замеры.
                _applied.Add(previous);
                _appliedRouteAction = true;
                _switchProblemRu = $"прежний маршрут не снялся ({revert.MessageRu})";
                _warnings.Add($"Переключение отменено: {_switchProblemRu}.");
                return false;
            }
        }

        OptimizationActionOutcome outcome = await _actions.ApplyAsync(next.Action, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _warnings.Add($"Переключиться не удалось: {outcome.MessageRu}");
            _switchProblemRu = $"новый маршрут не применился ({outcome.MessageRu})";

            if (previous is not null)
            {
                OptimizationActionOutcome back = await _actions.ApplyAsync(previous, ct).ConfigureAwait(false);
                if (back.Succeeded)
                {
                    _applied.Add(previous);
                    _appliedRouteAction = true;
                    _warnings.Add("Прежний маршрут возвращён: трафик идёт как до попытки переключения.");
                }
                else
                {
                    _warnings.Add(
                        $"Прежний маршрут вернуть не удалось ({back.MessageRu}): трафик идёт по маршруту по умолчанию, " +
                        "сеть надо проверить вручную — «Починить сеть».");
                }
            }

            return false;
        }

        if (markCurrentPathFailed && _activePathId is not null)
        {
            _failedPathIds.Add(_activePathId);
        }

        _applied.Add(next.Action);
        _appliedRouteAction = true;
        _activePathId = next.PathId;
        _lastSwitchUtc = _clock();
        _healthyWindowsSinceReview = 0;
        _consecutiveBadWindows = 0;
        _consecutiveGoodWindows = 0;
        SetState(RealtimeOptimizationState.ActiveMonitoring, successMessageRu);
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

    /// <summary>
    /// Записать окно замера в историю графика. Измеренное — с числами, неизмеренное — точкой
    /// с null-полями и причиной: на графике это разрыв с подписью, а не «пинг 0 мс».
    /// </summary>
    private void RecordHistory(PathMeasurement window)
    {
        lock (_historyGate)
        {
            _history.Add(window.IsMeasurable
                ? new RealtimeMeasurementPoint(
                    _clock(),
                    true,
                    window.RttMinMs,
                    window.RttMedianMs,
                    window.RttP95Ms,
                    window.JitterMs,
                    window.LossPercent,
                    window.Samples,
                    window.MethodRu)
                : RealtimeMeasurementPoint.Unmeasured(_clock(), window.MethodRu));

            if (_history.Count > HistoryLimit)
            {
                _history.RemoveRange(0, _history.Count - HistoryLimit);
            }
        }
    }

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
        lock (_historyGate)
        {
            _history.Clear();
        }
        _flow = null;
        _baselineRttMs = null;
        _floorRttMs = null;
        _confirmationRttMs = null;
        _lastRttMs = null;
        _lastJitterMs = null;
        _lastLossPercent = null;
        _activePathId = null;
        _appliedRouteAction = false;
        _failedPathIds.Clear();
        _switchProblemRu = null;
        _healthyWindowsSinceReview = 0;
        _lastSwitchUtc = null;
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
