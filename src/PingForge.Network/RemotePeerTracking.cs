namespace PingForge.Network;

/// <summary>
/// Удалённый собеседник процесса: с кем именно он сейчас говорит. Для TCP это видно в таблице соединений,
/// а для UDP — нет: таблица UDP знает только локальный сокет, удалённая сторона в ней не хранится.
///
/// Поэтому адрес игрового сервера для UDP-игр (а это большинство сетевых шутеров) берётся из отдельного
/// источника — трассировки сетевого стека (ETW, провайдер Microsoft-Windows-Kernel-Network) или из
/// перехвата пакетов. Ни то, ни другое не лезет в память игры: это наблюдение за сетевыми событиями
/// ядра, ровно как это делает «Монитор ресурсов».
/// </summary>
public sealed record TrackedRemotePeer(
    int ProcessId,
    string RemoteAddress,
    int RemotePort,
    ConnectionProtocol Protocol,
    DateTime ObservedUtc);

/// <summary>
/// Источник удалённых собеседников процесса. Реализация под Windows — на ETW; в тестах — заранее
/// заданный список. Порт отдельный, чтобы детектор игр не зависел от способа получения данных.
/// </summary>
public interface IRemotePeerTracker
{
    Task<IReadOnlyList<TrackedRemotePeer>> FindActivePeersAsync(CancellationToken ct = default);
}

/// <summary>
/// Сводит таблицу соединений и найденных собеседников в один список строк. Нужен потому, что у UDP
/// в таблице нет удалённой стороны, и без сведения игровой поток просто не нашёлся бы.
/// </summary>
public static class ConnectionRowMerger
{
    /// <param name="rows">Строки таблицы соединений (могут содержать строки без удалённой стороны).</param>
    /// <param name="peers">Собеседники из трассировки.</param>
    /// <param name="maxAge">Насколько старая запись ещё считается живой: остывший адрес — это уже не поток.</param>
    /// <param name="nowUtc">Текущее время (передаётся параметром, чтобы результат был воспроизводим в тестах).</param>
    public static IReadOnlyList<RawConnectionRow> Merge(
        IReadOnlyList<RawConnectionRow> rows,
        IReadOnlyList<TrackedRemotePeer> peers,
        TimeSpan maxAge,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(peers);

        var result = new List<RawConnectionRow>(rows);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RawConnectionRow row in rows)
        {
            if (row.RemotePort > 0 && row.RemoteAddress.Length > 0)
            {
                seen.Add(Key(row.ProcessId, row.RemoteAddress, row.RemotePort));
            }
        }

        foreach (TrackedRemotePeer peer in peers)
        {
            if (peer.RemotePort <= 0 || string.IsNullOrWhiteSpace(peer.RemoteAddress))
            {
                continue;
            }

            if (nowUtc - peer.ObservedUtc > maxAge)
            {
                continue; // собеседник «остыл»: показывать его как текущий поток было бы враньём
            }

            if (!seen.Add(Key(peer.ProcessId, peer.RemoteAddress, peer.RemotePort)))
            {
                continue;
            }

            // Локальная сторона неизвестна: таблица UDP её знает, но сопоставить с конкретным собеседником
            // нельзя. Пустое поле честнее выдуманного адреса.
            result.Add(new RawConnectionRow(
                peer.Protocol,
                peer.ProcessId,
                "0.0.0.0",
                0,
                peer.RemoteAddress,
                peer.RemotePort,
                peer.Protocol == ConnectionProtocol.Tcp ? "Established" : string.Empty));
        }

        return result;
    }

    private static string Key(int processId, string address, int port) => $"{processId}|{address}:{port}";
}
