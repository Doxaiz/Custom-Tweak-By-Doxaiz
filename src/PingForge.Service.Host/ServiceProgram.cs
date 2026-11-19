using PingForge.Core.Abstractions;
using PingForge.Core.Security;
using PingForge.Core.Diagnostics;
using PingForge.Core.Tweaks;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Network;
using PingForge.Network.Routes;
using PingForge.Network.Windows;
using PingForge.Orchestration;
using PingForge.Orchestration.Realtime;
using PingForge.Probing;
using PingForge.Service;
using PingForge.Service.Protocol;
using PingForge.SystemState;
using PingForge.Service.Windows;
using PingForge.Tweaks;
using PingForge.Tweaks.Windows;

namespace PingForge.Service.Host;

/// <summary>
/// Точка входа службы. Здесь собирается всё вместе: живой каталог твиков, база, снапшоты, журнал,
/// канал связи. Вся логика — в проекте <c>PingForge.Service</c>, который проверяется тестами;
/// этот файл только соединяет части и пишет журнал службы.
///
/// Два режима запуска:
///   • диспетчер служб Windows (обычный) — процесс стартует от LocalSystem;
///   • руками из консоли (отладка) — то же самое, но видно вывод. Так удобно проверять на своей машине,
///     не устанавливая службу.
/// </summary>
public static class ServiceProgram
{
    /// <summary>Имя службы. Оно же используется в командах установки и в диспетчере служб.</summary>
    public const string ServiceName = "PingForge";

    /// <summary>Каталог данных: журнал, база и общий с окном settings.json. Нужен и частям, которые служба собирает отдельно.</summary>
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PingForge");

    private static string LogPath => Path.Combine(DataDirectory, "service.log");

    /// <summary>
    /// Журнал службы: 5 МБ и три файла (текущий плюс две копии). Прежний поворот держал одну копию,
    /// то есть после второго поворота исчезала история первого — а нужна она как раз при разборе жалобы.
    /// </summary>
    private static readonly Lazy<RollingLogFile> LogFile = new(() => new RollingLogFile(LogPath));

    public static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDirectory);

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(HelpRu());
            return 0;
        }

        Log($"Запуск. Каталог данных: {DataDirectory}");

        // Диспетчер служб зовёт нас сам; если он не позвал — значит запустили руками, работаем как процесс.
        bool ranAsService = ServiceControlDispatcher.TryRunAsService(ServiceName, RunAsync, Log);
        if (ranAsService)
        {
            return 0;
        }

        Log("Режим отладки: служба запущена как обычный процесс. Остановка — Ctrl+C.");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Log("Пришла остановка (Ctrl+C): закрываю канал и снимаю свои изменения.");
            stop.Cancel();
        };

        try
        {
            RunAsync(stop.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Служба остановилась из-за ошибки: {ex}");
            return 1;
        }
    }

    /// <summary>Собрать службу и слушать канал до отмены.</summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        // Живые доступы к реестру, netsh и командлетам NIC. Только здесь создаются настоящие порты:
        // всё остальное приложение работает через интерфейсы и потому проверяемо тестами.
        using var platform = new WindowsTweakPlatform();

        var restorePoints = new WindowsRestorePointService(platform.Command);
        TweakCatalog catalog = new(platform.ToCatalogPorts());

        // Чтение состояния — теми же портами и тем же каталогом, что применяет изменения: состояние
        // не может разойтись с тем, что программа пишет в снапшот.
        var systemState = new SystemStateReader(
            catalog, platform.Registry, platform.Command, platform.NicProperties, platform.ActiveAdapter);

        // ── Целостность базы (задача U) ─────────────────────────────────────────────────────────
        // Проверка идёт ДО DDL: испорченный файл не должен ронять службу в цикле рестартов. Если
        // база повреждена, файл не удаляется, а откладывается рядом; читаемая копия ставится на
        // место. Копия снимается средствами SQLite, а не копированием файла.
        var database = new SqliteDatabaseInitializer(Path.Combine(DataDirectory, "pingforge.db"));
        var backups = new DatabaseBackupService(database);

        // ── Кому принадлежит каталог данных ────────────────────────────────────────────────────
        // Откат снапшота пишет прежние значения в реестр от имени системы, а снапшоты лежат в базе
        // в этом каталоге. Значит подмена файла базы — это подмена того, что система применит.
        // Права не переписываем (чужой ACL молча ломать нельзя), но и молчать не имеем права.
        DataTrustReport directoryTrust = WindowsDataDirectoryTrust.InspectDirectory(DataDirectory);
        Log(directoryTrust.MessageRu);

        // Общий файл настроек: его пишут и окно, и служба. Если писать в него может любой пользователь,
        // чужие правки приходят сюда как «настройки человека» (профиль, узлы, свои игры), и заметить
        // это иначе негде — права каталога не всегда говорят о правах файла.
        // «Файла ещё нет» или «права не прочитали» — не повод шуметь на каждом старте: говорим,
        // только когда писать в файл действительно может кто угодно.
        DataTrustReport settingsTrust = WindowsDataDirectoryTrust.InspectFile(ServiceSettings.FilePath(DataDirectory));
        if (settingsTrust.NeedsAttention)
        {
            Log(settingsTrust.MessageRu);
        }
        DatabaseStartupReport databaseReport = await backups.PrepareForStartAsync(ct: ct).ConfigureAwait(false);
        Log(databaseReport.DescribeRu());

        try
        {
            await database.EnsureInitializedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // quick_check прошёл, а схема не применилась — файл всё равно непригоден. Один повтор
            // с чистой базой; старый файл сохраняется для разбора, а не затирается.
            Log($"База не инициализировалась: {ex.Message}. Начинаю с чистой базы, прежний файл сохраняю.");
            string? quarantined = backups.QuarantineCorruptFile();
            databaseReport = new DatabaseStartupReport(
                DatabaseStartupOutcome.StartedCleanAfterCorruption,
                null,
                quarantined,
                ex.Message);
            Log(databaseReport.DescribeRu());
            await database.EnsureInitializedAsync(ct).ConfigureAwait(false);
        }

        // Права у файла свои: каталог может быть закрыт, а база — открыта на запись всем.
        DataTrustReport databaseTrust = WindowsDataDirectoryTrust.InspectFile(database.DatabasePath);
        if (!databaseTrust.IsTrusted)
        {
            Log(databaseTrust.MessageRu);
        }

        var snapshotRepository = new SnapshotRepository(database);
        var journal = new AuditJournalRepository(database);

        // История сессий (задача J) — в той же базе, что снапшоты и журнал изменений.
        var sessions = new OptimizationSessionRepository(database);
        var snapshotService = new SnapshotService(
            snapshotRepository,
            catalog,
            new TargetValueResolver(),
            restorePoints);
        var coordinator = new TweakCoordinator(snapshotService, journal, catalog);

        // Задача U: копия базы — до снапшота и до первого изменения системы.
        ITweakCoordinator guardedCoordinator = new BackupBeforeApplyCoordinator(coordinator, backups, Log);

        // ── Сетевой слой: живая оптимизация уже запущенной игры ────────────────────────────────
        // Раньше здесь был null, и кнопка честно отвечала «движок не подключён». Теперь собраны
        // все части: поиск игрового потока по таблице соединений, настоящие пробы (ICMP и TCP),
        // действия «маршрут для игры» и «QoS DSCP», откат и фоновый контроль.
        // Свои игры человека из общего файла настроек (задача A): окно пишет, служба читает
        // терпимо — битый список означает «20 встроенных», а не остановку службы.
        IReadOnlyList<CustomGameEntry> customGames = ServiceSettings.ReadCustomGames(DataDirectory, out string? customGamesProblem);
        if (customGamesProblem is not null)
        {
            Log($"Свои игры: {customGamesProblem}.");
        }
        else if (customGames.Count > 0)
        {
            Log($"Свои игры из настроек: {customGames.Count} (анти-чит у них неизвестен).");
        }

        // UDP-собеседники игры: таблица соединений удалённую сторону у UDP не знает, поэтому
        // наблюдение идёт из событий сетевого стека Windows (ETW, только чтение). Сбой наблюдения
        // не роняет службу: без него UDP-игры просто не находятся, как и до этой части.
        // Пассивная задержка живого UDP-потока: пары «отправка → ответ» из тех же событий.
        // Пока пар нет — замер идёт пробами, и программа говорит об этом прямо.
        var udpRtt = new PassiveUdpRttTracker();
        using var udpPeers = new EtwUdpPeerListener(Log, onObservation: observation => udpRtt.Observe(observation));
        udpPeers.Start();
        Log(udpPeers.IsRunning
            ? "UDP-собеседники: наблюдение идёт, служба увидит UDP-поток игры."
            : $"UDP-собеседники: наблюдение не идёт — {udpPeers.ProblemRu ?? "причина не названа"}. Остальное работает.");
        Log("Живой UDP-поток: задержка считается по парам «отправка → ответ» из этих событий; " +
            "пока пар нет — замер идёт пробами, и способ замера называется рядом с числом.");

        // IPv6 читаем: у Minecraft Bedrock и IPv6-only серверов поток иначе невидим вовсе. Маршрут по IPv6
        // продукт не ставит, поэтому такие потоки попадают в отчёт с пояснением, а не в оптимизацию.
        using var network = new WindowsGameNetworkPlatform(
            platform,
            GameProcessCatalog.WithCustomGames(customGames),
            peerTracker: udpPeers,
            includeIpv6: true);

        // Настоящие транспорты: ICMP-эхо, время TCP-рукопожатия, системный DNS.
        var icmp = new SystemNetIcmpProbe();
        var probes = new RouteProbeService(
            icmp,
            new SocketTcpHandshakeProbe(),
            new SystemHostResolver(),
            new HopTracerouteEngine(icmp),
            liveFlowProvider: new UdpObservationsLiveFlowRttProvider(udpRtt));
        var measurement = new RouteProbeMeasurementSource(probes, samplesPerWindow: 12);

        // ── Сервер маршрутизации (задача B) ──────────────────────────────────────────────────
        // Узлы ищет и измеряет служба: у окна прав на маршруты нет, а смотреть таблицу маршрутов
        // может всякий. Своей сети узлов у проекта нет: кандидаты — только реальные шлюзы активных
        // адаптеров, VPN из таблицы маршрутов и ручной ввод человека.
        Func<RoutingSettings> readRouting = () =>
        {
            RoutingSettings routing = ServiceSettings.ReadRouting(DataDirectory, out string? routingProblem);
            if (routingProblem is not null)
            {
                Log($"Серверы маршрутизации: {routingProblem}.");
            }

            return routing;
        };

        var routeComparison = new RoutePathComparisonService(platform.Command, network.RouteTable, measurement);

        // Единственный источник RouteTarget для живого оптимизатора: узел из настроек (или победитель
        // замера путей) платформа получает здесь и только во время оптимизации — при запуске службы
        // не меняется ни маршрут, ни этот источник.
        var routePaths = new RoutingPathOptionsProvider(
            readRouting,
            target => network.RouteTarget = target,
            note => network.RouteRefusalNote = note,
            routeComparison,
            flow => network.ActiveFlow = flow);

        var routingCandidates = new WindowsRoutingCandidateSource(
            network.RouteTable,
            new PowerShellAdapterDirectory(platform.Command));

        var routingService = new HostRoutingService(network, routingCandidates, routeComparison, Log);

        RoutingSettings startupRouting = readRouting();
        Log(startupRouting.Nodes.Count == 0
            ? "Серверы маршрутизации: узлов в настройках нет — действие «маршрут для игры» честно откажет и подскажет, где выбрать."
            : $"Серверы маршрутизации: узлов {startupRouting.Nodes.Count}, по умолчанию " +
              $"{(startupRouting.DefaultNode is null ? "не выбран" : $"«{startupRouting.DefaultNode.Name}» ({startupRouting.DefaultNode.Address})")}, " +
              $"автовыбор {(startupRouting.AutoSelectBest ? "включён (путь выбирается замером при оптимизации)" : "выключен")}.");

        var realtime = new RealtimeOptimizer(
            network.Watcher,
            measurement,
            new GameNetworkActionRunner(network.RouteActions, network.DscpActions),
            pathOptions: routePaths);

        // Новая сессия оптимизации обязана мерить пути заново: провайдер помнит свежий вердикт,
        // чтобы контроль качества не запускал сравнение на каждый тик. Первый тик сессии — «ищу игру».
        realtime.Updated += snapshot =>
        {
            if (snapshot.State == RealtimeOptimizationState.FindingGame)
            {
                routePaths.Invalidate();
            }
        };

        // Профиль потребления — общий с окном файл: экономный профиль растягивает контроль,
        // «максимум реакции» — наоборот. Служба читает его при каждом запуске оптимизации.
        Func<PerfProfile> readProfile = () => ServiceSettings.ReadProfile(DataDirectory);
        Action<PerfProfile> writeProfile = profile =>
        {
            if (!ServiceSettings.TryWriteProfile(DataDirectory, profile))
            {
                Log($"Профиль {profile} не записался в {ServiceSettings.FilePath(DataDirectory)}");
            }
        };

        var monitor = new RealtimeMonitor(
            realtime,
            interval: () => new ResourceBudgetOptions(readProfile())
                .EffectiveMonitorInterval(TimeSpan.FromSeconds(2)),
            log: Log);

        Log("Живая оптимизация подключена: поиск игры, замеры ICMP/TCP, маршрут и QoS, откат.");

        // Служба идёт от LocalSystem: права на изменение системы у неё есть по определению.
        var operations = new LiveEngineOperations(
            catalog,
            snapshotService,
            guardedCoordinator,
            isElevationAvailable: true,
            journal: journal,
            realtime: realtime,
            readProfile: readProfile,
            applyProfile: writeProfile,
            stateReader: systemState,
            routing: routingService,
            sessions: sessions,
            databaseNote: databaseReport.NeedsAttention ? databaseReport.DescribeRu() : null,
            backups: backups);

        var dispatcher = new EngineDispatcher(operations);
        var server = new EnginePipeServer(dispatcher, Log);

        Log($"Каталог: {catalog.GetAllTweaks().Count} твиков. База: {database.DatabasePath}");
        Log(EngineCommandCatalog.DescribeRu());

        // Контроль живой оптимизации идёт рядом с каналом и заканчивается вместе со службой.
        Task monitorTask = monitor.RunAsync(ct);

        try
        {
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await monitorTask.ConfigureAwait(false);
            Log($"Живая оптимизация: окон контроля за сеанс — {monitor.Ticks}.");
        }
    }

    /// <summary>
    /// Журнал службы. Пишем и в файл, и в консоль: файл нужен, когда служба работает без окна,
    /// консоль — при отладочном запуске. Ротация — <see cref="RollingLogFile"/>: 5 МБ, три файла,
    /// строка никогда не обрезается, старые копии удаляются по одной.
    /// </summary>
    private static void Log(string messageRu)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {messageRu}";

        try
        {
            LogFile.Value.Append(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал не записался — это не повод не работать. Скажем об этом в консоль и продолжим.
            Console.Error.WriteLine($"Журнал службы недоступен: {ex.Message}");
        }

        Console.WriteLine(line);
    }

    /// <summary>Краткая справка: что это за программа и как её запускать.</summary>
    public static string HelpRu() =>
        $"""
        Custom Tweak By Doxaiz — служба программы.

        Это служебная часть программы: она меняет настройки Windows от имени LocalSystem и принимает
        команды от окна программы по каналу {EngineProtocol.PipeName}. Отдельной программы нет: техническое
        имя службы и её папки данных — PingForge, оно осталось от первых версий и не меняется, чтобы
        обновление не потеряло журнал изменений и снапшоты.

        Запуск диспетчером служб (обычный режим):
            sc create {ServiceName} binPath= "C:\путь\PingForge.Service.exe" start= auto
            sc start {ServiceName}

        Запуск вручную (отладка, без установки службы):
            PingForge.Service.exe

        Данные и журнал: {DataDirectory}
        Команды службы: {EngineCommandCatalog.All.Count} штук, из них {EngineCommandCatalog.ElevatedCommandCount} требуют прав администратора.
        """;
}
