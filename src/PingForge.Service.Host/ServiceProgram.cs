using PingForge.Core.Abstractions;
using PingForge.Core.Tweaks;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Network;
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

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PingForge");

    private static string LogPath => Path.Combine(DataDirectory, "service.log");

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

        var database = new SqliteDatabaseInitializer(Path.Combine(DataDirectory, "pingforge.db"));
        await database.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var snapshotRepository = new SnapshotRepository(database);
        var journal = new AuditJournalRepository(database);
        var snapshotService = new SnapshotService(
            snapshotRepository,
            catalog,
            new TargetValueResolver(),
            restorePoints);
        var coordinator = new TweakCoordinator(snapshotService, journal, catalog);

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

        using var network = new WindowsGameNetworkPlatform(platform, GameProcessCatalog.WithCustomGames(customGames));

        // Настоящие транспорты: ICMP-эхо, время TCP-рукопожатия, системный DNS.
        var icmp = new SystemNetIcmpProbe();
        var probes = new RouteProbeService(
            icmp,
            new SocketTcpHandshakeProbe(),
            new SystemHostResolver(),
            new HopTracerouteEngine(icmp));
        var measurement = new RouteProbeMeasurementSource(probes, samplesPerWindow: 12);

        var realtime = new RealtimeOptimizer(
            network.Watcher,
            measurement,
            new GameNetworkActionRunner(network.RouteActions, network.DscpActions));

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
            coordinator,
            isElevationAvailable: true,
            journal: journal,
            realtime: realtime,
            readProfile: readProfile,
            applyProfile: writeProfile,
            stateReader: systemState);

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
    /// консоль — при отладочном запуске. Ротация простая: файл больше мегабайта — начинаем заново,
    /// чтобы он не рос бесконечно на чужом диске.
    /// </summary>
    private static void Log(string messageRu)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {messageRu}";

        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
            {
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            }

            File.AppendAllText(LogPath, line + Environment.NewLine);
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
        Custom Tweak By Doxaiz — служба PingForge.

        Это служебная часть программы: она меняет настройки Windows от имени LocalSystem и принимает
        команды от окна программы по каналу {EngineProtocol.PipeName}.

        Запуск диспетчером служб (обычный режим):
            sc create {ServiceName} binPath= "C:\путь\PingForge.Service.exe" start= auto
            sc start {ServiceName}

        Запуск вручную (отладка, без установки службы):
            PingForge.Service.exe

        Данные и журнал: {DataDirectory}
        Команды службы: {EngineCommandCatalog.All.Count} штук, из них {EngineCommandCatalog.ElevatedCommandCount} требуют прав администратора.
        """;
}
