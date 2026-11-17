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
        3074, 3075, 3478, 3479, 3480, 8766, 9339, 19132, 19133, 27015, 27016, 27017, 27018, 27019, 27020,
        27031, 27036, 28960, 3724, 1119,
        // Fortnite / Unreal (творческий режим и матчи)
        9000, 9001, 9002, 9003, 9004, 9005, 9006, 9007, 9008, 9009, 9010, 10000, 10001, 7777, 7778, 8000, 5222,
        // Minecraft: Java — TCP 25565 (query там же), Bedrock — UDP 19132/19133.
        // Диапазон 25500–25600 целиком сюда не идёт: соседние порты проверяются по подсказке самой игры.
        25565, 25566, 25567, 25568,
        // Одиночные игровые порты, которые ни с чем не путаются (широкие диапазоны живут в каталоге игр).
        2099, 28015, 30120, 6015, 6672, 4950, 4955
    };

    /// <summary>
    /// Надбавка за совпадение с портом самой игры: у общего процесса (javaw.exe) это единственное
    /// доказательство, а у Fortnite — способ выбрать поток матча, а не голосовой чат на 3478.
    /// </summary>
    private const int GamePortBonus = 8;

    private static readonly int[] WebPorts = { 80, 443 };

    /// <summary>
    /// Порты голосового чата и служебных медиа-сессий (STUN/TURN, SIP). У Fortnite это 3478–3479,
    /// и без поправки голосовой поток выигрывал у потока матча: у игры он тоже «нестандартный UDP».
    /// </summary>
    private static readonly int[] VoicePorts = { 3478, 3479, 3480, 5004, 5005, 5060, 5062 };

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
        int ipv6Dropped = 0;

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

            List<RawConnectionRow> publicCandidates = group
                .Where(r => r.RemotePort > 0 && IsPublicRemoteAddress(r.RemoteAddress))
                .ToList();

            // IPv6 видно, но маршрут ставится по IPv4 (route add для IPv6 в продукте нет). Такой поток
            // не отбрасываем молча: считаем и говорим об этом в примечании, иначе человек читает
            // «игр не найдено» и не понимает, что его сервер как раз IPv6-ный.
            int ipv6Rows = publicCandidates.Count(r => IsIpv6Remote(r));
            List<RawConnectionRow> candidates = publicCandidates.Where(r => !IsIpv6Remote(r)).ToList();
            ipv6Dropped += ipv6Rows;

            publicRows += candidates.Count;
            if (candidates.Count == 0)
            {
                continue;
            }

            // Игру ищем до ранжирования: порты самой игры — самый сильный признак, и учитывать их
            // можно только зная игру. Для Fortnite это отделяет поток матча от голосового чата на 3478,
            // для Minecraft — подтверждает или опровергает догадку по общему имени javaw.exe.
            KnownGame? game = _catalog.TryMatch(exe, out KnownGame matchedGame) ? matchedGame : null;

            RawConnectionRow best = candidates
                .OrderByDescending(r => ScoreForGame(r, game))
                .ThenBy(r => r.RemotePort)
                .First();

            string transport = best.Protocol == ConnectionProtocol.Udp ? "UDP" : "TCP";
            int score = ScoreForGame(best, game);

            // Общий процесс (javaw.exe/java.exe) называется игрой только при совпадении порта.
            bool portConfirmed = game is null || !game.RequiresPortMatch || game.MatchesPort(best.RemotePort);

            if (game is not null && portConfirmed)
            {
                string tail = $"анти-чит: {game.AntiCheatName}";
                if (game.MatchesPort(best.RemotePort))
                {
                    tail = $"{tail}; порт {best.RemotePort} совпал с портом игры";
                }

                games.Add(new DetectedGameFlow(
                    new GameFlowInfo(group.Key, exe, game.TitleRu, best.RemoteAddress, best.RemotePort, transport, game.AntiCheat),
                    score,
                    candidates.Count,
                    DescribeChoice(best, transport, candidates.Count, tail)));
                continue;
            }

            string endpointText = $"{best.RemoteAddress}:{best.RemotePort}";

            // Порт не подтвердил догадку у общего процесса: говорим об этом прямо, а не называем игру наугад.
            string reasonRu = game is null
                ? "Процесса нет в списке известных игр — название по имени файла не угадываем."
                : $"Процесс «{exe}» общий для многих программ: игру «{game.TitleRu}» подтверждает порт " +
                  $"({game.DescribePortsRu()}), а здесь соединение на порт {best.RemotePort} в это не попадает. " +
                  "Название по имени файла не угадываем — иначе маршрут ушёл бы в чужое соединение.";

            if (_includeUnknownProcesses)
            {
                string title = game is null
                    ? $"{exe} (не из списка известных игр)"
                    : $"{game.TitleRu} (порт не подтверждён)";

                games.Add(new DetectedGameFlow(
                    new GameFlowInfo(group.Key, exe, title, best.RemoteAddress, best.RemotePort, transport, AntiCheatStatus.Untested),
                    score,
                    candidates.Count,
                    DescribeChoice(best, transport, candidates.Count,
                        game is null ? "процесс не из списка: добавлен вами вручную" : "порт игру не подтвердил: решение за вами")));
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
                    reasonRu,
                    score,
                    IsAmbiguousProcess: game is not null));
            }
        }

        notes.Add($"Соединений прочитано: {rows.Count}; на публичные адреса: {publicRows}. " +
                  "Локальные, петлевые и служебные адреса отброшены: у игрового сервера адрес публичный.");

        if (ipv6Dropped > 0)
        {
            notes.Add($"IPv6-соединений с публичными адресами: {ipv6Dropped}. Они видны, но маршрут ставится только " +
                      "по IPv4: для IPv6-сервера программа честно ничего не меняет. Если игра умеет ходить по IPv4 " +
                      "(у Minecraft Java это обычный случай), она найдётся по IPv4-потоку.");
        }

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
            notes.Add($"Похожих на игру процессов, которые игрой не названы: {unknown.Count}. Это либо процесс не из " +
                      "списка (его можно добавить вручную), либо общий процесс без подтверждения портом: угадывать имя " +
                      "файла программа не станет, нужен поток на порт самой игры.");
        }

        // Fallback: игра запущена, но публичного потока нет (лобби/творческий, ETW ещё не догнал, UDP 0.0.0.0:0).
        // Тогда считаем её запущенной в лобби: кнопка скажет “игра уже запущена, зайдите в матч для замера”, а не “не найдена”.
        var lobbyGames = new List<DetectedGameFlow>();
        var ambiguousLobby = new List<KnownGame>();
        if (games.Count == 0)
        {
            foreach (IGrouping<int, RawConnectionRow> group in rows.Where(r => r.ProcessId > 4).GroupBy(r => r.ProcessId))
            {
                if (!processNames.TryGetValue(group.Key, out string? rawName) || string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                string exe = GameProcessCatalog.Normalize(rawName);
                if (!_catalog.TryMatch(exe, out KnownGame lobbyGame))
                {
                    continue;
                }

                // «Лобби» у общего процесса — это догадка без единого признака: под javaw.exe идёт и IDE,
                // и сервер мода. Такой процесс не называем игрой даже в лобби.
                if (lobbyGame.RequiresPortMatch)
                {
                    ambiguousLobby.Add(lobbyGame);
                    continue;
                }

                // Поток на публичный IPv6-адрес — это не лобби: игра в сети, просто путь по IPv6 мы не меняем.
                if (group.Any(r => r.RemotePort > 0 && IsIpv6Remote(r) && IsPublicRemoteAddress(r.RemoteAddress)))
                {
                    continue;
                }

                // Есть хоть какие-то строки этого PID (даже с RemotePort=0) — процесс живой
                int anyRows = group.Count();
                if (anyRows > 0)
                {
                    lobbyGames.Add(new DetectedGameFlow(
                        new GameFlowInfo(group.Key, exe, lobbyGame.TitleRu, "—", 0, "ожидание", lobbyGame.AntiCheat),
                        -1,
                        anyRows,
                        "игра запущена, но публичного адреса сервера пока нет — вы в лобби/творческом режиме. Зайдите в матч и нажмите снова: маршрут применится только к адресу сервера матча."));
                }
            }

            if (lobbyGames.Count > 0)
            {
                notes.Add($"Игра найдена в лобби: {string.Join(", ", lobbyGames.Select(g => g.Flow.GameTitleRu))} — процесс запущен, публичного потока матча ещё нет. Это нормально для лобби/творческого режима.");
                games.AddRange(lobbyGames);
            }
            else
            {
                notes.Add("Запущенных игр из списка не найдено. Оптимизировать «сеть вообще» программа не станет: " +
                          "у каждой игры свой сервер и свой маршрут.");
            }

            if (ambiguousLobby.Count > 0)
            {
                notes.Add($"Java-процессы без подтверждённого порта: {string.Join(", ", ambiguousLobby.Select(g => g.ProcessName).Distinct())}. " +
                          "Имя javaw.exe/java.exe общее, поэтому «игра в лобби» здесь не утверждается: нужен публичный " +
                          $"поток на порт игры ({ambiguousLobby[0].DescribePortsRu()}).");
            }
        }

        return new GameFlowDetectionReport(
            Games: games.OrderByDescending(g => g.Score).ToList(),
            // Порядок — «сначала самые игровые»: интерфейс называет первые, и человек должен
            // увидеть в первую очередь своё соединение на игровой порт, а не службу Windows с портом 32526.
            UnknownCandidates: unknown.OrderByDescending(u => u.Score).ThenBy(u => u.ProcessId).ToList(),
            NotesRu: notes,
            RowsRead: rows.Count,
            PublicFlowRows: publicRows,
            UnreadProcesses: unreadProcesses,
            Ipv6FlowRows: ipv6Dropped);
    }

    /// <summary>
    /// Счёт потока с поправкой на порты самой игры. Нужен там, где общий признак «нестандартный порт»
    /// обманывает: у Fortnite в одном процессе и матч (UDP 5795–5847/7000–9100), и голосовой чат (UDP 3478).
    /// </summary>
    internal static int ScoreForGame(RawConnectionRow row, KnownGame? game)
    {
        int score = Score(row);

        if (game is null)
        {
            return score;
        }

        if (game.MatchesPort(row.RemotePort))
        {
            return score + GamePortBonus;
        }

        // Порты игры известны, а этот поток в них не попал: голосовой чат того же процесса — не матч.
        // Без этой поправки UDP-голос (3478) обгонял матч на неизвестном заранее высоком порту.
        if ((game.PortHints is not null || game.PortRanges is not null) && VoicePorts.Contains(row.RemotePort))
        {
            score -= GamePortBonus;
        }

        return score;
    }

    /// <summary>
    /// IPv6-собеседник. Продукт ставит маршрут только командой route для IPv4, поэтому такие потоки
    /// считаются (чтобы объяснить человеку, что происходит), но в оптимизацию не идут.
    /// </summary>
    internal static bool IsIpv6Remote(RawConnectionRow row) =>
        IPAddress.TryParse(row.RemoteAddress, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

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
        $"он выбран как наиболее игровой (UDP, порты самой игры и нестандартные порты приоритетнее веб-портов); {tail}.";

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
