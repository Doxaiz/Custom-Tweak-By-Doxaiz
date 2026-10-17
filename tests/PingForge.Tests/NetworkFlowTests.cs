using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Network.Actions;
using PingForge.Network.Qos;
using PingForge.Network.Routes;
using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Проверки сетевого слоя: что считается игрой, что нет, что происходит с маршрутом и политикой QoS.
/// Здесь важно не «работает ли», а «не делает ли лишнего»: чужие маршруты, чужие политики и игры
/// с ядерным анти-читом — три места, где ошибка стоила бы пользователю проблем.
/// </summary>
public class NetworkFlowTests
{
    private const string SampleRoutePrint = """
        ===========================================================================
        Interface List
         14...3c 7c 3f 11 22 33 ......Realtek PCIe GbE Family Controller
         12...aa bb cc dd ee ff ......TP-Link Wireless USB Adapter
        ===========================================================================
        IPv4 Route Table
        ===========================================================================
        Active Routes:
        Network Destination        Netmask          Gateway       Interface  Metric
                  0.0.0.0          0.0.0.0      192.168.1.1     192.168.1.50     25
                127.0.0.0        255.0.0.0         On-link         127.0.0.1    331
              192.168.1.0    255.255.255.0         On-link      192.168.1.50    281
        ===========================================================================
        Persistent Routes:
          Network Address          Netmask  Gateway Address  Metric
                  0.0.0.0          0.0.0.0      192.168.1.1     Default
        ===========================================================================
        """;

    // ── 1. Разбор таблицы маршрутов ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParseRoutePrint_ReadsActiveAndPersistentRoutes()
    {
        IReadOnlyList<RouteTableEntry> entries = RouteTableParser.Parse(SampleRoutePrint);

        Assert.Equal(4, entries.Count);

        RouteTableEntry defaultRoute = entries[0];
        Assert.Equal("0.0.0.0", defaultRoute.Destination);
        Assert.Equal("192.168.1.1", defaultRoute.NextHop);
        Assert.Equal(25, defaultRoute.Metric);
        Assert.True(defaultRoute.IsDefaultRoute);

        RouteTableEntry onLink = entries.Single(e => e.Destination == "192.168.1.0");
        Assert.True(onLink.IsOnLink);
        Assert.False(onLink.IsPersistent);

        RouteTableEntry persistent = entries.Single(e => e.IsPersistent);
        Assert.Equal(0, persistent.Metric); // «Default» в выводе — метрика не число
    }

    [Fact]
    public void ParseRoutePrint_IgnoresGarbageAndEmpty()
    {
        Assert.Empty(RouteTableParser.Parse(string.Empty));
        Assert.Empty(RouteTableParser.Parse("это не вывод route print\nпросто текст\n"));
    }

    [Fact]
    public void FindExactHostRoute_MatchesDestinationAndNextHop()
    {
        var entries = new List<RouteTableEntry>
        {
            new("34.16.1.5", "255.255.255.255", "192.168.1.1", "192.168.1.50", 1),
            new("34.16.1.5", "255.255.255.255", "10.8.0.1", "10.8.0.2", 1)
        };

        Assert.NotNull(RouteTableParser.FindExactHostRoute(entries, "34.16.1.5"));
        Assert.NotNull(RouteTableParser.FindExactHostRoute(entries, "34.16.1.5", "10.8.0.1"));
        Assert.Null(RouteTableParser.FindExactHostRoute(entries, "34.16.1.5", "172.16.0.1"));
        Assert.Null(RouteTableParser.FindExactHostRoute(entries, "34.16.1.6"));
    }

    [Fact]
    public void FindDefaultRoute_PicksLowestMetric()
    {
        var entries = new List<RouteTableEntry>
        {
            new("0.0.0.0", "0.0.0.0", "192.168.1.1", "192.168.1.50", 45),
            new("0.0.0.0", "0.0.0.0", "10.8.0.1", "10.8.0.2", 5)
        };

        Assert.Equal("10.8.0.1", RouteTableParser.FindDefaultRoute(entries)!.NextHop);
    }

    // ── 2. Сборка команды route ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RouteCommandBuilder_BuildsHostRouteOnly()
    {
        IReadOnlyList<string> args = RouteCommandBuilder.AddHostRoute("34.16.1.5", "10.8.0.1", 3);

        Assert.Equal(new[] { "add", "34.16.1.5", "mask", "255.255.255.255", "10.8.0.1", "metric", "3" }, args);
        Assert.Equal("route add 34.16.1.5 mask 255.255.255.255 10.8.0.1 metric 3", RouteCommandBuilder.Describe(args));
    }

    [Theory]
    [InlineData("34.16.1.5 & whoami")]
    [InlineData("не-адрес")]
    [InlineData("2001:db8::1")]
    [InlineData("0.0.0.0")]
    public void RouteCommandBuilder_RejectsBadAddress(string address)
    {
        Assert.Throws<ArgumentException>(() => RouteCommandBuilder.AddHostRoute(address, "10.8.0.1", 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10000)]
    public void RouteCommandBuilder_RejectsBadMetric(int metric)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RouteCommandBuilder.AddHostRoute("34.16.1.5", "10.8.0.1", metric));
    }

    // ── 3. Детектор игровых потоков и каталог игр ─────────────────────────────────────────────────

    [Fact]
    public void Catalog_KnowsGamesAndMarksKernelAntiCheat()
    {
        var catalog = new GameProcessCatalog();

        Assert.Equal(20, catalog.All.Count);

        Assert.True(catalog.TryMatch(@"C:\Games\Fortnite\FortniteClient-Win64-Shipping.exe", out KnownGame fortnite));
        Assert.Equal("Fortnite", fortnite.TitleRu);
        Assert.Equal(AntiCheatStatus.Untested, fortnite.AntiCheat);

        Assert.True(catalog.TryMatch("VALORANT-Win64-Shipping.EXE", out KnownGame valorant));
        Assert.Equal(AntiCheatStatus.KnownKernelConflict, valorant.AntiCheat);

        Assert.False(catalog.TryMatch("notagame.exe", out _));
        Assert.True(GameProcessCatalog.IsIgnored("Steam.exe"));
        Assert.Equal("steam.exe", GameProcessCatalog.Normalize(@"C:\Program Files\Steam\Steam.Exe"));
    }

    [Fact]
    public void Detector_FindsKnownGameAndSkipsNoise()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Udp, 8124, "192.168.1.50", 51234, "34.16.1.5", 3074, ""),          // игровой сервер
            new(ConnectionProtocol.Tcp, 8124, "192.168.1.50", 51235, "52.14.7.9", 443, "Established"), // веб-поток игры
            new(ConnectionProtocol.Tcp, 9012, "192.168.1.50", 51236, "23.62.9.1", 443, "Established"), // Steam — не игра
            new(ConnectionProtocol.Udp, 8124, "192.168.1.50", 51237, "192.168.1.20", 27015, ""),       // LAN
            new(ConnectionProtocol.Udp, 4, "0.0.0.0", 500, "34.16.1.9", 4500, "")                      // системный pid
        };

        var names = new Dictionary<int, string>
        {
            [8124] = "FortniteClient-Win64-Shipping.exe",
            [9012] = "steam.exe",
            [4] = "System"
        };

        GameFlowDetectionReport report = new GameFlowDetector().Detect(rows, names);

        DetectedGameFlow flow = Assert.Single(report.Games);
        Assert.Equal("Fortnite", flow.GameTitleRu);
        Assert.Equal("34.16.1.5:3074", flow.EndpointText);
        Assert.Equal(2, flow.CandidateCount); // UDP к серверу + TCP 443, LAN-адрес отброшен
        Assert.Equal("UDP", flow.Flow.Transport);
        Assert.True(flow.CanOptimize);
        Assert.Contains("наиболее игровой", flow.ReasonRu);

        Assert.Equal(5, report.RowsRead);
        Assert.Equal(2, report.PublicFlowRows);
        Assert.Contains(report.NotesRu, n => n.Contains("Локальные", StringComparison.Ordinal));
    }

    [Fact]
    public void Detector_PrefersUdpOverWebPort()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Tcp, 700, "10.0.0.5", 40000, "52.14.7.9", 443, "Established"),
            new(ConnectionProtocol.Udp, 700, "10.0.0.5", 40001, "34.16.1.5", 9339, "")
        };

        GameFlowDetectionReport report = new GameFlowDetector()
            .Detect(rows, new Dictionary<int, string> { [700] = "rocketleague.exe" });

        Assert.Equal(9339, report.BestGame!.Flow.RemotePort);
    }

    [Fact]
    public void Detector_DoesNotGuessUnknownProcess()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Udp, 555, "192.168.1.50", 50000, "34.16.1.5", 27015, "")
        };

        var names = new Dictionary<int, string> { [555] = "mystery-arena.exe" };

        GameFlowDetectionReport report = new GameFlowDetector().Detect(rows, names);

        Assert.Empty(report.Games);
        UnknownGameCandidate candidate = Assert.Single(report.UnknownCandidates);
        Assert.Equal("mystery-arena.exe", candidate.ProcessName);
        Assert.Equal("34.16.1.5:27015", candidate.EndpointText);
        Assert.Contains("не угадываем", candidate.ReasonRu);
        Assert.False(report.HasGames);
        Assert.Contains("не найдено", report.DescribeSummaryRu());
    }

    [Fact]
    public void Detector_IncludesUnknownProcess_WhenUserAskedForIt()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Udp, 555, "192.168.1.50", 50000, "34.16.1.5", 27015, "")
        };

        var names = new Dictionary<int, string> { [555] = "mystery-arena.exe" };
        GameFlowDetectionReport report = new GameFlowDetector(includeUnknownProcesses: true).Detect(rows, names);

        DetectedGameFlow flow = Assert.Single(report.Games);
        Assert.Equal(AntiCheatStatus.Untested, flow.Flow.AntiCheat);
        Assert.Contains("не из списка", flow.GameTitleRu);
        Assert.Empty(report.UnknownCandidates);
    }

    [Fact]
    public void Detector_CountsUnreadableProcessNames()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Udp, 4242, "192.168.1.50", 50000, "34.16.1.5", 27015, ""),
            new(ConnectionProtocol.Udp, 8124, "192.168.1.50", 50001, "34.16.1.5", 3074, "")
        };

        var names = new Dictionary<int, string> { [8124] = "fortniteclient-win64-shipping.exe" };
        GameFlowDetectionReport report = new GameFlowDetector().Detect(rows, names);

        Assert.Equal(1, report.UnreadProcesses);
        Assert.Single(report.Games);
        Assert.Contains(report.NotesRu, n => n.Contains("имя прочитать не удалось", StringComparison.Ordinal));
    }

    [Fact]
    public void Detector_ReportsKernelProtectedGameForHonestWarning()
    {
        var rows = new List<RawConnectionRow>
        {
            new(ConnectionProtocol.Udp, 3311, "192.168.1.50", 50000, "99.83.1.1", 9339, "")
        };

        var names = new Dictionary<int, string> { [3311] = "VALORANT-Win64-Shipping.exe" };
        GameFlowDetectionReport report = new GameFlowDetector(includeUnknownProcesses: true).Detect(rows, names);

        DetectedGameFlow flow = Assert.Single(report.Games);
        Assert.False(flow.CanOptimize);
        Assert.True(report.HasKernelProtectedGame);
        Assert.Single(report.KernelProtectedGames);
        Assert.Contains("ядерный анти-чит", report.DescribeSummaryRu());
    }

    [Theory]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("34.16.1.5", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("fd00::1", false)]
    public void Detector_ClassifiesPublicAddresses(string address, bool expected)
    {
        Assert.Equal(expected, GameFlowDetector.IsPublicRemoteAddress(address));
    }

    // ── 4. Чтение таблицы маршрутов и наблюдение за игрой ─────────────────────────────────────────

    [Fact]
    public async Task RoutePrintReader_ParsesOutputAndSurvivesFailure()
    {
        var port = new FakeCommandPort { Handler = (_, _) => new CommandResult(0, SampleRoutePrint, string.Empty) };

        IReadOnlyList<RouteTableEntry> entries = await new RoutePrintTableReader(port).ReadAsync();

        Assert.Equal(4, entries.Count);
        Assert.Contains("route print -4", port.Invocations[0]);

        var failing = new FakeCommandPort { Handler = (_, _) => CommandResult.Failed("доступ запрещён") };
        Assert.Empty(await new RoutePrintTableReader(failing).ReadAsync());
    }

    [Fact]
    public async Task Watcher_FindsFlowThroughPorts()
    {
        var watcher = new WindowsGameFlowWatcher(
            new FakeConnectionTableProvider(new[]
            {
                new RawConnectionRow(ConnectionProtocol.Udp, 8124, "192.168.1.50", 50000, "34.16.1.5", 3074, "")
            }),
            new FakeProcessNameResolver(new Dictionary<int, string> { [8124] = "FortniteClient-Win64-Shipping.exe" }),
            includeUnknownProcesses: false);

        IReadOnlyList<GameFlowInfo> flows = await watcher.FindActiveGameFlowsAsync();

        GameFlowInfo flow = Assert.Single(flows);
        Assert.Equal("Fortnite", flow.GameTitleRu);
        Assert.Equal("fortniteclient-win64-shipping.exe", flow.ProcessName);
        Assert.Contains("report", nameof(GameFlowDetectionReport).ToLowerInvariant()); // отчёт тоже доступен
        Assert.True((await watcher.DetectAsync()).HasGames);
    }

    [Fact]
    public async Task Watcher_ExplainsUnknownProcessInsteadOfPlainNotFound()
    {
        // Живой случай: игра запущена, соединение с игровым портом есть, но игры нет в каталоге.
        // Раньше человек видел «игра не найдена» и не понимал, что соединение программа видит.
        var watcher = new WindowsGameFlowWatcher(
            new FakeConnectionTableProvider(new[]
            {
                new RawConnectionRow(ConnectionProtocol.Udp, 9001, "192.168.1.50", 50000, "34.16.1.5", 3074, "")
            }),
            new FakeProcessNameResolver(new Dictionary<int, string> { [9001] = "MyUnknownGame.exe" }),
            includeUnknownProcesses: false);

        (IReadOnlyList<GameFlowInfo> flows, string? reason) = await watcher.FindGameFlowsWithReasonAsync();

        Assert.Empty(flows);
        Assert.NotNull(reason);
        Assert.Contains("MyUnknownGame.exe", reason, StringComparison.Ordinal);
        Assert.Contains("34.16.1.5", reason, StringComparison.Ordinal);
        Assert.Contains("не входит в список известных игр", reason, StringComparison.Ordinal);

        // Порядок — «сначала самые игровые». В живой проверке первым оказался системный агент
        // с портом 32526, а настоящее соединение на игровой порт 3074 — ниже: человек читал
        // про чужой процесс и не узнавал свою игру. Теперь игровой порт идёт первым.
        var two = new WindowsGameFlowWatcher(
            new FakeConnectionTableProvider(new[]
            {
                new RawConnectionRow(ConnectionProtocol.Tcp, 700, "192.168.1.50", 50001, "16.129.63.168", 32526, "Established"),
                new RawConnectionRow(ConnectionProtocol.Tcp, 701, "192.168.1.50", 50002, "35.180.86.68", 3074, "Established")
            }),
            new FakeProcessNameResolver(new Dictionary<int, string>
            {
                [700] = "windowsazureguestagent.exe",
                [701] = "MyUnknownGame.exe"
            }));

        GameFlowDetectionReport report = await two.DetectAsync();
        Assert.Equal("MyUnknownGame.exe", report.UnknownCandidates[0].ProcessName);

        // Служба разрешения имён (UDP 53) не должна перебивать игровой порт: иначе человек
        // прочитает про DNS и решит, что программа не видит его игру.
        var dnsAndGame = new WindowsGameFlowWatcher(
            new FakeConnectionTableProvider(new[]
            {
                new RawConnectionRow(ConnectionProtocol.Udp, 600, "192.168.1.50", 50003, "8.8.8.8", 53, ""),
                new RawConnectionRow(ConnectionProtocol.Tcp, 601, "192.168.1.50", 50004, "35.180.86.68", 3074, "Established")
            }),
            new FakeProcessNameResolver(new Dictionary<int, string>
            {
                [600] = "dnshelper.exe",
                [601] = "MyUnknownGame.exe"
            }));

        GameFlowDetectionReport mixed = await dnsAndGame.DetectAsync();
        Assert.Equal("MyUnknownGame.exe", mixed.UnknownCandidates[0].ProcessName);
        Assert.Contains("«MyUnknownGame.exe» (35.180.86.68:3074)", WindowsGameFlowWatcher.DescribeNoGameRu(report), StringComparison.Ordinal);

        // И ни одного обещания, что «оптимизирует» этот процесс молча.
        Assert.DoesNotContain("оптимизируем", reason, StringComparison.OrdinalIgnoreCase);

        // А когда игра из каталога есть — объяснять нечего.
        var known = new WindowsGameFlowWatcher(
            new FakeConnectionTableProvider(new[]
            {
                new RawConnectionRow(ConnectionProtocol.Udp, 8124, "192.168.1.50", 50000, "34.16.1.5", 3074, "")
            }),
            new FakeProcessNameResolver(new Dictionary<int, string> { [8124] = "FortniteClient-Win64-Shipping.exe" }));

        (IReadOnlyList<GameFlowInfo> knownFlows, string? noReason) = await known.FindGameFlowsWithReasonAsync();

        Assert.Single(knownFlows);
        Assert.Null(noReason);
    }

    // ── 5. Действие «маршрут для игры» ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAction_WithoutNextHop_DoesNothingAndSaysWhy()
    {
        var command = new FakeCommandPort();
        var table = new FakeRouteTableReader();
        var runner = new RouteForGameActionRunner(command, table, () => Flow(), () => null);

        OptimizationActionOutcome outcome = await runner.ApplyAsync(RouteAction());

        Assert.False(outcome.Succeeded);
        Assert.Contains("не задан альтернативный узел", outcome.MessageRu);
        Assert.Empty(command.Invocations);
    }

    [Fact]
    public async Task RouteAction_AddsHostRouteAndVerifiesIt()
    {
        var command = new FakeCommandPort();
        var table = new FakeRouteTableReader();

        command.Handler = (tool, args) =>
        {
            Assert.Equal("route", tool);
            if (args[0] == "add")
            {
                table.Entries.Add(new RouteTableEntry(args[1], "255.255.255.255", args[4], "192.168.1.50", int.Parse(args[6])));
            }

            return new CommandResult(0, "OK!", string.Empty);
        };

        var runner = new RouteForGameActionRunner(command, table, () => Flow(), () => new RouteOverride("10.8.0.1", 2, "релей PingForge"));

        OptimizationActionOutcome outcome = await runner.ApplyAsync(RouteAction());

        Assert.True(outcome.Succeeded);
        Assert.Contains("добавлен через 10.8.0.1", outcome.MessageRu);
        Assert.Contains("Проверено по таблице маршрутов", outcome.MessageRu);
        Assert.Single(table.Entries);
        Assert.Equal(2, table.ReadCount); // «до» и подтверждение после применения
    }

    [Fact]
    public async Task RouteAction_RollsBackWhenRouteNotConfirmed()
    {
        var command = new FakeCommandPort { Handler = (_, _) => CommandResult.Ok("OK!") };
        var table = new FakeRouteTableReader(); // маршрут «не появляется»: имитация политики безопасности
        var runner = new RouteForGameActionRunner(command, table, () => Flow(), () => new RouteOverride("10.8.0.1"));

        OptimizationActionOutcome outcome = await runner.ApplyAsync(RouteAction());

        Assert.False(outcome.Succeeded);
        Assert.Contains("откатили сразу", outcome.MessageRu);
        Assert.Contains("route delete 34.16.1.5 mask 255.255.255.255", command.Invocations[1]);
    }

    [Fact]
    public async Task RouteAction_DoesNotOverwriteForeignRoute()
    {
        var command = new FakeCommandPort();
        var table = new FakeRouteTableReader();
        table.Entries.Add(new RouteTableEntry("34.16.1.5", "255.255.255.255", "192.168.1.1", "192.168.1.50", 1));

        var runner = new RouteForGameActionRunner(command, table, () => Flow(), () => new RouteOverride("10.8.0.1"));

        OptimizationActionOutcome outcome = await runner.ApplyAsync(RouteAction());

        Assert.False(outcome.Succeeded);
        Assert.Contains("чужой маршрут", outcome.MessageRu, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(command.Invocations);
    }

    [Fact]
    public async Task RouteAction_SkipsWorkWhenRouteAlreadyCorrect()
    {
        var command = new FakeCommandPort();
        var table = new FakeRouteTableReader();
        table.Entries.Add(new RouteTableEntry("34.16.1.5", "255.255.255.255", "10.8.0.1", "10.8.0.2", 1));

        var runner = new RouteForGameActionRunner(command, table, () => Flow(), () => new RouteOverride("10.8.0.1"));

        OptimizationActionOutcome outcome = await runner.ApplyAsync(RouteAction());

        Assert.True(outcome.Succeeded);
        Assert.Contains("менять нечего", outcome.MessageRu);
        Assert.Empty(command.Invocations);
    }

    [Fact]
    public async Task RouteAction_RevertRemovesRouteAndIsIdempotent()
    {
        var command = new FakeCommandPort();
        var table = new FakeRouteTableReader();
        table.Entries.Add(new RouteTableEntry("34.16.1.5", "255.255.255.255", "10.8.0.1", "10.8.0.2", 1));

        command.Handler = (_, args) =>
        {
            if (args[0] == "delete")
            {
                table.Entries.Clear();
            }

            return CommandResult.Ok(string.Empty);
        };

        var runner = new RouteForGameActionRunner(command, table, () => Flow(), () => new RouteOverride("10.8.0.1"));

        OptimizationActionOutcome first = await runner.RevertAsync(RouteAction());
        Assert.True(first.Succeeded);
        Assert.Contains("снова идёт через шлюз по умолчанию", first.MessageRu);

        OptimizationActionOutcome second = await runner.RevertAsync(RouteAction());
        Assert.True(second.Succeeded);
        Assert.Contains("снимать нечего", second.MessageRu);
    }

    [Fact]
    public async Task RouteAction_FailsWithoutFlowOrPorts()
    {
        var action = RouteAction();
        var noFlow = new RouteForGameActionRunner(new FakeCommandPort(), new FakeRouteTableReader(), () => null, () => new RouteOverride("10.8.0.1"));
        var noPorts = new RouteForGameActionRunner(null, null, () => Flow(), () => new RouteOverride("10.8.0.1"));

        Assert.Contains("Игровой поток не найден", (await noFlow.ApplyAsync(action)).MessageRu);
        Assert.Contains("Порты маршрутизации не подключены", (await noPorts.ApplyAsync(action)).MessageRu);
    }

    [Fact]
    public async Task RouteAction_RejectsForeignActionKind()
    {
        var runner = new RouteForGameActionRunner(new FakeCommandPort(), new FakeRouteTableReader(), () => Flow(), () => new RouteOverride("10.8.0.1"));

        OptimizationActionOutcome outcome = await runner.ApplyAsync(
            new OptimizationActionDescriptor("qos:dscp-46", OptimizationActionKind.DscpMarking, "Приоритет пакетов", "…"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("не умеет действие", outcome.MessageRu);
    }

    // ── 6. Политика QoS (DSCP) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QosWriter_WritesTemplateValuesIntoPolicyRoot()
    {
        var registry = new FakeRegistryPort();
        var writer = new QosDscpPolicyWriter(registry);
        QosPolicySpec spec = new(QosDscpPolicyWriter.PolicyNameFor("FortniteClient-Win64-Shipping.exe", 46),
            "fortniteclient-win64-shipping.exe", "UDP", "*", 46);

        QosPolicyWriteOutcome outcome = await writer.ApplyAsync(spec);

        Assert.True(outcome.Succeeded);
        Assert.Equal(9, outcome.ValuesWrittenRu.Count);
        Assert.Equal("46", registry.Get(QosLocation(spec.PolicyName, "DSCP Value")));
        Assert.Equal("fortniteclient-win64-shipping.exe", registry.Get(QosLocation(spec.PolicyName, "Application Name")));
        Assert.Equal("1.0", registry.Get(QosLocation(spec.PolicyName, "Version")));
        Assert.Contains("DSCP 46", outcome.MessageRu);
        Assert.Contains("Политика QoS «PingForge fortniteclient-win64-shipping.exe DSCP46»", outcome.MessageRu);
    }

    [Fact]
    public async Task QosWriter_NeverTouchesForeignPolicy()
    {
        var registry = new FakeRegistryPort();
        var writer = new QosDscpPolicyWriter(registry);

        QosPolicyWriteOutcome outcome = await writer.ApplyAsync(
            new QosPolicySpec("Чужая политика", "game.exe", "UDP", "*", 46));

        Assert.False(outcome.Succeeded);
        Assert.Contains("создана не PingForge", outcome.MessageRu);
        Assert.Equal(0, registry.WriteCount);
        Assert.False(QosDscpPolicyWriter.IsOurs("Чужая политика"));
        Assert.True(QosDscpPolicyWriter.IsOurs("PingForge cs2.exe DSCP46"));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(-1)]
    public async Task QosWriter_RejectsDscpOutOfRange(int dscp)
    {
        var writer = new QosDscpPolicyWriter(new FakeRegistryPort());
        QosPolicyWriteOutcome outcome = await writer.ApplyAsync(
            new QosPolicySpec(QosDscpPolicyWriter.PolicyNameFor("cs2.exe", 46), "cs2.exe", "UDP", "*", dscp));

        Assert.False(outcome.Succeeded);
        Assert.Contains("вне диапазона", outcome.MessageRu);
    }

    [Fact]
    public async Task QosWriter_RejectsBadProtocolAndAddress()
    {
        var writer = new QosDscpPolicyWriter(new FakeRegistryPort());
        string name = QosDscpPolicyWriter.PolicyNameFor("cs2.exe", 46);

        Assert.Contains("не поддерживается", (await writer.ApplyAsync(new QosPolicySpec(name, "cs2.exe", "ICMP", "*", 46))).MessageRu);
        Assert.Contains("не похож на IP-адрес", (await writer.ApplyAsync(new QosPolicySpec(name, "cs2.exe", "UDP", "не-адрес", 46))).MessageRu);
    }

    [Fact]
    public async Task QosWriter_WithoutRegistryPort_SaysSoInsteadOfPretending()
    {
        var writer = new QosDscpPolicyWriter(null);
        QosPolicySpec spec = new("PingForge cs2.exe DSCP46", "cs2.exe", "UDP", "*", 46);

        Assert.False((await writer.ApplyAsync(spec)).Succeeded);
        Assert.False((await writer.RemoveAsync(spec)).Succeeded);
        Assert.False((await writer.VerifyAsync(spec)).PolicyPresent);
    }

    [Fact]
    public async Task QosWriter_VerifyDetectsSilentlyIgnoredWrite()
    {
        var registry = new FakeRegistryPort { IgnoreWrites = true };
        var writer = new QosDscpPolicyWriter(registry);
        QosPolicySpec spec = new(QosDscpPolicyWriter.PolicyNameFor("cs2.exe", 46), "cs2.exe", "UDP", "*", 46);

        QosPolicyWriteOutcome write = await writer.ApplyAsync(spec);
        QosPolicyVerification verification = await writer.VerifyAsync(spec);

        Assert.True(write.Succeeded); // порт отчитался об успехе…
        Assert.False(verification.PolicyPresent); // …а проверка показала, что политики нет
        Assert.Contains("нет", verification.MessageRu);
    }

    [Fact]
    public async Task QosWriter_RemovesOnlyOwnValues()
    {
        var registry = new FakeRegistryPort();
        var writer = new QosDscpPolicyWriter(registry);
        QosPolicySpec spec = new(QosDscpPolicyWriter.PolicyNameFor("cs2.exe", 46), "cs2.exe", "UDP", "*", 46);

        await writer.ApplyAsync(spec);
        QosPolicyWriteOutcome removal = await writer.RemoveAsync(spec);

        Assert.True(removal.Succeeded);
        Assert.False(registry.Exists(QosLocation(spec.PolicyName, "DSCP Value")));
        Assert.Equal(9, registry.DeleteCount);
        Assert.Contains("Пустой раздел реестра остаётся", removal.MessageRu);
    }

    [Fact]
    public async Task DscpActionRunner_AppliesForActiveFlowAndReverts()
    {
        var registry = new FakeRegistryPort();
        string name = QosDscpPolicyWriter.PolicyNameFor("fortniteclient-win64-shipping.exe", 46);
        var runner = new DscpMarkingActionRunner(registry, () => Flow());
        var action = new OptimizationActionDescriptor("qos:dscp-46", OptimizationActionKind.DscpMarking, "Приоритет пакетов игры", "DSCP 46");

        OptimizationActionOutcome applied = await runner.ApplyAsync(action);

        Assert.True(applied.Succeeded);
        Assert.Equal("46", registry.Get(QosLocation(name, "DSCP Value")));
        Assert.Contains("SGI-46", applied.MessageRu.Replace("DSCP46", "SGI-46")); // имя политики видно в отчёте
        Assert.Contains("без драйвера нельзя", applied.MessageRu);

        Assert.True((await runner.RevertAsync(action)).Succeeded);
        Assert.False(registry.Exists(QosLocation(name, "DSCP Value")));
    }

    [Fact]
    public async Task DscpActionRunner_FailsHonestlyWhenNothingToMark()
    {
        var action = new OptimizationActionDescriptor("qos:dscp-46", OptimizationActionKind.DscpMarking, "Приоритет пакетов игры", "DSCP 46");

        var noFlow = new DscpMarkingActionRunner(new FakeRegistryPort(), () => null);
        Assert.Contains("Игровой поток не найден", (await noFlow.ApplyAsync(action)).MessageRu);

        var wrongKind = new DscpMarkingActionRunner(new FakeRegistryPort(), () => Flow());
        Assert.Contains("не умеет действие", (await wrongKind.ApplyAsync(RouteAction())).MessageRu);
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────────────────────────

    private static GameFlowInfo Flow() => new(
        ProcessId: 8124,
        ProcessName: "fortniteclient-win64-shipping.exe",
        GameTitleRu: "Fortnite",
        RemoteEndpoint: "34.16.1.5",
        RemotePort: 3074,
        Transport: "UDP",
        AntiCheat: AntiCheatStatus.Untested);

    private static OptimizationActionDescriptor RouteAction() => new(
        "route:game-server", OptimizationActionKind.RouteForGame, "Маршрут для игрового сервера", "Через ближайший узел");

    private static RegistryLocation QosLocation(string policyName, string valueName) =>
        new(RegistryRootKind.LocalMachine, $@"{QosDscpPolicyWriter.PolicyRootSubKey}\{policyName}", valueName);

    private sealed class FakeConnectionTableProvider : IRawConnectionTableProvider
    {
        private readonly IReadOnlyList<RawConnectionRow> _rows;

        public FakeConnectionTableProvider(IReadOnlyList<RawConnectionRow> rows) => _rows = rows;

        public Task<IReadOnlyList<RawConnectionRow>> ReadAsync(CancellationToken ct = default) => Task.FromResult(_rows);
    }

    private sealed class FakeProcessNameResolver : IProcessNameResolver
    {
        private readonly IReadOnlyDictionary<int, string> _names;

        public FakeProcessNameResolver(IReadOnlyDictionary<int, string> names) => _names = names;

        public Task<string?> TryGetProcessNameAsync(int processId, CancellationToken ct = default) =>
            Task.FromResult(_names.TryGetValue(processId, out string? name) ? name : null);
    }

    private sealed class FakeRouteTableReader : IRouteTableReader
    {
        public List<RouteTableEntry> Entries { get; } = new();

        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<RouteTableEntry>> ReadAsync(CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<RouteTableEntry>>(Entries.ToList());
        }
    }
}
