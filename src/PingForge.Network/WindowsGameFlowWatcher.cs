using PingForge.Orchestration.Realtime;

namespace PingForge.Network;

/// <summary>
/// Ищет запущенные игры: читает таблицу соединений, при необходимости дополняет её собеседниками из
/// трассировки (для UDP удалённая сторона в таблице не хранится), подтягивает имена процессов и отдаёт
/// то, что нашёл детектор. Кода под Windows здесь нет — только логика поверх портов, поэтому поведение
/// проверяется автотестами.
/// </summary>
public sealed class WindowsGameFlowWatcher : IGameFlowWatcher
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
