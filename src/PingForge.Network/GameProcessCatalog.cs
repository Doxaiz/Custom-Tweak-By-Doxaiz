using PingForge.Core.Models;

namespace PingForge.Network;

/// <summary>Известная игра: имя процесса, анти-чит и честный статус его совместимости с сетевым фильтром.</summary>
public sealed record KnownGame(
    string ProcessName,
    string TitleRu,
    string AntiCheatName,
    AntiCheatStatus AntiCheat,
    string NoteRu = "",
    bool IsCustom = false);

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
            errorRu = "Файл игры не выбран. Нажмите «Добавить игру» и укажите .exe файл игры.";
            return false;
        }

        string path = executablePath.Trim();
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            errorRu = $"«{path}» — не .exe файл. Своей игрой можно добавить только исполняемый файл игры.";
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
/// Статус анти-чита — это про совместимость с нашим сетевым фильтром, а не «безопасно ли для аккаунта».
/// Маршрут и маркировка DSCP содержимое пакетов не меняют и в память игры не лезут, поэтому они
/// разрешены для всех; фильтр пакетов (WinDivert) не поднимается для игр с ядерным анти-читом,
/// и это подтверждено тестом каталога.
/// </summary>
public sealed class GameProcessCatalog
{
    public static IReadOnlyList<KnownGame> DefaultGames { get; } = new[]
    {
        new KnownGame("fortniteclient-win64-shipping.exe", "Fortnite", "EasyAntiCheat (EAC)", AntiCheatStatus.Untested,
            "Оверлеи сторонних программ лучше выключить: часть анти-читов считает их подозрительными."),
        new KnownGame("cs2.exe", "Counter-Strike 2", "VAC", AntiCheatStatus.Untested,
            "VAC работает в пользовательском режиме, но совместимость фильтра мы не проверяли."),
        new KnownGame("dota2.exe", "Dota 2", "VAC", AntiCheatStatus.Untested),
        new KnownGame("valorant-win64-shipping.exe", "VALORANT", "Vanguard (ядро)", AntiCheatStatus.KnownKernelConflict,
            "Vanguard — ядерный анти-чит: фильтр пакетов не поднимаем, применяем только маршрут и QoS."),
        new KnownGame("valorant.exe", "VALORANT (лаунчер)", "Vanguard (ядро)", AntiCheatStatus.KnownKernelConflict,
            "Только маршрут и QoS."),
        new KnownGame("league of legends.exe", "League of Legends", "Vanguard (ядро)", AntiCheatStatus.KnownKernelConflict,
            "С 2024 года игра использует Vanguard: только маршрут и QoS."),
        new KnownGame("r5apex.exe", "Apex Legends", "EasyAntiCheat (EAC)", AntiCheatStatus.Untested),
        new KnownGame("rustclient.exe", "Rust", "EasyAntiCheat (EAC)", AntiCheatStatus.Untested),
        new KnownGame("rainbowsix.exe", "Rainbow Six Siege", "BattlEye", AntiCheatStatus.Untested),
        new KnownGame("tslgame.exe", "PUBG: Battlegrounds", "BattlEye", AntiCheatStatus.Untested),
        new KnownGame("escapefromtarkov.exe", "Escape from Tarkov", "BattlEye", AntiCheatStatus.Untested),
        new KnownGame("cod.exe", "Call of Duty", "Ricochet (ядро)", AntiCheatStatus.KnownKernelConflict,
            "Ricochet — ядерный анти-чит: фильтр не поднимаем."),
        new KnownGame("modernwarfare.exe", "Call of Duty: Modern Warfare", "Ricochet (ядро)", AntiCheatStatus.KnownKernelConflict),
        new KnownGame("overwatch.exe", "Overwatch 2", "Защита Blizzard", AntiCheatStatus.Untested),
        new KnownGame("rocketleague.exe", "Rocket League", "EasyAntiCheat (EAC)", AntiCheatStatus.Untested),
        new KnownGame("gta5.exe", "GTA Online", "Защита Rockstar", AntiCheatStatus.Untested,
            "Часть сессий — peer-to-peer: трафик идёт напрямую другим игрокам, а не только на сервер."),
        new KnownGame("fivem_gtaprocess.exe", "FiveM (GTA V)", "Защита FiveM", AntiCheatStatus.Untested),
        new KnownGame("destiny2.exe", "Destiny 2", "BattlEye", AntiCheatStatus.Untested,
            "У игры были блокировки за VPN: релейные пути предлагаем только с вашего явного согласия."),
        new KnownGame("warframe.x64.exe", "Warframe", "Защита Digital Extremes", AntiCheatStatus.Untested),
        new KnownGame("deadbydaylight-win64-shipping.exe", "Dead by Daylight", "EasyAntiCheat (EAC)", AntiCheatStatus.Untested)
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

    /// <summary>Только свои игры человека (встроенные 20 так не помечены и не удаляются).</summary>
    public IReadOnlyList<KnownGame> CustomGames => _games.Where(game => game.IsCustom).ToList();

    /// <summary>
    /// Каталог «встроенные + свои». Битые записи своих игр (пустое имя/название, дубли встроенных)
    /// пропускаются, а не ломают список: служба обязана работать с 20 известными в любом случае.
    /// </summary>
    public static GameProcessCatalog WithCustomGames(IEnumerable<CustomGameEntry>? customGames)
    {
        var games = new List<KnownGame>(DefaultGames);
        var known = new HashSet<string>(DefaultGames.Select(game => Normalize(game.ProcessName)), StringComparer.OrdinalIgnoreCase);

        foreach (CustomGameEntry entry in customGames ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.ProcessName) || string.IsNullOrWhiteSpace(entry.TitleRu))
            {
                continue;
            }

            string processName = Normalize(entry.ProcessName);
            if (!known.Add(processName) || IsIgnored(processName))
            {
                continue;
            }

            games.Add(CustomGameValidation.ToKnownGame(new CustomGameEntry(processName, entry.TitleRu.Trim())));
        }

        return new GameProcessCatalog(games);
    }

    public bool TryMatch(string? processName, out KnownGame game)
    {
        game = null!;
        return !string.IsNullOrWhiteSpace(processName) && _byExe.TryGetValue(Normalize(processName), out game!);
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
