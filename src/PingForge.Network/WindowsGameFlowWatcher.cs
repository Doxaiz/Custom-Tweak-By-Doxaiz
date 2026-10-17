using PingForge.Orchestration.Realtime;

namespace PingForge.Network;

/// <summary>
/// Ищет запущенные игры: читает таблицу соединений, при необходимости дополняет её собеседниками из
/// трассировки (для UDP удалённая сторона в таблице не хранится), подтягивает имена процессов и отдаёт
/// то, что нашёл детектор. Кода под Windows здесь нет — только логика поверх портов, поэтому поведение
/// проверяется автотестами.
/// </summary>
public sealed class WindowsGameFlowWatcher : IGameFlowReporter
{
    private readonly IRawConnectionTableProvider _table;
    private readonly IProcessNameResolver _processNames;
    private readonly IRemotePeerTracker? _peerTracker;
    private readonly GameFlowDetector _detector;
    private readonly TimeSpan _peerMaxAge;
    private readonly Func<DateTime> _clock;

    public WindowsGameFlowWatcher(
        IRawConnectionTableProvider table,
        IProcessNameResolver processNames,
        GameProcessCatalog? catalog = null,
        bool includeUnknownProcesses = false,
        IRemotePeerTracker? peerTracker = null,
        TimeSpan? peerMaxAge = null,
        Func<DateTime>? clock = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _processNames = processNames ?? throw new ArgumentNullException(nameof(processNames));
        _peerTracker = peerTracker;
        _peerMaxAge = peerMaxAge ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTime.UtcNow);
        _detector = new GameFlowDetector(catalog, includeUnknownProcesses);
    }

    /// <summary>Полный отчёт: нужен интерфейсу, чтобы показать и «не из списка» тоже.</summary>
    public async Task<GameFlowDetectionReport> DetectAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RawConnectionRow> rows = await ReadRowsAsync(ct).ConfigureAwait(false);

        var names = new Dictionary<int, string>();
        foreach (int pid in rows.Select(r => r.ProcessId).Where(p => p > 4).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            string? name = await _processNames.TryGetProcessNameAsync(pid, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[pid] = name!;
            }
        }

        return _detector.Detect(rows, names);
    }

    public async Task<IReadOnlyList<GameFlowInfo>> FindActiveGameFlowsAsync(CancellationToken ct = default)
    {
        GameFlowDetectionReport report = await DetectAsync(ct).ConfigureAwait(false);
        return report.Flows;
    }

    public async Task<(IReadOnlyList<GameFlowInfo> Flows, string? NoGameReasonRu)> FindGameFlowsWithReasonAsync(
        CancellationToken ct = default)
    {
        GameFlowDetectionReport report = await DetectAsync(ct).ConfigureAwait(false);
        return (report.Flows, report.HasGames ? null : DescribeNoGameRu(report));
    }

    /// <summary>
    /// Почему игры нет — по-человечески и без догадок. Если в таблице есть публичные соединения
    /// процессов, которых нет в каталоге, так и говорим: они похожи на игру, но назвать их игрой
    /// программа не имеет права. Если таблицу прочитать не удалось — тоже говорим прямо.
    /// </summary>
    internal static string DescribeNoGameRu(GameFlowDetectionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.UnknownCandidates.Count > 0)
        {
            UnknownGameCandidate first = report.UnknownCandidates[0];
            string more = report.UnknownCandidates.Count > 1
                ? $" Всего таких процессов: {report.UnknownCandidates.Count}, лучший выбран по порту и типу соединения."
                : string.Empty;

            return $"Похожее на игру соединение есть, но оптимизировать его нельзя: процесс «{first.ProcessName}» " +
                $"({first.EndpointText}) не входит в список известных игр, а название по имени файла программа " +
                $"не угадывает.{more} " +
                "Если это ваша игра — добавьте её в список известных игр, и кнопка возьмёт именно этот поток.";
        }

        if (report.RowsRead == 0)
        {
            return "Таблица соединений пуста: возможно, нет прав на её чтение или сеть сейчас не используется.";
        }

        return $"Публичных соединений не найдено: прочитано строк — {report.RowsRead}, " +
            $"с публичными адресами — {report.PublicFlowRows}. Зайдите в матч и нажмите кнопку снова.";
    }

    private async Task<IReadOnlyList<RawConnectionRow>> ReadRowsAsync(CancellationToken ct)
    {
        IReadOnlyList<RawConnectionRow> rows = await _table.ReadAsync(ct).ConfigureAwait(false);

        if (_peerTracker is null)
        {
            return rows;
        }

        IReadOnlyList<TrackedRemotePeer> peers = await _peerTracker.FindActivePeersAsync(ct).ConfigureAwait(false);
        return ConnectionRowMerger.Merge(rows, peers, _peerMaxAge, _clock());
    }
}
