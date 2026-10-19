using System.Text.Json;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Data.Repositories;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.Service;

/// <summary>
/// Что служба делает на самом деле: собирает твики из каталога, снимает снапшот, применяет, проверяет,
/// умеет вернуть прежние настройки и рассказать, что происходит.
///
/// Здесь нет ни одного Windows-вызова — только порты. Поэтому «мозги» службы проверяются тестами
/// на Linux, а платформенная часть (<c>PingForge.Service.Windows</c>) остаётся тонким слоем.
///
/// Правило, которое держится строго: если чего-то нет (журнала, движка живой оптимизации, прав),
/// служба отвечает «не могу, потому что…». Молчаливая замена на «ладно, ничего не сделал» запрещена:
/// пользователь должен видеть, применены твики или нет.
/// </summary>
public sealed class LiveEngineOperations : IEngineOperations
{
    private readonly ITweakCatalog _catalog;
    private readonly ISnapshotService _snapshots;
    private readonly ITweakCoordinator _coordinator;
    private readonly AuditJournalRepository? _journal;
    private readonly RealtimeOptimizer? _realtime;
    private readonly Func<PerfProfile>? _readProfile;
    private readonly Action<PerfProfile>? _applyProfile;
    private readonly string _productName;
    private readonly string _appVersion;
    private readonly ISystemStateReader? _stateReader;
    private readonly IRoutingService? _routing;
    private readonly OptimizationSessionRepository? _sessions;

    /// <summary>
    /// Сессия, начатая этой службой и ещё не закрытая. Именно она дописывается «после» при остановке;
    /// после перезапуска службы незакрытая запись остаётся в базе и честно показывается незавершённой.
    /// </summary>
    private OptimizationSessionRecord? _currentSession;

    public LiveEngineOperations(
        ITweakCatalog catalog,
        ISnapshotService snapshots,
        ITweakCoordinator coordinator,
        bool isElevationAvailable,
        AuditJournalRepository? journal = null,
        RealtimeOptimizer? realtime = null,
        Func<PerfProfile>? readProfile = null,
        Action<PerfProfile>? applyProfile = null,
        string productName = "Custom Tweak By Doxaiz",
        string? appVersion = null,
        ISystemStateReader? stateReader = null,
        IRoutingService? routing = null,
        OptimizationSessionRepository? sessions = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _journal = journal;
        _realtime = realtime;
        _readProfile = readProfile;
        _applyProfile = applyProfile;
        _productName = productName;
        _appVersion = appVersion ?? typeof(LiveEngineOperations).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _stateReader = stateReader;
        _routing = routing;
        _sessions = sessions;
        IsElevationAvailable = isElevationAvailable;
    }

    public bool IsElevationAvailable { get; }

    public async Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default)
    {
        int tweakCount = _catalog.GetAllTweaks().Count;
        string rights = IsElevationAvailable
            ? "права администратора есть — меняющие команды работают"
            : "прав администратора нет — менять настройки Windows нельзя, доступны замеры и просмотр";

        // C7: служба называет того, кто подключился, — по процессу КЛИЕНТА. Если личность не
        // прочитана, так и пишем «не определён»: подставлять сюда саму службу (LocalSystem,
        // S-1-5-18) было бы ровно тем враньём, которое C7 чинит.
        EnginePeerIdentity client = peer ?? EnginePeerIdentity.Unknown("канал личность не передал");
        string clientNote = client.ProblemRu is null
            ? $"Клиент: {client.ClientName} (SID {client.ClientSid}), права администратора: {(client.ClientIsAdmin ? "есть" : "нет")}."
            : $"Клиент не определён ({client.ProblemRu}) — считаю, что прав администратора у него нет.";

        string message = $"Служба жива: {_productName} {_appVersion}, протокол {EngineProtocol.Version}, " +
            $"твиков в каталоге: {tweakCount}. {rights}. {clientNote}";

        string data = Encode(new PingData(
            _productName,
            _appVersion,
            EngineProtocol.Version,
            tweakCount,
            IsElevationAvailable,
            EngineCommandCatalog.All.Count,
            client.ClientName,
            client.ClientSid,
            client.ClientIsAdmin,
            client.ClientProcessId));

        return await Task.FromResult(EngineOperationResult.Success(message, data)).ConfigureAwait(false);
    }

    public async Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default)
    {
        var commands = EngineCommandCatalog.All
            .Select(item => new CommandData(item.WireName, item.TitleRu, item.WhatRu, item.NeedsElevation))
            .ToList();

        string data = Encode(new CapabilitiesData(EngineProtocol.Version, commands));
        return await Task.FromResult(EngineOperationResult.Success(EngineCommandCatalog.DescribeRu(), data)).ConfigureAwait(false);
    }

    public async Task<EngineOperationResult> StatusAsync(CancellationToken ct = default)
    {
        int pending = 0;
        if (_journal is not null)
        {
            IReadOnlyList<PendingOperationRecord> operations = await _journal.GetPendingOperationsAsync(ct).ConfigureAwait(false);
            pending = operations.Count;
        }

        RealtimeSnapshot? snapshot = _realtime?.Current;
        RealtimeOptimizationState state = _realtime?.State ?? RealtimeOptimizationState.Idle;
        PerfProfile profile = _readProfile?.Invoke() ?? PerfProfile.Balanced;

        string gameNote = snapshot?.GameTitleRu is { Length: > 0 } title
            ? $"Игра: {title}, задержка сейчас {FormatMs(snapshot.CurrentRttMs)}."
            : "Игра не найдена: живая оптимизация не запущена.";

        string pendingNote = pending == 0
            ? "Незакрытых операций в журнале нет."
            : $"Незакрытых операций в журнале: {pending} — их снимет команда repair-network.";

        string realtimeNote = _realtime is null
            ? "Движок живой оптимизации не подключён."
            : $"Движок живой оптимизации подключён. Состояние: {StateRu(state)}.";

        string message = $"{realtimeNote} {gameNote} {pendingNote} " +
            $"Профиль потребления: {ResourceBudgetOptions.DescribeProfile(profile)}.";

        string data = Encode(new StatusData(
            _productName,
            _appVersion,
            EngineProtocol.Version,
            IsElevationAvailable,
            _catalog.GetAllTweaks().Count,
            StateWire(state),
            StateRu(state),
            snapshot?.GameTitleRu,
            snapshot?.Endpoint,
            snapshot?.BaselineRttMs,
            snapshot?.CurrentRttMs,
            snapshot?.QualityLabelRu,
            EngineNames.ProfileWireName(profile),
            ResourceBudgetOptions.DescribeProfile(profile),
            pending,
            ChartSamples()));

        return await Task.FromResult(EngineOperationResult.Success(message, data)).ConfigureAwait(false);
    }

    /// <summary>
    /// Точки истории замеров для графика сети в разделе «Оптимизация» (задача F). Отдаются только
    /// замеры этой сессии живой оптимизации: окно графика — максимум 15 минут, поэтому точки старше
    /// не отдаются вовсе. Неизмеренное окно — точка с null-полями: разрыв на графике, а не ноль.
    /// </summary>
    private IReadOnlyList<ChartSampleData> ChartSamples()
    {
        if (_realtime is null)
        {
            return [];
        }

        DateTime cutoffUtc = DateTime.UtcNow - ChartWindowLimit;
        return _realtime.RecentSamples
            .Where(point => point.AtUtc >= cutoffUtc)
            .Select(point => new ChartSampleData(
                point.AtUtc,
                point.IsMeasurable,
                point.RttMinMs,
                point.RttMedianMs,
                point.RttP95Ms,
                point.JitterMadMs,
                point.LossPercent))
            .ToList();
    }

    /// <summary>Максимальное окно графика: 15 минут. Дальше точки не отдаются — «за пределами запуска пусто».</summary>
    private static readonly TimeSpan ChartWindowLimit = TimeSpan.FromMinutes(15);

    public async Task<EngineOperationResult> ApplyPresetAsync(
        TweakProfilePreset preset,
        bool requestRestorePoint,
        CancellationToken ct = default)
    {
        IReadOnlyList<ITweak> tweaks = _catalog.GetByPreset(preset);
        if (tweaks.Count == 0)
        {
            return EngineOperationResult.Failure(
                $"В пресете «{EngineNames.PresetTitleRu(preset)}» нет ни одного твика: каталог не отдал состав. " +
                "Применять нечего — это ошибка каталога, а не «всё уже включено».");
        }

        string title = $"Применение пресета «{EngineNames.PresetTitleRu(preset)}»";
        TweakBatchReport report = await _coordinator
            .ExecuteBatchAsync(title, preset, tweaks, requestRestorePoint, ct)
            .ConfigureAwait(false);

        return BuildBatchResult(report, EngineNames.PresetTitleRu(preset));
    }

    public async Task<EngineOperationResult> ApplyTweaksAsync(
        IReadOnlyList<string> tweakCodes,
        bool requestRestorePoint,
        CancellationToken ct = default)
    {
        var resolved = new List<ITweak>(tweakCodes.Count);
        var unknown = new List<string>();

        foreach (string code in tweakCodes)
        {
            ITweak? tweak = _catalog.GetByCode(code);
            if (tweak is null)
            {
                unknown.Add(code);
                continue;
            }

            if (!resolved.Contains(tweak))
            {
                resolved.Add(tweak);
            }
        }

        if (unknown.Count > 0)
        {
            return EngineOperationResult.Failure(
                $"Твиков с такими кодами в каталоге нет: {string.Join(", ", unknown)}. " +
                $"Всего в каталоге {_catalog.GetAllTweaks().Count} твиков; правильные коды видно в списке твиков.");
        }

        if (resolved.Count == 0)
        {
            return EngineOperationResult.Failure("Список твиков пуст: применять нечего.");
        }

        string title = $"Применение выбранных твиков ({resolved.Count})";
        TweakBatchReport report = await _coordinator
            .ExecuteBatchAsync(title, TweakProfilePreset.CustomUserPreset, resolved, requestRestorePoint, ct)
            .ConfigureAwait(false);

        return BuildBatchResult(report, "свой набор");
    }

    public async Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default)
    {
        IReadOnlyList<SystemSnapshot> snapshots = await _snapshots.GetAllSnapshotsAsync(ct).ConfigureAwait(false);
        if (snapshots.Count == 0)
        {
            return EngineOperationResult.Failure(
                "Снапшотов нет: программа ещё ничего не меняла в этой системе. Возвращать нечего.");
        }

        SystemSnapshot? target;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            target = snapshots.OrderByDescending(s => s.CreatedUtc).First();
        }
        else
        {
            target = snapshots.FirstOrDefault(s => string.Equals(s.SnapshotId, snapshotId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return EngineOperationResult.Failure(
                    $"Снапшот «{snapshotId}» не найден. Есть {snapshots.Count} снапшотов; последний — " +
                    $"«{snapshots.OrderByDescending(s => s.CreatedUtc).First().SnapshotId}».");
            }
        }

        if (target.WasRolledBack)
        {
            return EngineOperationResult.Failure(
                $"Снапшот «{target.SnapshotId}» уже откатывали {target.RolledBackUtc:dd.MM.yyyy HH:mm} UTC. " +
                "Повторный откат ничего не изменит: берите снапшот следующего применения.");
        }

        SnapshotRollbackReport report = await _snapshots.RollbackSnapshotAsync(target.SnapshotId, ct).ConfigureAwait(false);

        string message = $"{report.SummaryMessageRu} Возвращено параметров: {report.RolledBackCount}, " +
            $"не получилось: {report.FailedCount}." +
            (report.OrphanedTweakCodes.Count > 0
                ? $" Параметров без твика в каталоге: {report.OrphanedTweakCodes.Count} — они остались как были."
                : string.Empty);

        string data = Encode(new RollbackData(
            report.SnapshotId,
            report.Status.ToString(),
            report.RolledBackCount,
            report.FailedCount,
            report.OrphanedTweakCodes.Count,
            target.CreatedUtc));

        return new EngineOperationResult(report.Status != SnapshotRollbackStatus.Failed, message, data);
    }

    public async Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default)
    {
        if (_journal is null)
        {
            return EngineOperationResult.Failure(
                "Журнал не подключён: служба не может проверить, осталось ли что-то после аварии. Ничего не меняю.");
        }

        IReadOnlyList<PendingOperationRecord> pending = await _journal.GetPendingOperationsAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return EngineOperationResult.Success(
                "Остатков после аварии не найдено: незакрытых операций в журнале нет, чужие маршруты программа не трогает.",
                Encode(new RepairData(0, 0, 0, [])));
        }

        var byCategory = pending
            .GroupBy(operation => operation.Category, StringComparer.Ordinal)
            .Select(group => $"{group.Key}: {group.Count()}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        string message = $"Найдено незакрытых операций: {pending.Count} ({string.Join(", ", byCategory)}). " +
            "Снятие выполняется по одной, с проверкой результата; операции, которые снять не удалось, останутся в журнале " +
            "и будут названы.";

        return new EngineOperationResult(
            Ok: false,
            MessageRu: message + " Автоматическое снятие в этой сборке ещё не подключено: нужен исполнитель откатов.",
            DataJson: Encode(new RepairData(pending.Count, 0, pending.Count, byCategory)));
    }

    public async Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default)
    {
        if (_journal is null)
        {
            return EngineOperationResult.Failure("Журнал не подключён: показать записи нечем.");
        }

        IReadOnlyList<ModificationLogRecord> records = await _journal.GetModificationLogTailAsync(limit, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return EngineOperationResult.Success(
                "Журнал пуст: программа ещё ничего не меняла.",
                Encode(new JournalData(0, [])));
        }

        int failed = records.Count(record => !record.Succeeded);
        string message = $"Последние {records.Count} записей журнала (новые сверху). Не получилось: {failed}.";

        var rows = records
            .Select(record => new JournalRow(
                record.LoggedUtc,
                record.EventKind,
                record.TweakCode,
                record.TweakTitleRu,
                record.PreviousValueText,
                record.AppliedValueText,
                record.VerifiedActualText,
                record.Succeeded,
                record.RebootNeeded,
                record.ExplanationRu))
            .ToList();

        return await Task.FromResult(EngineOperationResult.Success(message, Encode(new JournalData(records.Count, rows)))).ConfigureAwait(false);
    }

    public async Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default)
    {
        if (_realtime is null)
        {
            return EngineOperationResult.Failure(
                "Движок живой оптимизации не подключён: запускать нечего. Это сборка без сетевого слоя.");
        }

        if (!start)
        {
            RealtimeStopReport report = await _realtime.StopAsync(ct).ConfigureAwait(false);
            await RecordSessionStopAsync(report, ct).ConfigureAwait(false);
            string message = report.HonestSummaryRu +
                (report.EverythingReverted
                    ? " Все свои изменения сняты."
                    : $" Снять удалось не всё: {string.Join(" ", report.RevertMessagesRu)}");

            string data = Encode(new RealtimeData(
                "idle",
                report.GameTitleRu,
                report.Endpoint,
                report.BaselineRttMs,
                report.FinalRttMs,
                report.ImprovementMs,
                report.EverythingReverted,
                report.AppliedActionTitles));

            return new EngineOperationResult(report.EverythingReverted, message, data);
        }

        // Настройки контроля берутся из профиля потребления, который человек выбрал в окне:
        // экономный профиль растягивает окна, «максимум реакции» — наоборот.
        PerfProfile profile = _readProfile?.Invoke() ?? PerfProfile.Balanced;
        var options = new RealtimeOptions(Budget: new ResourceBudgetOptions(profile));

        DateTime startedUtc = DateTime.UtcNow;
        string sessionId = Guid.NewGuid().ToString("N");

        RealtimeSnapshot snapshot = await _realtime
            .StartAsync(options, isAdministrator: IsElevationAvailable, preferredGame: null, ct: ct)
            .ConfigureAwait(false);

        await RecordSessionStartAsync(sessionId, startedUtc, snapshot, ct).ConfigureAwait(false);
        // При отказе не добавляем хвост «Игра: не найдена, задержка: не измерено»: человек и так
        // читает причину отказа, а хвост превращает её в длинную склейку из двух предложений.
        string startMessage = snapshot.State switch
        {
            RealtimeOptimizationState.Failed => snapshot.StatusRu,
            RealtimeOptimizationState.ReadOnlyNoAdmin =>
                "Оптимизация запущена без прав: идут только замеры, менять сеть служба не станет.",
            _ => $"{snapshot.StatusRu} Игра: {snapshot.GameTitleRu ?? "не найдена"}, задержка: {FormatMs(snapshot.CurrentRttMs)}."
        };

        string startData = Encode(new RealtimeData(
            StateWire(snapshot.State),
            snapshot.GameTitleRu,
            snapshot.Endpoint,
            snapshot.BaselineRttMs,
            snapshot.CurrentRttMs,
            snapshot.ImprovementMs,
            EverythingReverted: true,
            snapshot.AppliedActions.Select(action => action.TitleRu).ToList()));

        return new EngineOperationResult(snapshot.State != RealtimeOptimizationState.Failed, startMessage, startData);
    }

    public async Task<EngineOperationResult> SessionsAsync(int limit, CancellationToken ct = default)
    {
        if (_sessions is null)
        {
            return EngineOperationResult.Failure("История сессий не подключена: показать записи нечем.");
        }

        IReadOnlyList<OptimizationSessionRecord> records = await _sessions.GetRecentAsync(limit, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return EngineOperationResult.Success(
                "История сессий пуста: живая оптимизация ещё не запускалась. Это не ошибка — записывать пока нечего.",
                Encode(new SessionsData(0, [])));
        }

        int completed = records.Count(record => record.IsCompleted);
        int unfinished = records.Count - completed;
        string message = $"Сессий: {records.Count}, завершено: {completed}, без итога: {unfinished}. " +
            "Измеренные числа — только из сессий; «не завершена» никогда не превращается в улучшение.";

        var rows = records
            .Select(record => new SessionRowData(
                record.SessionId,
                record.StartedUtc,
                record.StoppedUtc,
                record.GameTitleRu,
                record.Endpoint,
                record.HasGame,
                record.IsMeasurable,
                record.BaselineRttMs,
                record.FinalRttMs,
                record.ImprovementMs,
                record.Actions,
                record.RollbackState,
                OptimizationSessionMapping.RollbackStateRu(record.RollbackState),
                record.IsCompleted,
                record.StatusNoteRu,
                record.Outcome))
            .ToList();

        return await Task.FromResult(EngineOperationResult.Success(
            message, Encode(new SessionsData(records.Count, rows)))).ConfigureAwait(false);
    }

    public async Task<EngineOperationResult> ClearSessionsAsync(CancellationToken ct = default)
    {
        if (_sessions is null)
        {
            return EngineOperationResult.Failure("История сессий не подключена: очищать нечего.");
        }

        if (_currentSession is not null)
        {
            return EngineOperationResult.Failure(
                "Живая оптимизация ещё идёт: её сессия пишется прямо сейчас. Остановите оптимизацию и очистите историю после этого — " +
                "иначе запись вернулась бы при остановке.");
        }

        int deleted = await _sessions.DeleteAllAsync(ct).ConfigureAwait(false);
        return EngineOperationResult.Success(
            deleted == 0
                ? "История сессий уже была пуста — удалять было нечего. Журнал изменений системы не тронут."
                : $"История сессий очищена: удалено записей — {deleted}. Журнал изменений системы не тронут: он остался как был.");
    }

    /// <summary>
    /// Начало сессии: пишется сразу, ещё до исхода. Если служба упадёт или сессия оборвётся,
    /// запись останется с честным «не завершена, итог неизвестен» — без выдуманного улучшения.
    /// </summary>
    private async Task RecordSessionStartAsync(
        string sessionId, DateTime startedUtc, RealtimeSnapshot snapshot, CancellationToken ct)
    {
        if (_sessions is null)
        {
            return;
        }

        OptimizationSessionRecord record = OptimizationSessionMapping.FromStart(sessionId, startedUtc, snapshot);
        _currentSession = record;
        await TrySaveSessionAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Остановка: та же запись дописывается настоящими «после», действиями и пометкой откатa.
    /// Нет измеренного «после» — сессия остаётся незавершённой, и это написано словами.
    /// </summary>
    private async Task RecordSessionStopAsync(RealtimeStopReport report, CancellationToken ct)
    {
        if (_sessions is null || _currentSession is not { } session)
        {
            return;
        }

        OptimizationSessionRecord completed = OptimizationSessionMapping.Complete(session, report, DateTime.UtcNow);

        await TrySaveSessionAsync(completed, ct).ConfigureAwait(false);
        _currentSession = null;
    }

    /// <summary>
    /// История не должна ломать саму оптимизацию: сбой записи в базу не отменяет применение
    /// и откат. Молчаливым это не остаётся — сессии в журнале просто не появится, и это видно.
    /// </summary>
    private async Task TrySaveSessionAsync(OptimizationSessionRecord record, CancellationToken ct)
    {
        if (_sessions is null)
        {
            return;
        }

        try
        {
            await _sessions.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Сбой базы не должен отменять уже выполненные действия оптимизации.
        }
    }

    public async Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default)
    {
        if (_applyProfile is null || _readProfile is null)
        {
            return EngineOperationResult.Failure(
                "Настройки потребления не подключены: профиль менять нечем.");
        }

        _applyProfile(profile);
        PerfProfile actual = _readProfile();

        if (actual != profile)
        {
            return EngineOperationResult.Failure(
                $"Профиль «{ResourceBudgetOptions.DescribeProfile(profile)}» не сохранился: фактически стоит " +
                $"«{ResourceBudgetOptions.DescribeProfile(actual)}». Настройка не применилась.");
        }

        int perMinute = new ResourceBudgetOptions(actual).MeasurementsPerMinute(TimeSpan.FromSeconds(2), 1);
        string message = $"Профиль потребления: {ResourceBudgetOptions.DescribeProfile(actual)}. " +
            $"Контроль качества: {new ResourceBudgetOptions(actual).EffectiveMonitorInterval(TimeSpan.FromSeconds(2)).TotalSeconds:0.#} с, " +
            $"до {perMinute} проб в минуту.";

        string data = Encode(new ProfileData(EngineNames.ProfileWireName(actual), ResourceBudgetOptions.DescribeProfile(actual), perMinute));
        return await Task.FromResult(EngineOperationResult.Success(message, data)).ConfigureAwait(false);
    }

    private EngineOperationResult BuildBatchResult(TweakBatchReport report, string whatRu)
    {
        bool everythingOk = report.FailedCount == 0;

        string message = $"{whatRu}: применено {report.AppliedCount} из {report.Results.Count}, " +
            $"без изменений {report.NoOpCount}, пропущено {report.SkippedCount}, ошибок {report.FailedCount}. " +
            report.RestorePointMessageRu +
            (report.RebootRequired ? " Часть твиков вступит в силу после перезагрузки — это указано в журнале." : string.Empty);

        var failures = report.Results
            .Where(result => result.Outcome == TweakOutcome.Failed)
            .Select(result => $"{result.TweakCode}: {result.FailureReasonRu ?? "без объяснения"}")
            .ToList();

        if (!everythingOk)
        {
            message += failures.Count > 0 ? $" Не получилось: {string.Join("; ", failures)}." : string.Empty;
        }

        string data = Encode(new BatchData(
            report.Snapshot.SnapshotId,
            report.Results.Count,
            report.AppliedCount,
            report.NoOpCount,
            report.SkippedCount,
            report.FailedCount,
            report.RebootRequired,
            report.RestorePointStatus.ToString(),
            failures));

        return new EngineOperationResult(everythingOk, message, data);
    }

    private static string StateWire(RealtimeOptimizationState state) => state switch
    {
        RealtimeOptimizationState.ActiveMonitoring => "active-monitoring",
        RealtimeOptimizationState.MeasuringBaseline => "measuring-baseline",
        RealtimeOptimizationState.FindingGame => "finding-game",
        RealtimeOptimizationState.ChoosingRoute => "choosing-route",
        RealtimeOptimizationState.ApplyingActions => "applying-actions",
        RealtimeOptimizationState.Degraded => "degraded",
        RealtimeOptimizationState.ReadOnlyNoAdmin => "read-only-no-admin",
        RealtimeOptimizationState.Stopping => "stopping",
        RealtimeOptimizationState.Stopped => "stopped",
        RealtimeOptimizationState.Failed => "failed",
        _ => "idle",
    };

    private static string StateRu(RealtimeOptimizationState state) => state switch
    {
        RealtimeOptimizationState.ActiveMonitoring => "идёт контроль качества",
        RealtimeOptimizationState.MeasuringBaseline => "меряю «до»",
        RealtimeOptimizationState.FindingGame => "ищу запущенную игру",
        RealtimeOptimizationState.ChoosingRoute => "выбираю путь до сервера",
        RealtimeOptimizationState.ApplyingActions => "применяю действия",
        RealtimeOptimizationState.Degraded => "качество просело, жду возможность переключиться",
        RealtimeOptimizationState.ReadOnlyNoAdmin => "только замеры: нет прав администратора",
        RealtimeOptimizationState.Stopping => "останавливаюсь и снимаю изменения",
        RealtimeOptimizationState.Stopped => "остановлена",
        RealtimeOptimizationState.Failed => "не смогла запуститься",
        _ => "не запущена",
    };

    public async Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default)
    {
        if (_stateReader is null)
        {
            return EngineOperationResult.Failure(
                "Служба не умеет читать состояние системы: в этой сборке чтение не подключено. " +
                "Это наша недоработка, а не ваша ошибка — остальные команды работают как обычно.");
        }

        SystemStateReading reading = await _stateReader.ReadAsync(ct).ConfigureAwait(false);

        string adapterText = reading.ActiveAdapter is null
            ? "активный адаптер не определён"
            : $"активный адаптер «{reading.ActiveAdapter.DisplayName}»" +
              (reading.LinkSpeedMbps is null
                  ? ", скорость соединения не прочитана"
                  : $", {reading.LinkSpeedMbps} Мбит/с");

        string mtuText = reading.CurrentMtuBytes is null ? "MTU не прочитан" : $"MTU {reading.CurrentMtuBytes} байт";

        string rightsText = reading.IsRunningAsAdministrator
            ? "права администратора есть"
            : "прав администратора нет";

        string unresolvedText = reading.UnresolvedFields.Count == 0
            ? "Прочитано всё, что запрашивали."
            : $"Не прочитано полей: {reading.UnresolvedFields.Count} — они остались неизвестными, " +
              "а не выдуманными: интерфейс покажет их серым с причиной.";

        string message = $"{reading.WindowsEditionAndBuild}. {rightsText}; {adapterText}; {mtuText}. {unresolvedText}";

        string data = Encode(new SystemStateData(
            reading.WindowsEditionAndBuild,
            reading.IsRunningAsAdministrator,
            reading.ActiveAdapter?.InterfaceGuid,
            reading.ActiveAdapter?.DisplayName,
            reading.ActiveAdapter?.IsUp,
            reading.LinkSpeedMbps,
            reading.CurrentMtuBytes,
            reading.PowerSchemeGuid,
            reading.PowerSchemeName,
            reading.TcpAutoTuningLevel,
            reading.CongestionProvider,
            reading.IsRssEnabled,
            reading.IsRscEnabled,
            reading.IsEcnEnabled,
            reading.IsNagleDisabledOnActiveNic,
            reading.IsGameModeEnabled,
            reading.IsDeliveryOptimizationP2pEnabled,
            reading.IsSystemRestoreEnabledOnSystemDrive,
            reading.IsTimerResolutionRaised,
            reading.ConfiguredDnsServers,
            reading.AttachedNdisFilters,
            reading.UnresolvedFields));

        return EngineOperationResult.Success(message, data);
    }

    public async Task<EngineOperationResult> DiscoverRoutingNodesAsync(CancellationToken ct = default)
    {
        if (_routing is null)
        {
            return EngineOperationResult.Failure(
                "Слой маршрутизации не подключён: это сборка службы без сетевого слоя. Окно покажет узлы, " +
                "которые сохранены в настройках, а найти новые нечем.");
        }

        return await _routing.DiscoverAsync(ct).ConfigureAwait(false);
    }

    public async Task<EngineOperationResult> MeasureRoutingPathsAsync(CancellationToken ct = default)
    {
        if (_routing is null)
        {
            return EngineOperationResult.Failure(
                "Слой маршрутизации не подключён: измерять пути нечем. Остальные команды службы работают как обычно.");
        }

        return await _routing.MeasureAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Следы перед удалением (задача T). Считаем по фактическим хранилищам: журнал изменений,
    /// незакрытые операции, список снапшотов, состояние живой оптимизации. Врём цифрам ровно
    /// ничего: нет журнала — в ответе нули и честная оговорка, а не выдуманное «всё чисто».
    /// </summary>
    public async Task<EngineOperationResult> UninstallCheckAsync(CancellationToken ct = default)
    {
        int applied = 0;
        int open = 0;
        bool journalAvailable = _journal is not null;
        if (_journal is not null)
        {
            applied = await _journal.CountModificationLogEntriesAsync(ct).ConfigureAwait(false);
            IReadOnlyList<PendingOperationRecord> pendingOps =
                await _journal.GetPendingOperationsAsync(ct).ConfigureAwait(false);
            open = pendingOps.Count;
        }

        int snapshots = (await _snapshots.GetAllSnapshotsAsync(ct).ConfigureAwait(false)).Count(s => !s.WasRolledBack);
        RealtimeOptimizationState realtimeState = _realtime?.State ?? RealtimeOptimizationState.Idle;
        UninstallState state = new(applied, open, snapshots, realtimeState != RealtimeOptimizationState.Idle);

        bool ask = UninstallGuard.MustAsk(state);
        string options = ask
            ? " Варианты: " + string.Join("; ", UninstallGuard.OptionsRu
                .Select((option, index) => $"{index + 1}) {option}")) + $". По умолчанию — {UninstallGuard.OptionsRu[0].ToLowerInvariant()}."
            : string.Empty;
        string caveat = journalAvailable
            ? string.Empty
            : " Журнал изменений к этой службе не подключён: чисел по нему нет, есть только снапшоты.";

        return EngineOperationResult.Success(
            UninstallGuard.QuestionRu(state) + options +
            (ask ? " " + UninstallGuard.KeepChangesWarningRu(state) + "." : string.Empty) + caveat,
            Encode(new UninstallCheckData(
                state.AppliedChanges,
                state.OpenOperations,
                state.Snapshots,
                state.OptimizationRunning,
                ask,
                ask ? UninstallGuard.OptionsRu : Array.Empty<string>(),
                UninstallGuard.QuestionRu(state),
                ask ? UninstallGuard.KeepChangesWarningRu(state) : string.Empty)));
    }

    private static string FormatMs(double? value) => value is null ? "не измерено" : $"{value.Value:0.#} мс";

    private static string Encode<T>(T payload) => JsonSerializer.Serialize(payload, EngineJson.Options);

    // ── то, что служба отдаёт в поле данных: интерфейс читает это как есть, без догадок ─────────

    private sealed record UninstallCheckData(
        int AppliedChanges,
        int OpenOperations,
        int Snapshots,
        bool OptimizationRunning,
        bool MustAsk,
        IReadOnlyList<string> OptionsRu,
        string QuestionRu,
        string WarningRu);

    private sealed record PingData(
        string Product,
        string AppVersion,
        int ProtocolVersion,
        int TweakCount,
        bool IsElevationAvailable,
        int CommandCount,
        string ClientName,
        string ClientSid,
        bool ClientIsAdmin,
        int ClientProcessId);

    private sealed record CommandData(string Wire, string TitleRu, string WhatRu, bool NeedsElevation);

    private sealed record CapabilitiesData(int ProtocolVersion, IReadOnlyList<CommandData> Commands);

    private sealed record StatusData(
        string Product,
        string AppVersion,
        int ProtocolVersion,
        bool IsElevationAvailable,
        int TweakCount,
        string RealtimeState,
        string RealtimeStateRu,
        string? GameTitleRu,
        string? Endpoint,
        double? BaselineRttMs,
        double? CurrentRttMs,
        string? QualityLabelRu,
        string ProfileWire,
        string ProfileRu,
        int PendingOperations,
        IReadOnlyList<ChartSampleData> ChartSamples);

    /// <summary>Точка графика: все числа необязательные — «не измерялось» остаётся null, а не нулём.</summary>
    private sealed record ChartSampleData(
        DateTime AtUtc,
        bool Measured,
        double? RttMinMs,
        double? RttMedianMs,
        double? RttP95Ms,
        double? JitterMadMs,
        double? LossPercent);

    private sealed record BatchData(
        string SnapshotId,
        int Total,
        int Applied,
        int NoOp,
        int Skipped,
        int Failed,
        bool RebootRequired,
        string RestorePointStatus,
        IReadOnlyList<string> Failures);

    private sealed record RollbackData(
        string SnapshotId,
        string Status,
        int RolledBack,
        int Failed,
        int Orphaned,
        DateTime CreatedUtc);

    private sealed record RepairData(
        int PendingOperations,
        int Reverted,
        int LeftAsIs,
        IReadOnlyList<string> ByCategory);

    private sealed record JournalRow(
        DateTime LoggedUtc,
        string EventKind,
        string TweakCode,
        string TweakTitleRu,
        string PreviousValueText,
        string AppliedValueText,
        string VerifiedActualText,
        bool Succeeded,
        bool RebootNeeded,
        string ExplanationRu);

    private sealed record JournalData(int Count, IReadOnlyList<JournalRow> Records);

    /// <summary>
    /// Строка истории сессий. Все числа nullable: у незавершённой сессии «после» нет, и это
    /// остаётся null, а не нулём и не «улучшением на 0 мс».
    /// </summary>
    private sealed record SessionRowData(
        string SessionId,
        DateTime StartedUtc,
        DateTime? StoppedUtc,
        string GameTitleRu,
        string Endpoint,
        bool HasGame,
        bool IsMeasurable,
        double? BaselineRttMs,
        double? FinalRttMs,
        double? ImprovementMs,
        IReadOnlyList<string> Actions,
        string RollbackState,
        string RollbackStateRu,
        bool IsCompleted,
        string StatusRu,
        string Outcome);

    private sealed record SessionsData(int Count, IReadOnlyList<SessionRowData> Records);

    private sealed record RealtimeData(
        string State,
        string? GameTitleRu,
        string? Endpoint,
        double? BaselineRttMs,
        double? CurrentRttMs,
        double? ImprovementMs,
        bool EverythingReverted,
        IReadOnlyList<string> AppliedActionTitles);

    private sealed record ProfileData(string ProfileWire, string ProfileRu, int MeasurementsPerMinute);

    /// <summary>
    /// Состояние системы для интерфейса: поля повторяют <c>SystemStateReading</c> один в один, включая null
    /// у всего неизвестного. Интерфейс обязан показывать null серым («не прочитано»), а не нулём.
    /// </summary>
    private sealed record SystemStateData(
        string WindowsEditionAndBuild,
        bool IsRunningAsAdministrator,
        string? AdapterInterfaceGuid,
        string? AdapterDisplayName,
        bool? AdapterIsUp,
        int? LinkSpeedMbps,
        int? CurrentMtuBytes,
        string? PowerSchemeGuid,
        string? PowerSchemeName,
        string? TcpAutoTuningLevel,
        string? CongestionProvider,
        bool? IsRssEnabled,
        bool? IsRscEnabled,
        bool? IsEcnEnabled,
        bool? IsNagleDisabledOnActiveNic,
        bool? IsGameModeEnabled,
        bool? IsDeliveryOptimizationP2pEnabled,
        bool? IsSystemRestoreEnabledOnSystemDrive,
        bool? IsTimerResolutionRaised,
        IReadOnlyList<string> ConfiguredDnsServers,
        IReadOnlyList<string> AttachedNdisFilters,
        IReadOnlyList<string> UnresolvedFields);
}
