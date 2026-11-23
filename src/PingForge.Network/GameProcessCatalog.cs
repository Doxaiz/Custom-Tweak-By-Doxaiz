using PingForge.Core.Models;

namespace PingForge.Network;

/// <summary>
/// Известная игра: имя процесса, анти-чит и честный статус того, что мы о нём знаем.
/// <para>Перехвата пакетов в программе нет ни для одной игры, поэтому статус отвечает не на вопрос
/// «совместимы ли мы с фильтром», а на вопрос «проверяли ли мы совместимость своих действий».</para>
/// <para><c>PortHints</c>/<c>PortRanges</c> — порты, по которым игру можно подтвердить при ранжировании потоков
/// процесса: игровой UDP-порт часто меняется, одиночным числом его не описать.</para>
/// <para><c>RequiresPortMatch</c> — процесс общий для многих программ (javaw.exe — это и Minecraft, и любая другая
/// Java-программа). Тогда игру называем только при совпадении порта с подсказкой, иначе — честный «кандидат,
/// а не игра».</para>
/// </summary>
public sealed record KnownGame(
    string ProcessName,
    string TitleRu,
    string AntiCheatName,
    AntiCheatStatus AntiCheat,
    string NoteRu = "",
    bool IsCustom = false,
    IReadOnlyList<int>? PortHints = null,
    IReadOnlyList<(int From, int To)>? PortRanges = null,
    bool RequiresPortMatch = false)
{
    /// <summary>Совпал ли удалённый порт с портом/диапазоном этой игры.</summary>
    public bool MatchesPort(int port)
    {
        if (port <= 0)
        {
            return false;
        }

        if (PortHints is not null && PortHints.Contains(port))
        {
            return true;
        }

        if (PortRanges is not null)
        {
            foreach ((int from, int to) in PortRanges)
            {
                if (port >= from && port <= to)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Текст для человека: какие порты подтверждают именно эту игру.</summary>
    public string DescribePortsRu()
    {
        var parts = new List<string>();

        if (PortHints is { Count: > 0 })
        {
            parts.Add(string.Join(", ", PortHints));
        }

        if (PortRanges is not null)
        {
            parts.AddRange(PortRanges.Select(range => $"{range.From}–{range.To}"));
        }

        return parts.Count == 0 ? "порты не заданы" : string.Join(", ", parts);
    }
}

/// <summary>Своя игра человека в общем файле настроек: имя процесса и название, которое он дал.</summary>
public sealed record CustomGameEntry(string ProcessName, string TitleRu);

/// <summary>
/// Проверка своей игры перед сохранением. Отказ — с причиной и средством, а не «не получилось»:
/// файл должен существовать и быть .exe, название — не пустым, дубли и встроенные игры не добавляются.
/// </summary>
public static class CustomGameValidation
{
    public static bool TryCreate(
        string? executablePath,
        string? titleRu,
        Func<string, bool> fileExists,
        IEnumerable<string> existingProcessNames,
        out CustomGameEntry? entry,
        out string errorRu)
    {
        entry = null;
        errorRu = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            errorRu = "Файл игры не выбран. Нажмите «Добавить игру» и укажите .exe игры, её ярлык с рабочего стола или ссылку.";
            return false;
        }

        string path = executablePath.Trim();
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            errorRu = $"«{path}» — не .exe файл. Своей игрой можно добавить только исполняемый файл игры; ярлык на рабочем столе подойдёт — программа найдёт .exe по нему.";
            return false;
        }

        if (!fileExists(path))
        {
            errorRu = $"Файла «{path}» на диске нет. Проверьте путь: возможно, игра перемещена или удалена.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(titleRu))
        {
            errorRu = "Название игры пустое. Дайте игре название — по нему вы узнаете её в списке.";
            return false;
        }

        string processName = GameProcessCatalog.Normalize(path);
        if (GameProcessCatalog.IsIgnored(processName))
        {
            errorRu = $"«{processName}» — служебный процесс или лаунчер, а не игра: такие процессы программа игрой не называет.";
            return false;
        }

        if (existingProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            errorRu = $"Игра с процессом «{processName}» уже есть в списке. Второй раз добавлять не нужно.";
            return false;
        }

        entry = new CustomGameEntry(processName, titleRu.Trim());
        return true;
    }

    /// <summary>Своя игра как запись каталога: анти-чит не проверялся, и это сказано прямо.</summary>
    public static KnownGame ToKnownGame(CustomGameEntry entry) => new(
        entry.ProcessName,
        entry.TitleRu,
        "анти-чит не проверялся",
        AntiCheatStatus.Untested,
        "своя игра: анти-чит неизвестен",
        IsCustom: true);
}

/// <summary>
/// Список известных игр. Намеренно короткий и проверяемый: неизвестный процесс мы НЕ называем игрой
/// по угадыванию — он просто не попадает в список (см. <see cref="GameFlowDetector"/>).
///
/// Статус анти-чита — это про то, что мы знаем об анти-чите игры, а не «безопасно ли для аккаунта».
/// Маршрут и маркировка DSCP содержимое пакетов не меняют и в память игры не лезут, поэтому они
/// разрешены для всех. Перехвата пакетов в программе нет вовсе — ни WinDivert, ни своего драйвера,
/// ни для одной игры: раньше каталог обещал «не поднимать фильтр для игр с ядерным анти-читом»,
/// а это обещание того, чего не существует (см. <see cref="PingForge.Core.Models.AntiCheatNotes"/>).
/// </summary>
/// <summary>Дополнительные алиасы Фортнайта: EAC/BE-обёртки и лаунчерные варианты. Проверяется префиксом fortniteclient.</summary>
public static class FortniteAliases
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "fortniteclient-win64-shipping.exe",
        "fortniteclient-win64-shipping_be.exe",
        "fortniteclient-win64-shipping_eac.exe",
        "fortniteclient-win64-shipping-eac.exe",
        "fortnite_be.exe",
        "fortnite_eac.exe",
        "fortnite.exe",
    };
}

public sealed class GameProcessCatalog
{
    public static IReadOnlyList<KnownGame> DefaultGames { get; } = new[]
    {
        new KnownGame("fortniteclient-win64-shipping.exe", "Fortnite", "EasyAntiCheat (EAC)", AntiCheatStatus.KnownKernelConflict,
            "Игровой UDP-порт матча у Fortnite заранее неизвестен (5795–5847 и 7000–9100 в разных сезонах), " +
            "поэтому поток матча опознаётся по процессу, протоколу UDP и собеседнику из трассировки стека. " +
            "Без трассировки UDP поток матча не виден — это честное «не нашли», а не ошибка. " +
            "Оверлеи сторонних программ лучше выключить: часть анти-читов считает их подозрительными.",
            PortHints: [7777, 7778, 9000, 5222],
            PortRanges: [(5795, 5847), (7000, 9100), (10000, 10100)]),
        new KnownGame("javaw.exe", "Minecraft (Java)", "анти-чит не проверялся", AntiCheatStatus.Untested,
            "Клиент Minecraft на Java — это javaw.exe, и точно так же называется любая Java-программа. " +
            "Игру подтверждает порт соединения: 25565 (а также диапазон 25500–25600 — у серверов и прокси " +
            "бывают соседние порты). Если порт другой, программа Minecraft не называет и показывает процесс " +
            "кандидатом: угадывать игру по общему имени файла — значит направить маршрут в чужое соединение. " +
            "Запускать игру нужно лаунчером Minecraft: javaw.exe — уже запущенный клиент, сам он не стартует.",
            PortHints: [25565],
            PortRanges: [(25500, 25600)],
            RequiresPortMatch: true),
        new KnownGame("minecraft.windows.exe", "Minecraft (Bedrock)", "анти-чит не проверялся", AntiCheatStatus.Untested,
            "Bedrock из Microsoft Store — пакет UWP с именем процесса minecraft.windows.exe. Сервер общается " +
            "по UDP: 19132 (IPv4) и 19133 (IPv6). UDP-собеседник виден только при включённой трассировке " +
            "сетевого стека; без неё поток не найдётся.",
            PortHints: [19132, 19133],
            PortRanges: [(19132, 19133)]),
        new KnownGame("cs2.exe", "Counter-Strike 2", "VAC", AntiCheatStatus.Untested,
            "VAC работает в пользовательском режиме. Совместимость наших действий мы не проверяли — " +
            "так и записано: обещать проверенное без проверки нельзя.",
            PortHints: [27015],
            PortRanges: [(27015, 27050)]),
        new KnownGame("dota2.exe", "Dota 2", "VAC", AntiCheatStatus.Untested,
            PortHints: [27015],
            PortRanges: [(27015, 27030)]),
        new KnownGame("valorant-win64-shipping.exe", "VALORANT", "Vanguard (ядро)", AntiCheatStatus.KnownKernelConflict,
            "Vanguard — ядерный анти-чит: рядом с ним программа не поднимает ничего своего, а перехвата " +
            "пакетов в ней нет и для других игр. Остаются маршрут и QoS — настройки Windows. " +
            "Игровой трафик идёт по UDP 7000–8000 и 8180–8181, голос — 3478–3480 (STUN), поэтому голос " +
            "здесь не матч.",
            PortHints: [8393, 8088],
            PortRanges: [(7000, 8000), (8180, 8181)]),
        new KnownGame("valorant.exe", "VALORANT (лаунчер)", "Vanguard (ядро)", AntiCheatStatus.KnownKernelConflict,
            "Только маршрут и QoS."),
        new KnownGame("league of legends.exe", "League of Legends", "Vanguard (ядро)", AntiCheatStatus.KnownKernelConflict,
            "С 2024 года игра использует Vanguard — ядерный анти-чит: программа не поднимает рядом с ним " +
            "ничего своего, остаются маршрут и QoS (настройки Windows).",
            PortHints: [2099, 5222, 8393],
            PortRanges: [(5000, 5500)]),
        new KnownGame("r5apex.exe", "Apex Legends", "EasyAntiCheat (EAC)", AntiCheatStatus.KnownKernelConflict,
            "Порт матча у Apex не один: по логам игроков он попадает в 37000–40115 (шаг 100 и +15). " +
            "Ранжирование учитывает диапазон, а не одно число.",
            PortRanges: [(37000, 40115)]),
        new KnownGame("rustclient.exe", "Rust", "EasyAntiCheat (EAC)", AntiCheatStatus.KnownKernelConflict,
            "Матчмейкинга у Rust нет: человек сам выбирает сервер в списке, где рядом показан его пинг, — " +
            "поэтому «регион» здесь ничего не значит, путь строится до конкретного адреса. Порт сервера по " +
            "умолчанию 28015 UDP; у серверов сообществ он бывает любым, так что поток опознаётся по процессу и " +
            "UDP-собеседнику, а не по одному числу. 28016 TCP (RCON) и 28082 TCP (Rust+ для телефона) — это " +
            "порты сервера для администрирования, к задержке игры они не относятся. EAC работает в ядре: " +
            "перехвата пакетов в программе нет вовсе, остаются маршрут и QoS.",
            PortHints: [28015],
            PortRanges: [(28015, 28017)]),
        new KnownGame("rainbowsix.exe", "Rainbow Six Siege", "BattlEye", AntiCheatStatus.Untested,
            PortHints: [6015],
            PortRanges: [(13000, 14001)]),
        new KnownGame("tslgame.exe", "PUBG: Battlegrounds", "BattlEye", AntiCheatStatus.Untested,
            "Порты матча у PUBG меняются сезонно: в сообществах фиксируют 7086–7995 и 12070–12460.",
            PortHints: [7086],
            PortRanges: [(7086, 7995), (12070, 12460)]),
        new KnownGame("escapefromtarkov.exe", "Escape from Tarkov", "BattlEye", AntiCheatStatus.Untested),
        new KnownGame("cod.exe", "Call of Duty", "Ricochet (ядро)", AntiCheatStatus.KnownKernelConflict,
            "Ricochet работает в ядре: рядом с ним программа не поднимает ничего своего, остаются маршрут и QoS.",
            PortHints: [3074],
            PortRanges: [(27000, 27050)]),
        new KnownGame("modernwarfare.exe", "Call of Duty: Modern Warfare", "Ricochet (ядро)", AntiCheatStatus.KnownKernelConflict,
            PortHints: [3074],
            PortRanges: [(27000, 27050)]),
        new KnownGame("overwatch.exe", "Overwatch 2", "Защита Blizzard", AntiCheatStatus.Untested,
            "Battle.net держит TCP 1119/3724/6113 и UDP 3478–3479/5060/6250 — те же порты у голосового чата, " +
            "поэтому здесь они заданы как порты самой игры, а не как «голос».",
            PortHints: [1119, 3724, 6113, 3478, 3479, 5060, 6250]),
        new KnownGame("rocketleague.exe", "Rocket League", "EasyAntiCheat (EAC)", AntiCheatStatus.KnownKernelConflict,
            PortHints: [7000],
            PortRanges: [(7000, 9000), (27000, 27031)]),
        new KnownGame("gta5.exe", "GTA Online", "Защита Rockstar", AntiCheatStatus.Untested,
            "Часть сессий — peer-to-peer: трафик идёт напрямую другим игрокам, а не только на сервер. " +
            "Маршрут до одного адреса помогает только той части сессии, что идёт к нему.",
            PortHints: [6672],
            PortRanges: [(61455, 61458)]),
        new KnownGame("fivem_gtaprocess.exe", "FiveM (GTA V)", "Защита FiveM", AntiCheatStatus.Untested,
            PortHints: [30120]),
        new KnownGame("destiny2.exe", "Destiny 2", "BattlEye", AntiCheatStatus.Untested,
            "У игры были блокировки за VPN: релейные пути предлагаем только с вашего явного согласия.",
            PortHints: [3074, 3097]),
        new KnownGame("warframe.x64.exe", "Warframe", "Защита Digital Extremes", AntiCheatStatus.Untested,
            PortHints: [4950, 4955]),
        new KnownGame("deadbydaylight-win64-shipping.exe", "Dead by Daylight", "EasyAntiCheat (EAC)", AntiCheatStatus.KnownKernelConflict,
            "EAC работает в ядре — тот же анти-чит, что у Fortnite и Rust. Маршрут и QoS остаются доступны.")
    };

    /// <summary>
    /// Процессы, которые точно не игра: лаунчеры, оверлеи, системные службы. Если их не отсеять,
    /// «оптимизация» уйдёт в магазин или в обновление, а не в игру.
    /// </summary>
    public static IReadOnlySet<string> IgnoredProcesses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "svchost.exe", "explorer.exe", "lsass.exe", "services.exe", "csrss.exe", "wininit.exe",
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe", "battle.net.exe",
        "galaxyclient.exe", "eadesktop.exe", "origin.exe", "upc.exe", "ubisoftconnect.exe", "riotclientservices.exe",
        "discord.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "telegram.exe", "spotify.exe",
        "onedrive.exe", "dropbox.exe", "teams.exe", "zoom.exe", "obs64.exe", "msedgewebview2.exe",
        "nvidia share.exe", "nvcontainer.exe", "radeonsoftware.exe", "gameoverlayui.exe", "amdow.exe",
        "searchindexer.exe", "taskhostw.exe", "runtimebroker.exe", "sihost.exe", "dwm.exe", "ctfmon.exe"
    };

    private readonly List<KnownGame> _games;
    private readonly Dictionary<string, KnownGame> _byExe;

    public GameProcessCatalog(IEnumerable<KnownGame>? games = null)
    {
        _games = (games ?? DefaultGames).ToList();
        _byExe = new Dictionary<string, KnownGame>(StringComparer.OrdinalIgnoreCase);

        foreach (KnownGame game in _games)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(game.ProcessName);
            _byExe[Normalize(game.ProcessName)] = game;
        }
    }

    public IReadOnlyList<KnownGame> All => _games;

    /// <summary>Только свои игры человека (встроенные так не помечены и не удаляются).</summary>
    public IReadOnlyList<KnownGame> CustomGames => _games.Where(game => game.IsCustom).ToList();

    /// <summary>
    /// Каталог «встроенные + свои». Битые записи своих игр (пустое имя/название, дубли встроенных)
    /// пропускаются, а не ломают список: служба обязана работать со встроенными играми в любом случае.
    /// Исключение — встроенная запись общего процесса (<see cref="KnownGame.RequiresPortMatch"/>): своя
    /// игра её заменяет, иначе Minecraft на нестандартном порту не назвать игрой никак.
    /// </summary>
    public static GameProcessCatalog WithCustomGames(IEnumerable<CustomGameEntry>? customGames)
    {
        var games = new List<KnownGame>(DefaultGames);

        foreach (CustomGameEntry entry in customGames ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.ProcessName) || string.IsNullOrWhiteSpace(entry.TitleRu))
            {
                continue;
            }

            string processName = Normalize(entry.ProcessName);
            if (IsIgnored(processName))
            {
                continue;
            }

            KnownGame custom = CustomGameValidation.ToKnownGame(new CustomGameEntry(processName, entry.TitleRu.Trim()));
            int existing = games.FindIndex(game => string.Equals(Normalize(game.ProcessName), processName, StringComparison.OrdinalIgnoreCase));

            if (existing < 0)
            {
                games.Add(custom);
                continue;
            }

            // Общий процесс (javaw.exe) человек вправе назвать своей игрой: без этого сервер на
            // нестандартном порту не оптимизировать никак. Встроенную однозначную игру не перекрываем:
            // «cs2.exe = моя игра» — это не уточнение, а подмена.
            if (games[existing].RequiresPortMatch)
            {
                games[existing] = custom;
            }
        }

        return new GameProcessCatalog(games);
    }

    public bool TryMatch(string? processName, out KnownGame game)
    {
        game = null!;
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string norm = Normalize(processName);
        if (_byExe.TryGetValue(norm, out game!))
        {
            return true;
        }

        // Java: у клиента Minecraft процесс javaw.exe, у серверных сборок и лаунчеров — java.exe.
        // Это один и тот же общий процесс, поэтому подтверждение идёт портом (см. RequiresPortMatch).
        if (norm.Equals("java.exe", StringComparison.OrdinalIgnoreCase)
            || norm.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return _byExe.TryGetValue("javaw.exe", out game!);
        }

        // Фортнайт в творческом/лобби часто запускается обёрткой EAC/BE с суффиксом _be/_eac: ловим префиксом.
        if (norm.StartsWith("fortniteclient", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("fortnite_", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("fortnite.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Всегда есть базовый Фортнайт в каталоге
            return _byExe.TryGetValue("fortniteclient-win64-shipping.exe", out game!);
        }

        return false;
    }

    public static bool IsIgnored(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && IgnoredProcesses.Contains(processName.Trim());

    /// <summary>Имя процесса в том виде, в котором оно приходит из системы: без пути, в нижнем регистре.</summary>
    public static string Normalize(string processName)
    {
        string name = processName.Trim();
        int slash = name.LastIndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.ToLowerInvariant();
    }

    public string DescribeSummaryRu() =>
        $"В списке известных игр: {_games.Count}. Если вашей игры нет — приложение не угадает её по имени, " +
        "а покажет процесс как «не из списка» и предложит добавить вручную.";
}
