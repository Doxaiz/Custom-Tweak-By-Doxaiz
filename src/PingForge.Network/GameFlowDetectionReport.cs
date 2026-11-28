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
    /// Нужно ли человеку предупреждение об анти-чите. Сетевые действия (маршрут и QoS) доступны всем:
    /// это настройки Windows, в игру они не лезут и перехвата пакетов не требуют.
    ///
    /// Раньше здесь стояло «CanOptimize = false» для ядерного анти-чита, и окно честно повторяло:
    /// «автоматические действия выключены, только пояснение» — при том, что маршрут и QoS для такой игры
    /// как раз применяются. Одно имя обещало не то, что делает код.
    /// </summary>
    public bool NeedsAntiCheatWarning => Flow.AntiCheat != Core.Models.AntiCheatStatus.VerifiedSafe;
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
    int Score = 0,
    bool IsAmbiguousProcess = false);

/// <summary>Итог одного поиска игры: что нашли, что не смогли прочитать и что из этого следует.</summary>
public sealed record GameFlowDetectionReport(
    IReadOnlyList<DetectedGameFlow> Games,
    IReadOnlyList<UnknownGameCandidate> UnknownCandidates,
    IReadOnlyList<string> NotesRu,
    int RowsRead,
    int PublicFlowRows,
    int UnreadProcesses,
    int Ipv6FlowRows = 0)
{
    /// <summary>Потоки в порядке «сначала наиболее игровые» — то, что нужно кнопке оптимизации.</summary>
    public IReadOnlyList<GameFlowInfo> Flows => Games.Select(g => g.Flow).ToList();

    public bool HasGames => Games.Count > 0;

    public DetectedGameFlow? BestGame => Games.Count > 0 ? Games[0] : null;

    /// <summary>
    /// Игры с ядерным анти-читом (Vanguard, Ricochet, EAC). Сетевые действия для них те же — маршрут и QoS,
    /// настройки Windows; в интерфейсе это отдельная строка, чтобы человек видел, что анти-чит работает в ядре.
    /// </summary>
    public IReadOnlyList<DetectedGameFlow> KernelProtectedGames =>
        Games.Where(g => g.Flow.AntiCheat == Core.Models.AntiCheatStatus.KnownKernelConflict).ToList();

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
        string reason = best.Flow.AntiCheat switch
        {
            Core.Models.AntiCheatStatus.VerifiedSafe =>
                "маршрут и QoS разрешены, совместимость проверена",
            Core.Models.AntiCheatStatus.KnownKernelConflict =>
                "ядерный анти-чит: маршрут и QoS разрешены, перехвата пакетов в программе нет",
            _ =>
                "анти-чит не проверялся: маршрут и QoS разрешены, перехвата пакетов в программе нет",
        };

        return $"Нашли: {best.GameTitleRu} — {best.EndpointText} ({best.Flow.Transport}), {reason}.";
    }
}
