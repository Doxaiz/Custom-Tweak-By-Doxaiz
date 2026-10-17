using PingForge.Orchestration.Realtime;

namespace PingForge.Network;

/// <summary>
/// Найденный живой поток игры: то, что пойдёт в кнопку оптимизации, плюс объяснение выбора.
/// Объяснение здесь не «для красоты»: пользователь должен видеть, почему программа решила,
/// что играет именно этот поток, и на каком основании она называет процесс игрой.
/// </summary>
public sealed record DetectedGameFlow(
    GameFlowInfo Flow,
    int Score,
    int CandidateCount,
    string ReasonRu)
{
    public string GameTitleRu => Flow.GameTitleRu;

    public string EndpointText => $"{Flow.RemoteEndpoint}:{Flow.RemotePort}";

    /// <summary>
    /// Можно ли трогать сеть для этого потока. Ядерный анти-чит — стоп: маршрут и DSCP мы не применяем
    /// молча туда, где за это можно получить блокировку аккаунта.
    /// </summary>
    public bool CanOptimize => Flow.AntiCheat != Core.Models.AntiCheatStatus.KnownKernelConflict;
}

/// <summary>
/// Процесс с публичными соединениями, которого нет в списке известных игр.
/// Название по имени файла не угадывается: список можно дополнить вручную.
/// </summary>
public sealed record UnknownGameCandidate(
    int ProcessId,
    string ProcessName,
    int CandidateCount,
    string EndpointText,
    string ReasonRu,
    int Score = 0);

/// <summary>Итог одного поиска игры: что нашли, что не смогли прочитать и что из этого следует.</summary>
public sealed record GameFlowDetectionReport(
    IReadOnlyList<DetectedGameFlow> Games,
    IReadOnlyList<UnknownGameCandidate> UnknownCandidates,
    IReadOnlyList<string> NotesRu,
    int RowsRead,
    int PublicFlowRows,
    int UnreadProcesses)
{
    /// <summary>Потоки в порядке «сначала наиболее игровые» — то, что нужно кнопке оптимизации.</summary>
    public IReadOnlyList<GameFlowInfo> Flows => Games.Select(g => g.Flow).ToList();

    public bool HasGames => Games.Count > 0;

    public DetectedGameFlow? BestGame => Games.Count > 0 ? Games[0] : null;

    /// <summary>
    /// Игры с ядерным анти-читом (Vanguard, Ricochet). Для них программа не поднимает фильтр и не применяет
    /// сетевые действия без подтверждения — в интерфейсе это отдельная строка.
    /// </summary>
    public IReadOnlyList<DetectedGameFlow> KernelProtectedGames =>
        Games.Where(g => !g.CanOptimize).ToList();

    public bool HasKernelProtectedGame => KernelProtectedGames.Count > 0;

    /// <summary>Короткая строка для интерфейса: «нашли то-то» либо честное «не нашли».</summary>
    public string DescribeSummaryRu()
    {
        if (Games.Count == 0)
        {
            return UnknownCandidates.Count > 0
                ? $"Игр из списка не найдено. Есть {UnknownCandidates.Count} похожих процессов — прочитайте их отдельно."
                : "Запущенных игр не найдено.";
        }

        DetectedGameFlow best = Games[0];
        string reason = best.CanOptimize
            ? "сетевые действия разрешены"
            : "ядерный анти-чит: автоматические действия выключены, только пояснение";

        return $"Нашли: {best.GameTitleRu} — {best.EndpointText} ({best.Flow.Transport}), {reason}.";
    }
}
