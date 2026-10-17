using System.Net;
using System.Net.Sockets;
using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;

namespace PingForge.Network;

/// <summary>
/// Отбирает игровые потоки из таблицы соединений. Логика намеренно простая и объяснимая:
/// локальные адреса отбрасываем, из оставшихся строк одного процесса выбираем наиболее «игровую»,
/// а процесс, которого нет в списке известных игр, игрой НЕ называем — показываем его как кандидата.
/// </summary>
public sealed class GameFlowDetector
{
    /// <summary>
    /// Порты, на которых игровые серверы встречаются чаще всего. Используются только для ранжирования:
    /// играть «на 443» никто не запрещает, просто такой поток менее типичен.
    /// </summary>
    private static readonly int[] GameLikePorts =
    {
        3074, 3075, 3478, 3479, 3480, 8766, 9339, 19132, 27015, 27016, 27017, 27018, 27019, 27020,
        27031, 27036, 28960, 3724, 1119
    };

    private static readonly int[] WebPorts = { 80, 443 };

    /// <summary>Служебные порты: DNS, NTP, LLMNR/mDNS. Это точно не игровой сервер.</summary>
    private static readonly int[] ServicePorts = { 53, 123, 5353, 5355, 67, 68 };

    private readonly GameProcessCatalog _catalog;
    private readonly bool _includeUnknownProcesses;

    public GameFlowDetector(GameProcessCatalog? catalog = null, bool includeUnknownProcesses = false)
    {
        _catalog = catalog ?? new GameProcessCatalog();
        _includeUnknownProcesses = includeUnknownProcesses;
    }

    public GameFlowDetectionReport Detect(
        IReadOnlyList<RawConnectionRow> rows,
        IReadOnlyDictionary<int, string> processNames)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(processNames);

        var games = new List<DetectedGameFlow>();
        var unknown = new List<UnknownGameCandidate>();
        var notes = new List<string>();

        int publicRows = 0;
        int unreadProcesses = 0;

        foreach (IGrouping<int, RawConnectionRow> group in rows.Where(r => r.ProcessId > 4).GroupBy(r => r.ProcessId))
        {
            if (!processNames.TryGetValue(group.Key, out string? rawName) || string.IsNullOrWhiteSpace(rawName))
            {
                unreadProcesses++;
                continue;
            }

            string exe = GameProcessCatalog.Normalize(rawName);
            if (GameProcessCatalog.IsIgnored(exe))
            {
                continue;
            }

            List<RawConnectionRow> candidates = group
                .Where(r => r.RemotePort > 0 && IsPublicRemoteAddress(r.RemoteAddress))
                .ToList();

            publicRows += candidates.Count;
            if (candidates.Count == 0)
            {
                continue;
            }

            RawConnectionRow best = candidates
                .OrderByDescending(Score)
                .ThenBy(r => r.RemotePort)
                .First();

            string transport = best.Protocol == ConnectionProtocol.Udp ? "UDP" : "TCP";
            int score = Score(best);

            if (_catalog.TryMatch(exe, out KnownGame game))
            {
                games.Add(new DetectedGameFlow(
                    new GameFlowInfo(group.Key, exe, game.TitleRu, best.RemoteAddress, best.RemotePort, transport, game.AntiCheat),
                    score,
                    candidates.Count,
                    DescribeChoice(best, transport, candidates.Count, $"анти-чит: {game.AntiCheatName}")));
                continue;
            }

            string endpointText = $"{best.RemoteAddress}:{best.RemotePort}";

            if (_includeUnknownProcesses)
            {
                games.Add(new DetectedGameFlow(
                    new GameFlowInfo(group.Key, exe, $"{exe} (не из списка известных игр)",
                        best.RemoteAddress, best.RemotePort, transport, AntiCheatStatus.Untested),
                    score,
                    candidates.Count,
                    DescribeChoice(best, transport, candidates.Count, "процесс не из списка: добавлен вами вручную")));
            }
            else
            {
                // В кандидата кладём имя в том виде, как его отдаёт система: человек ищет процесс
                // в диспетчере задач по настоящему имени, а не по приведённому к нижнему регистру.
                unknown.Add(new UnknownGameCandidate(
                    group.Key,
                    rawName.Trim(),
                    candidates.Count,
                    endpointText,
                    "Процесса нет в списке известных игр — название по имени файла не угадываем.",
                    score));
            }
        }

        notes.Add($"Соединений прочитано: {rows.Count}; на публичные адреса: {publicRows}. " +
                  "Локальные, петлевые и служебные адреса отброшены: у игрового сервера адрес публичный.");

        int peerlessUdpSockets = rows.Count(r => r.Protocol == ConnectionProtocol.Udp && r.RemotePort == 0);
        if (peerlessUdpSockets > 0)
        {
            notes.Add($"UDP-сокетов без известного собеседника: {peerlessUdpSockets}. Таблица соединений Windows " +
                      "хранит для UDP только локальный сокет — адрес сервера берётся из трассировки сетевого стека. " +
                      "Если игра общается только по UDP, а трассировка выключена, поток найден не будет: это честное " +
                      "«не нашли», а не ошибка.");
        }

        if (unreadProcesses > 0)
        {
            notes.Add($"У {unreadProcesses} процессов имя прочитать не удалось — они пропущены, а не подписаны «неизвестно.exe».");
        }

        if (unknown.Count > 0)
        {
            notes.Add($"Похожих на игру процессов не из списка: {unknown.Count}. Они показаны отдельно: " +
                      "если это ваша игра, добавьте её вручную — угадывать имя файла программа не станет.");
        }

        if (games.Count == 0)
        {
            notes.Add("Запущенных игр из списка не найдено. Оптимизировать «сеть вообще» программа не станет: " +
                      "у каждой игры свой сервер и свой маршрут.");
        }

        return new GameFlowDetectionReport(
            Games: games.OrderByDescending(g => g.Score).ToList(),
            // Порядок — «сначала самые игровые»: интерфейс называет первые, и человек должен
            // увидеть в первую очередь своё соединение на игровой порт, а не службу Windows с портом 32526.
            UnknownCandidates: unknown.OrderByDescending(u => u.Score).ThenBy(u => u.ProcessId).ToList(),
            NotesRu: notes,
            RowsRead: rows.Count,
            PublicFlowRows: publicRows,
            UnreadProcesses: unreadProcesses);
    }

    /// <summary>Ранжирование потока одного и того же процесса: UDP и «не-веб» порт считаем более игровыми.</summary>
    internal static int Score(RawConnectionRow row)
    {
        int score = 0;

        if (row.Protocol == ConnectionProtocol.Udp)
        {
            score += 6;
        }

        if (!WebPorts.Contains(row.RemotePort))
        {
            score += 3;
        }

        // Известный игровой порт — сильный признак: он перевешивает «просто нестандартный порт»
        // и должен перевешивать даже UDP-DNS, иначе программа назовёт игрой службу разрешения имён.
        if (GameLikePorts.Contains(row.RemotePort))
        {
            score += 6;
        }

        if (ServicePorts.Contains(row.RemotePort))
        {
            score -= 4;
        }

        if (row.Protocol == ConnectionProtocol.Tcp
            && row.TcpState.Equals("Established", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    private static string DescribeChoice(RawConnectionRow row, string transport, int candidateCount, string tail) =>
        $"{transport}-поток к {row.RemoteAddress}:{row.RemotePort} — из {candidateCount} публичных потоков этого процесса " +
        $"он выбран как наиболее игровой (UDP и нестандартные порты приоритетнее веб-портов); {tail}.";

    /// <summary>
    /// Публичный ли адрес у сервера. Всё приватное и служебное — не игровой сервер: трафик внутри
    /// домашней сети оптимизировать нечего.
    /// </summary>
    public static bool IsPublicRemoteAddress(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
            {
                return false;
            }

            byte[] v6 = ip.GetAddressBytes();
            bool uniqueLocal = (v6[0] & 0xFE) == 0xFC;
            return !uniqueLocal;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();

        if (b[0] == 0 || b[0] == 127 || b[0] >= 224)
        {
            return false;
        }

        if (b[0] == 10)
        {
            return false;
        }

        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        {
            return false;
        }

        if (b[0] == 192 && b[1] == 168)
        {
            return false;
        }

        if (b[0] == 169 && b[1] == 254)
        {
            return false;
        }

        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
        {
            return false; // операторский NAT: это адрес провайдера, а не игрового сервера
        }

        if (b[0] == 192 && b[1] == 0 && b[2] == 2)
        {
            return false; // тестовые диапазоны документации
        }

        return true;
    }
}
