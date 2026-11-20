using PingForge.Data.Repositories;
using PingForge.Orchestration.Realtime;

namespace PingForge.Service;

/// <summary>
/// Превращает состояние живой оптимизации в запись истории сессий (задача J).
///
/// Правила честности собраны в одном месте, чтобы их нельзя было обойти по частям:
///   • нет измеренного «до» и «после» — сессия незавершённая, а не «улучшение на 0 мс»;
///   • игра не найдена — отдельный исход и отдельная подпись, а не пустая строка;
///   • снято не всё — так и написано, а не «готово».
/// </summary>
public static class OptimizationSessionMapping
{
    /// <summary>
    /// Запись на момент старта. Пишется сразу: если служба упадёт или сессия оборвётся,
    /// в истории останется честное «не завершена, итог неизвестен».
    /// </summary>
    public static OptimizationSessionRecord FromStart(string sessionId, DateTime startedUtc, RealtimeSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);

        bool noGame = snapshot.State == RealtimeOptimizationState.Failed
            && string.IsNullOrWhiteSpace(snapshot.GameTitleRu);

        return new OptimizationSessionRecord(
            SessionId: sessionId,
            StartedUtc: startedUtc,
            StoppedUtc: null,
            GameTitleRu: noGame ? "Игра не найдена" : snapshot.GameTitleRu ?? "Игра не определена",
            ProcessExeName: string.Empty,
            Endpoint: snapshot.Endpoint ?? string.Empty,
            IsMeasurable: snapshot.BaselineRttMs is not null,
            BaselineRttMs: snapshot.BaselineRttMs,
            FinalRttMs: null,
            ImprovementMs: null,
            Actions: [],
            RollbackState: SessionRollbackStates.Unknown,
            IsCompleted: false,
            Outcome: noGame ? SessionOutcomes.NoGame : SessionOutcomes.NotFinished,
            BaselineMethodTag: snapshot.BaselineMethodTag,
            FinalMethodTag: string.Empty,
            StatusNoteRu: noGame
                ? "Игра не найдена: замер не состоялся — итог неизвестен."
                : "Сессия не завершена: итог неизвестен, «после» ещё не измерено.");
    }

    /// <summary>
    /// Та же запись после остановки: «после», действия и пометка откатa. Нет измеренного «после» —
    /// сессия остаётся незавершённой, и это написано словами.
    /// </summary>
    public static OptimizationSessionRecord Complete(
        OptimizationSessionRecord session, RealtimeStopReport report, DateTime stoppedUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(report);

        bool measured = report.BaselineRttMs is not null && report.FinalRttMs is not null;
        bool noGame = session.Outcome == SessionOutcomes.NoGame;

        return session with
        {
            StoppedUtc = stoppedUtc,
            GameTitleRu = noGame ? session.GameTitleRu : report.GameTitleRu,
            Endpoint = string.IsNullOrWhiteSpace(report.Endpoint) || report.Endpoint == "—"
                ? session.Endpoint
                : report.Endpoint,
            IsMeasurable = measured || session.BaselineRttMs is not null,
            // Способ замера идёт вместе с числами: пара «до/после» могла быть снята разными способами.
            BaselineMethodTag = string.IsNullOrWhiteSpace(report.BaselineMethodTag)
                ? session.BaselineMethodTag
                : report.BaselineMethodTag,
            FinalMethodTag = report.FinalMethodTag,
            BaselineRttMs = report.BaselineRttMs ?? session.BaselineRttMs,
            FinalRttMs = report.FinalRttMs,
            ImprovementMs = report.ImprovementMs,
            Actions = report.AppliedActionTitles,
            RollbackState = noGame
                ? SessionRollbackStates.Unknown
                : report.EverythingReverted
                    ? SessionRollbackStates.EverythingReverted
                    : SessionRollbackStates.PartiallyReverted,
            IsCompleted = measured,
            Outcome = noGame ? SessionOutcomes.NoGame : measured ? SessionOutcomes.Completed : SessionOutcomes.NotFinished,
            StatusNoteRu = noGame
                ? "Игра не найдена: замер не состоялся — итог неизвестен."
                : measured
                    ? (report.EverythingReverted
                        ? "Сессия завершена: «после» измерено, свои изменения сняты."
                        : "Сессия завершена: «после» измерено, но снять удалось не всё — смотрите пометку откатa.")
                    : "Сессия не завершена, итог неизвестен: «после» не измерено.",
        };
    }

    /// <summary>Подпись пометки откатa для окна: без неё пользователь видел бы машинный код.</summary>
    public static string RollbackStateRu(string state) => state switch
    {
        SessionRollbackStates.EverythingReverted => "Изменения сняты полностью",
        SessionRollbackStates.PartiallyReverted => "Сняты не все изменения",
        SessionRollbackStates.NotReverted => "Изменения не сняты",
        _ => "Неизвестно (сессия без итога)",
    };
}
