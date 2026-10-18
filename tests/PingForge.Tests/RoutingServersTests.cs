using PingForge.App.ViewModels;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Network.Actions;
using PingForge.Network.Routes;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Задача B: «Сервер маршрутизации» — узлы, замер путей, честный отказ.
///
/// Всё проверяется через подменяемый сетевой бэкенд: командный порт и читалка таблицы маршрутов
/// с двойниками, у которых «route» живёт в памяти, замерщик с заранее заданной программой ответов.
/// Это ровно та граница портов, на которой работают настоящие route.exe и ICMP — подменил двойника,
/// и вся логика продукта (валидация, сравнение, откат, вердикты, права) прогоняется без Windows.
/// </summary>
public sealed class RoutingServersTests
{
    private const string GameServer = "34.16.1.5";

    // ── B1. валидация адреса: строже не бывает, и ни одного исключения наружу ─────────────────

    [Theory]
    [InlineData("10.8.0.1", true)]
    [InlineData(" 192.168.1.1 ", true)]
    [InlineData("010.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("999.1.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("224.0.0.5", false)]
    [InlineData("example.com", false)]
    [InlineData("::1", false)]
    [InlineData("10.8.0.1:443", false)]
    [InlineData("10.8.0.1/24", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Валидация_IPv4_строгая_без_исключений(string? candidate, bool allowed) =>
        Assert.Equal(allowed, RoutingNodeValidation.IsStrictIpv4(candidate));

    [Fact]
    public void Ручной_узел_валидируется_дубли_и_мусор_отклоняются()
    {
        RoutingNodeEntry[] existing = [new("10.8.0.1", "WG", RoutingNodeSourceRu.Manual)];

        Assert.True(RoutingNodeValidation.TryCreateManual(" 192.168.66.1 ", "домашний роутер", existing, out RoutingNodeEntry? node, out string error));
        Assert.Equal("", error);
        Assert.Equal("192.168.66.1", node!.Address);
        Assert.Equal("домашний роутер", node.Name);
        Assert.Equal(RoutingNodeSourceRu.Manual, node.SourceRu);

        Assert.False(RoutingNodeValidation.TryCreateManual("10.8.0.1", "дубль", existing, out _, out string duplicate));
        Assert.Contains("уже в списке", duplicate);

        Assert.False(RoutingNodeValidation.TryCreateManual("вгпс.пример.ру", null, existing, out _, out string notIp));
        Assert.Contains("не подходит", notIp);

        Assert.False(RoutingNodeValidation.TryCreateManual(null, null, existing, out _, out string empty));
        Assert.Contains("пустой", empty);

        // Пустая подпись — не ошибка: именем становится сам адрес, чтобы в списке не было безымянных строк.
        Assert.True(RoutingNodeValidation.TryCreateManual("10.0.0.2", "  ", existing, out RoutingNodeEntry? bare, out _));
        Assert.Equal("10.0.0.2", bare!.Name);
    }

    [Fact]
    public void Подпись_узла_не_содержит_управляющих_символов_и_не_бесконечна()
    {
        string dirty = "длинная " + new string('а', 200) + "\r\nподпись\tс табуляцией";
        string label = RoutingNodeValidation.SanitizeLabel(dirty);

        Assert.True(label.Length <= RoutingNodeValidation.MaxLabelChars);
        Assert.DoesNotContain('\n', label);
        Assert.DoesNotContain('\r', label);
        Assert.DoesNotContain('\t', label);
    }

    // ── B1. автопоиск: только реальные шлюзы, никаких выдуманных «серверов PingForge» ──────────

    private static IReadOnlyList<RouteTableEntry> SampleRoutes() =>
    [
        new RouteTableEntry("0.0.0.0", "0.0.0.0", "192.168.1.1", "192.168.1.50", 25),
        new RouteTableEntry("0.0.0.0", "0.0.0.0", "10.0.0.1", "10.0.0.10", 30),
        new RouteTableEntry("10.8.0.0", "255.255.255.0", "10.8.0.1", "10.8.0.2", 2),
        new RouteTableEntry("192.168.1.0", "255.255.255.0", "On-link", "192.168.1.50", 25, IsOnLink: true),
        new RouteTableEntry("127.0.0.0", "255.0.0.0", "On-link", "127.0.0.1", 331, IsOnLink: true),
        new RouteTableEntry("255.255.255.255", "255.255.255.255", "On-link", "192.168.1.50", 25, IsOnLink: true),
    ];

    private static IReadOnlyList<AdapterInfo> SampleAdapters() =>
    [
        new AdapterInfo(12, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201", true, ["192.168.1.50"]),
        new AdapterInfo(14, "Ethernet", "Realtek Gaming GbE Family Controller", true, ["10.0.0.10"]),
        new AdapterInfo(21, "WireGuard", "Wintun Userspace Tunnel", true, ["10.8.0.2"]),
        new AdapterInfo(1, "Loopback Pseudo-Interface 1", "Software Loopback Interface 1", true, ["127.0.0.1"]),
    ];

    [Fact]
    public void Поиск_находит_только_реальные_шлюзы_и_подписывает_источники_честно()
    {
        IReadOnlyList<RouteTableEntry> routes = SampleRoutes();
        IReadOnlyList<RoutingNodeEntry> found = RoutingNodeDiscovery.Discover(routes, SampleAdapters());

        Assert.Equal(3, found.Count);

        // Текущий — следующий узел маршрута по умолчанию с наименьшей метрикой; он и в списке первый.
        Assert.Equal("192.168.1.1", found[0].Address);
        Assert.Equal(RoutingNodeSourceRu.CurrentGateway, found[0].SourceRu);
        Assert.Equal("Wi-Fi", found[0].Name);

        // VPN — по имени туннельного адаптера, без единой попытки подключиться.
        RoutingNodeEntry vpn = found.First(node => node.SourceRu.StartsWith("VPN-адаптер ", StringComparison.Ordinal));
        Assert.Equal("10.8.0.1", vpn.Address);
        Assert.Equal("WireGuard", vpn.Name);
        Assert.Contains("«WireGuard»", vpn.SourceRu, StringComparison.Ordinal);

        // Остальное — второй шлюз.
        RoutingNodeEntry second = found.First(node => node.Address == "10.0.0.1");
        Assert.Equal(RoutingNodeSourceRu.SecondGateway, second.SourceRu);

        // Источники — только из белосписка продукта.
        Assert.All(found, node => Assert.True(RoutingNodeSourceRu.IsHonest(node.SourceRu), $"источник «{node.SourceRu}» нечестный"));

        // Выдуманных серверов не бывает в принципе: адрес каждого найденного узла существует в таблице
        // маршрутов, и ни одна подпись не обещает «сеть PingForge», акселератор или «бесплатный VPN».
        Assert.All(found, node => Assert.Contains(routes, route => route.NextHop == node.Address));
        Assert.DoesNotContain(found, node => node.Address.Contains("pingforge", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("ускорител", StringComparison.OrdinalIgnoreCase)
            || node.SourceRu.Contains("наш сервер", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Поиск_без_данных_об_адаптерах_остаётся_честным_просто_без_имён()
    {
        IReadOnlyList<RoutingNodeEntry> found = RoutingNodeDiscovery.Discover(SampleRoutes(), []);

        Assert.Equal(3, found.Count);
        Assert.All(found, node => Assert.Equal(node.Address, node.Name));
        Assert.Contains(found, node => node.SourceRu == RoutingNodeSourceRu.CurrentGateway);
    }

    [Fact]
    public void Петлевой_и_выключенный_адаптер_узлов_не_дает()
    {
        IReadOnlyList<RouteTableEntry> routes =
        [
            new RouteTableEntry("0.0.0.0", "0.0.0.0", "127.0.0.1", "127.0.0.1", 1),
            new RouteTableEntry("172.16.0.0", "255.255.255.0", "172.16.0.1", "172.16.0.5", 5),
        ];
        IReadOnlyList<AdapterInfo> adapters =
        [
            new AdapterInfo(1, "Loopback Pseudo-Interface 1", "Software Loopback Interface 1", true, ["127.0.0.1"]),
            new AdapterInfo(7, "Hyper-V Ethernet", "Hyper-V Virtual Ethernet Adapter", false, ["172.16.0.5"]),
        ];

        IReadOnlyList<RoutingNodeEntry> found = RoutingNodeDiscovery.Discover(routes, adapters);

        // Оба «шлюза» — с обрубленного/петлевого адаптера: список пуст, программа не выдаёт мусор за выбор.
        Assert.Empty(found);
    }

    [Fact]
    public void Разбор_списка_адаптеров_из_PowerShell_видит_VPN_петлю_и_статус()
    {
        string output = string.Join('\n',
            "A|12|Wi-Fi|Intel(R) Wi-Fi 6 AX201|Up",
            "A|21|WireGuard|Wintun Userspace Tunnel|Up",
            "A|1|Loopback Pseudo-Interface 1|Software Loopback Interface 1|Up",
            "A|9|Bluetooth|Bluetooth Device (Personal Area Network)|Disconnected",
            "какая-то случайная строка из вывода PowerShell",
            "I|12|192.168.1.50",
            "I|21|10.8.0.2",
            "I|1|127.0.0.1");

        IReadOnlyList<AdapterInfo> adapters = AdapterDirectoryParser.Parse(output);

        Assert.Equal(4, adapters.Count);
        AdapterInfo wifi = adapters.Single(adapter => adapter.Alias == "Wi-Fi");
        Assert.True(wifi.IsUp);
        Assert.Equal(["192.168.1.50"], wifi.Ipv4Addresses);

        AdapterInfo vpn = adapters.Single(adapter => adapter.Alias == "WireGuard");
        Assert.True(AdapterDirectoryParser.LooksLikeVpn(vpn));
        Assert.False(AdapterDirectoryParser.LooksLikeVpn(wifi));
        Assert.True(AdapterDirectoryParser.LooksLikeLoopback(adapters.Single(adapter => adapter.Alias.StartsWith("Loopback", StringComparison.Ordinal))));
        Assert.False(adapters.Single(adapter => adapter.Alias == "Bluetooth").IsUp);
    }

    // ── чтение настроек службой: терпимо, без падений ─────────────────────────────────────────

    [Fact]
    public void Пустой_и_битый_конфиг_читается_терпимо_а_не_падает()
    {
        RoutingSettings empty = RoutingNodesJson.Read(string.Empty, out string? emptyProblem);
        Assert.Empty(empty.Nodes);
        Assert.Null(emptyProblem);

        RoutingSettings broken = RoutingNodesJson.Read("{ это не json,,,", out string? brokenProblem);
        Assert.Empty(broken.Nodes);
        Assert.NotNull(brokenProblem);

        RoutingSettings notArray = RoutingNodesJson.Read("""{"RoutingNodes": {"а": 1}}""", out string? typeProblem);
        Assert.Empty(notArray.Nodes);
        Assert.Contains("не список", typeProblem);

        // Частично битый список: годные узлы остаются, мусор пропускается.
        RoutingSettings mixed = RoutingNodesJson.Read("""
            {"RoutingNodes":[
              {"Address":"10.8.0.1","Name":"WG","SourceRu":"VPN-адаптер «WG»"},
              {"Address":"не-адрес","Name":"нет"},
              "странный элемент",
              {"Address":"010.1.1.1","Name":"ведущий ноль"},
              {"Address":"10.8.0.1","Name":"дубль"},
              {"Address":"192.168.66.1","Name":"роутер"}
            ],"RoutingDefaultNode":"10.8.0.1","RoutingAutoSelect":true}
            """, out string? mixedProblem);

        Assert.Equal(2, mixed.Nodes.Count);
        Assert.Contains("битая", mixedProblem);
        Assert.True(mixed.AutoSelectBest);
        Assert.Equal("10.8.0.1", mixed.DefaultNodeAddress);
        Assert.Equal("WG", mixed.DefaultNode!.Name);

        // Узла по умолчанию нет в списке — сбрасываем выбор, а не держим ссылку в никуда.
        RoutingSettings dangling = RoutingNodesJson.Read("""
            {"RoutingNodes":[{"Address":"10.8.0.1","Name":"WG","SourceRu":"введён вручную"}],"RoutingDefaultNode":"10.9.9.9"}
            """, out _);
        Assert.Null(dangling.DefaultNodeAddress);

        // Источник не из наших подписей → честно помечаем ручным: «элитных серверов» чтение не придумает.
        RoutingSettings liar = RoutingNodesJson.Read("""
            {"RoutingNodes":[{"Address":"10.8.0.1","Name":"X","SourceRu":"элитный сервер PingForge"}]}
            """, out _);
        Assert.Equal(RoutingNodeSourceRu.Manual, liar.Nodes[0].SourceRu);
    }

    [Fact]
    public void Настройки_узлов_переживают_перезапуск_и_не_затираются_другим_писателем()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf_routing_{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileSettingsStore(path);
            store.Save(new AppSettings
            {
                RoutingNodes = [new RoutingNodeEntry("10.8.0.1", "WG", RoutingNodeSourceRu.Manual, MedianRttMs: 31.5)],
                RoutingDefaultNode = "10.8.0.1",
                RoutingAutoSelect = true,
            });

            string fileText = File.ReadAllText(path);
            Assert.Contains("RoutingNodes", fileText, StringComparison.Ordinal);
            Assert.Contains("10.8.0.1", fileText, StringComparison.Ordinal);

            // Служба читает этот же текст тем же набором правил: окно и служба не разъедутся.
            RoutingSettings serviceView = RoutingNodesJson.Read(fileText, out string? serviceProblem);
            Assert.Null(serviceProblem);
            Assert.Single(serviceView.Nodes);
            Assert.Equal(31.5, serviceView.Nodes[0].MedianRttMs!.Value, 1);
            Assert.True(serviceView.AutoSelectBest);
            Assert.Equal("10.8.0.1", serviceView.DefaultNode!.Address);

            // Второй писатель (профиль службы через общий writer) не съедает узлы.
            var writer = new PingForge.Core.Security.SharedSettingsFileWriter(path);
            writer.Update(root =>
            {
                root["PerfProfile"] = "economy";
                return root;
            });

            AppSettings afterProfileWrite = store.Load();
            Assert.Equal("economy", afterProfileWrite.PerfProfile);
            Assert.Single(afterProfileWrite.RoutingNodes!);
            Assert.True(afterProfileWrite.RoutingAutoSelect);

            // И обратно: пересохранение окна оставляет профиль службы в покое.
            store.Save(afterProfileWrite with { RoutingAutoSelect = false });
            Assert.Equal("economy", store.Load().PerfProfile);
            Assert.False(store.Load().RoutingAutoSelect);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ── B2. сравнение путей: временный /32, порядок победы, откат при любом исходе ────────────

    [Fact]
    public async Task Победитель_по_медиане_временный_маршрут_снят_таблица_как_была()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, hop => hop is null ? Window(40) : hop == "10.8.0.1" ? Window(30) : Window(38));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(
            GameServer, 3074, [Node("10.8.0.1", "WG"), Node("10.0.0.1", "второй")]);

        Assert.True(report.HasWinner);
        Assert.Equal("10.8.0.1", report.Winner!.Address);
        Assert.Contains("выигрыш 10 мс", report.VerdictRu, StringComparison.Ordinal);
        Assert.Contains("Этот путь можно применять", report.VerdictRu, StringComparison.Ordinal);
        Assert.True(report.TableRestored);

        // Каждый замер через узел — это add + delete, и ни одного следа в таблице.
        Assert.Contains($"route add {GameServer} mask 255.255.255.255 10.8.0.1 metric 1", system.Command.Invocations);
        Assert.Contains($"route delete {GameServer} mask 255.255.255.255", system.Command.Invocations);
        Assert.Empty(system.Table.Entries);

        // Замеров было ровно три: прямой путь и два узла.
        Assert.Equal(3, probe.Count);
    }

    [Fact]
    public async Task При_равных_медианах_решает_джиттер_потом_потери()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement byJitter = new(system, hop => hop is null ? Window(40) : hop == "10.0.0.9" ? Window(30, jitter: 9, loss: 0.5) : Window(30, jitter: 2));

        var service = new RoutePathComparisonService(system.Command, system.Reader, byJitter);
        RoutePathComparisonReport jitterReport = await service.CompareAsync(
            GameServer, 0, [Node("10.0.0.9", "дрожит"), Node("10.0.0.8", "ровный")]);

        Assert.Equal("10.0.0.8", jitterReport.Winner!.Address);
        Assert.Contains("Лучший из 2 измеренных", jitterReport.VerdictRu, StringComparison.Ordinal);

        // Ровный и по джиттеру — решает последний критерий, потери.
        ScriptedMeasurement byLoss = new(system, hop => hop is null ? Window(40) : hop == "10.0.0.9" ? Window(30, jitter: 2, loss: 1.5) : Window(30, jitter: 2));
        var service2 = new RoutePathComparisonService(system.Command, system.Reader, byLoss);
        RoutePathComparisonReport lossReport = await service2.CompareAsync(
            GameServer, 0, [Node("10.0.0.9", "с потерями"), Node("10.0.0.8", "без потерь")]);

        Assert.Equal("10.0.0.8", lossReport.Winner!.Address);
    }

    [Fact]
    public async Task Узел_хуже_прямого_пути_победителя_нет_и_вердикт_говорит_правду()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, hop => hop is null ? Window(40) : Window(47));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.0.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Contains("Прямой путь короче на 7 мс", report.VerdictRu, StringComparison.Ordinal);
        Assert.Contains("ничего не меняем", report.VerdictRu, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.TableRestored);

        // Сравнение не оставляет изменений: таблица пуста, как была до замеров.
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task Разница_в_пределах_погрешности_тоже_ничего_не_меняем()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, _ => Window(40));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.0.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Contains("Прямой путь не хуже", report.VerdictRu, StringComparison.Ordinal);
        Assert.Contains("разница в пределах погрешности", report.VerdictRu, StringComparison.Ordinal);

        // Временные маршруты ставились и снялись: ни одного add без пары delete.
        int adds = system.Command.Invocations.Count(line => line.StartsWith("route add", StringComparison.Ordinal));
        int deletes = system.Command.Invocations.Count(line => line.StartsWith("route delete", StringComparison.Ordinal));
        Assert.Equal(1, adds);
        Assert.Equal(adds, deletes);
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task Прямой_не_измерился_сравнения_нет_и_узел_не_выбирается()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, hop => hop is null
            ? PathMeasurement.Unmeasurable("маршрут до сервера есть, ответы нет")
            : Window(30));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.0.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Contains("не измерился", report.VerdictRu, StringComparison.Ordinal);
        Assert.Contains("не выбираю", report.VerdictRu, StringComparison.Ordinal);
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task Исключение_замера_временный_маршрут_всё_равно_снимается()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, _ => Window(30)) { ThrowOnHop = "10.8.0.1" };

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.8.0.1", "падение")]);

        // Замер упал — временный /32 снят в finally, в таблице чисто, вердикт не притворяется победой.
        Assert.Empty(system.Table.Entries);
        Assert.True(report.TableRestored);
        Assert.False(report.HasWinner);
        Assert.Contains(report.ViaNodes, result => result.ProblemRu is not null && result.ProblemRu.Contains("замер упал", StringComparison.Ordinal));
        Assert.Contains($"route delete {GameServer} mask 255.255.255.255", system.Command.Invocations);
    }

    [Fact]
    public async Task Команда_add_прошла_но_маршрута_в_таблице_нет_замер_отменён_без_мусора()
    {
        FakeRouteSystem system = new() { ConfirmAdds = false };
        ScriptedMeasurement probe = new(system, _ => Window(30));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.8.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Equal(1, probe.Count); // через несостоявшийся маршрут не мерим
        Assert.Contains(report.ViaNodes, result => result.ProblemRu is not null && result.ProblemRu.Contains("не появился", StringComparison.Ordinal));
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task route_отказал_без_прав_причина_в_вердикте_а_не_выдуманное_измерение()
    {
        FakeRouteSystem system = new() { AddResult = CommandResult.Failed("Политика безопасности Windows (ошибка 87).") };
        ScriptedMeasurement probe = new(system, hop => hop is null ? Window(40) : Window(30));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.8.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Contains(report.ViaNodes, result => result.ProblemRu is not null && result.ProblemRu.Contains("права администратора", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, probe.Count);
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task Чужой_slash32_к_адресу_не_перезаписывается_сравнение_его_не_трогает()
    {
        FakeRouteSystem system = new();
        system.Table.Entries.Add(new RouteTableEntry(GameServer, "255.255.255.255", "172.16.0.1", "192.168.1.50", 7));
        string foreignKey = system.Table.Entries[0].Key;

        ScriptedMeasurement probe = new(system, _ => Window(40));
        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);

        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("10.8.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Contains("уже есть маршрут", report.VerdictRu, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(system.Command.Invocations, line => line.StartsWith("route add", StringComparison.Ordinal));
        Assert.DoesNotContain(system.Command.Invocations, line => line.StartsWith("route delete", StringComparison.Ordinal));

        // Чужая строка стоит нетронутой.
        Assert.Equal(foreignKey, Assert.Single(system.Table.Entries).Key);
    }

    [Fact]
    public async Task Узел_равный_текущему_шлюзу_это_прямой_путь_и_таблицу_не_трогаем()
    {
        FakeRouteSystem system = new();
        system.Table.Entries.Add(new RouteTableEntry("0.0.0.0", "0.0.0.0", "192.168.1.1", "192.168.1.50", 25));
        ScriptedMeasurement probe = new(system, _ => Window(40));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);

        // 192.168.1.1 — текущий шлюз машины: «через него» = прямой путь, временный /32 не нужен.
        RoutePathComparisonReport report = await service.CompareAsync(GameServer, 0, [Node("192.168.1.1", "текущий")]);

        Assert.False(report.HasWinner);
        Assert.Empty(system.Command.Invocations);
        Assert.Contains(report.ViaNodes, result => result.ProblemRu is not null && result.ProblemRu.Contains("тот же прямой путь", StringComparison.Ordinal));

        // Маршрут по умолчанию, который был до замера, стоит на месте.
        Assert.Single(system.Table.Entries);
    }

    [Fact]
    public async Task Имя_вместо_адреса_сервера_сравнение_не_запускается_вовсе()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, _ => Window(40));

        var service = new RoutePathComparisonService(system.Command, system.Reader, probe);
        RoutePathComparisonReport report = await service.CompareAsync("eu.destinypuppet.shared", 0, [Node("10.0.0.1")]);

        Assert.False(report.HasWinner);
        Assert.Contains("не IPv4", report.VerdictRu, StringComparison.Ordinal);
        Assert.Empty(system.Command.Invocations);
        Assert.Equal(0, probe.Count);
    }

    // ── B3. провайдер: настройки доходят до платформы; отказ честный; применяет только измеренное ──

    private static GameFlowInfo Flow() => new(
        ProcessId: 8124,
        ProcessName: "fortniteclient-win64-shipping.exe",
        GameTitleRu: "Fortnite",
        RemoteEndpoint: GameServer,
        RemotePort: 3074,
        Transport: "UDP",
        AntiCheat: AntiCheatStatus.Untested);

    [Fact]
    public async Task Узел_из_настроек_доходит_до_платформы_и_применяется_как_slash32_на_адрес_сервера()
    {
        FakeRouteSystem system = new();
        RouteOverride? target = null;
        GameFlowInfo? flow = null;

        var provider = new RoutingPathOptionsProvider(
            () => new RoutingSettings([Node("10.8.0.1", "WG", RoutingNodeSourceRu.VpnAdapter("WG"))], "10.8.0.1", AutoSelectBest: false),
            value => target = value,
            note => { },
            comparison: null,
            applyFlow: applied => flow = applied);

        IReadOnlyList<RealtimePathOption> options = await provider.GetOptionsAsync(Flow());
        Assert.Empty(options);
        Assert.Equal("10.8.0.1", target!.NextHop);
        Assert.Equal(GameServer, flow!.RemoteEndpoint);

        // То же самое состояние наживую проходит исполнителя: /32 ровно на адрес сервера игры.
        var runner = new RouteForGameActionRunner(system.Command, system.Reader, () => flow, () => target);
        OptimizationActionOutcome outcome = await runner.ApplyAsync(RouteAction());

        Assert.True(outcome.Succeeded, outcome.MessageRu);
        Assert.Contains($"route add {GameServer} mask 255.255.255.255 10.8.0.1 metric 1", system.Command.Invocations);
        Assert.Contains("VPN-адаптер", outcome.MessageRu, StringComparison.Ordinal);
        Assert.Contains("Проверено по таблице", outcome.MessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Без_узла_состояние_не_меняется_а_отказ_указывает_куда_идти()
    {
        FakeRouteSystem system = new();
        RouteOverride? target = new("что-то_стояло");

        var provider = new RoutingPathOptionsProvider(
            () => RoutingSettings.Empty,
            value => target = value,
            note => Assert.Null(note), // узлов нет — это «не выбрано», а не «измерено и отказано»
            comparison: null,
            applyFlow: _ => { });

        await provider.GetOptionsAsync(Flow());
        Assert.Null(target); // цель снята, маршрут по умолчанию не тронут

        var runner = new RouteForGameActionRunner(system.Command, system.Reader, Flow, () => target);
        OptimizationActionOutcome refusal = await runner.ApplyAsync(RouteAction());

        Assert.False(refusal.Succeeded);
        Assert.Contains("не задан альтернативный узел", refusal.MessageRu, StringComparison.Ordinal);
        Assert.Contains("Сервер маршрутизации", refusal.MessageRu, StringComparison.Ordinal);
        Assert.Empty(system.Command.Invocations);
    }

    [Fact]
    public async Task Автовыбор_без_выигрыша_не_ведёт_трафик_и_передаёт_причину_замера_в_отказ()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, hop => hop is null ? Window(40) : Window(52));
        var comparison = new RoutePathComparisonService(system.Command, system.Reader, probe);

        RouteOverride? target = new("было-и-снялось");
        string? note = null;

        var provider = new RoutingPathOptionsProvider(
            () => new RoutingSettings([Node("10.0.0.1")], null, AutoSelectBest: true),
            value => target = value,
            written => note = written,
            comparison);

        await provider.GetOptionsAsync(Flow());

        Assert.Null(target);
        Assert.NotNull(note);
        Assert.Contains("Прямой путь короче на 12 мс", note, StringComparison.Ordinal);

        var runner = new RouteForGameActionRunner(system.Command, system.Reader, Flow, () => target, () => note);
        OptimizationActionOutcome refusal = await runner.ApplyAsync(RouteAction());

        Assert.False(refusal.Succeeded);
        Assert.StartsWith("Маршрут не изменён", refusal.MessageRu, StringComparison.Ordinal);
        Assert.Contains("Прямой путь короче", refusal.MessageRu, StringComparison.Ordinal); // факт замера, а не отговорка «узла нет»
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task Автовыбор_с_выигрышем_ведёт_через_победителя_и_мерит_один_раз_на_сессию()
    {
        FakeRouteSystem system = new();
        ScriptedMeasurement probe = new(system, hop => hop is null ? Window(40) : Window(30));
        var comparison = new RoutePathComparisonService(system.Command, system.Reader, probe);

        RouteOverride? target = null;
        var provider = new RoutingPathOptionsProvider(
            () => new RoutingSettings([Node("10.8.0.1", "WG")], null, AutoSelectBest: true),
            value => target = value,
            note => { },
            comparison);

        await provider.GetOptionsAsync(Flow());
        Assert.Equal("10.8.0.1", target!.NextHop);
        int probesAfterStart = probe.Count;

        // Контроль качества при деградации спрашивает варианты снова — решение переиспользуется,
        // сеть не должна получать полное сравнение на каждый тик.
        await provider.GetOptionsAsync(Flow());
        Assert.Equal(probesAfterStart, probe.Count);

        // Новая сессия обязана мерить заново: вчерашняя победа — не сегодняшняя.
        provider.Invalidate();
        await provider.GetOptionsAsync(Flow());
        Assert.True(probe.Count > probesAfterStart);

        // И после всего таблица такая же, какой была (пустой).
        Assert.Empty(system.Table.Entries);
    }

    [Fact]
    public async Task Мусор_вместо_адреса_в_настройках_не_доходит_до_маршрутизации()
    {
        FakeRouteSystem system = new();
        RouteOverride? target = new("стоял");

        var provider = new RoutingPathOptionsProvider(
            () => new RoutingSettings([new RoutingNodeEntry("не-ip", "битый", RoutingNodeSourceRu.Manual)], "не-ip", AutoSelectBest: false),
            value => target = value,
            note => { },
            comparison: null,
            applyFlow: _ => { });

        await provider.GetOptionsAsync(Flow());

        Assert.Null(target);
        Assert.Empty(system.Command.Invocations);
    }

    [Fact]
    public async Task Без_службы_автовыбор_не_угадывает_а_честно_отказывается()
    {
        RouteOverride? target = new("предыдущая цель");
        string? note = null;

        var provider = new RoutingPathOptionsProvider(
            () => new RoutingSettings([Node("10.8.0.1", "WG")], null, AutoSelectBest: true),
            value => target = value,
            written => note = written,
            comparison: null);

        await provider.GetOptionsAsync(Flow());

        Assert.Null(target);
        Assert.Contains("не меняется", note, StringComparison.Ordinal);
    }

    // ── служба: каталог, права, каналы ─────────────────────────────────────────────────────────

    [Fact]
    public void Каталог_новых_команд_discovery_только_читает_measure_меняет()
    {
        EngineCommandDescriptor discover = EngineCommandCatalog.Get(EngineCommandKind.RouteDiscover);
        EngineCommandDescriptor measure = EngineCommandCatalog.Get(EngineCommandKind.RouteMeasure);

        Assert.Equal("route-discover", discover.WireName);
        Assert.False(discover.NeedsElevation);

        Assert.Equal("route-measure", measure.WireName);
        Assert.True(measure.NeedsElevation);
        Assert.True(measure.TimeoutSeconds >= 120, "замер путей — долгая команда, короткий таймаут срезал бы её на полпути");

        // Поиск узлов — только чтение, он разрешён и в канале просмотра. Измерение меняет таблицу — основной канал.
        Assert.Contains(EngineCommandKind.RouteDiscover, EngineCommandCatalog.ReadOnlyChannelKinds);
        Assert.DoesNotContain(EngineCommandKind.RouteMeasure, EngineCommandCatalog.ReadOnlyChannelKinds);
    }

    [Fact]
    public async Task Без_подключенного_слоя_маршрутизации_команды_отказывают_а_не_делают_вид()
    {
        // Диспетчер с двойником, который новых методов не реализует, — как сборка службы без сетевого слоя.
        var dispatcher = new EngineDispatcher(new DimOnlyOps(), EngineChannelKind.Main);

        EngineResponse discover = await dispatcher.HandleJsonAsync("""{"id":"1","command":"route-discover"}""");
        Assert.False(discover.Ok);
        Assert.Contains("не подключён", discover.MessageRu, StringComparison.Ordinal);

        EngineResponse measure = await dispatcher.HandleJsonAsync("""{"id":"2","command":"route-measure"}""");
        Assert.False(measure.Ok);
        Assert.Contains("не подключён", measure.MessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Канал_чтения_разрешает_поиск_узлов_и_запрещает_замер_путей()
    {
        var ops = new RecordingOps();
        var readOnly = new EngineDispatcher(ops, EngineChannelKind.ReadOnly);

        EngineResponse discover = await readOnly.HandleJsonAsync("""{"id":"1","command":"route-discover"}""", clientIsAdministrator: false);
        Assert.True(discover.Ok, discover.MessageRu);
        Assert.Contains("route-discover", ops.Calls);

        // Даже администратор в канале чтения изменение маршрутов не получит.
        EngineResponse measure = await readOnly.HandleJsonAsync("""{"id":"2","command":"route-measure"}""", clientIsAdministrator: true);
        Assert.False(measure.Ok);
        Assert.Contains("только для чтения", measure.MessageRu, StringComparison.Ordinal);
        Assert.DoesNotContain("route-measure", ops.Calls);
    }

    [Fact]
    public async Task Меняющую_команду_без_прав_окно_получает_объяснение_а_не_тишину()
    {
        var main = new EngineDispatcher(new RecordingOps(), EngineChannelKind.Main);

        EngineResponse denied = await main.HandleJsonAsync("""{"id":"1","command":"route-measure"}""", clientIsAdministrator: false);
        Assert.False(denied.Ok);
        Assert.Contains("только для администратора", denied.MessageRu, StringComparison.Ordinal);
        Assert.Contains("администратора", denied.MessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Операции_службы_пробрасывают_ответ_слоя_маршрутизации_без_подмены()
    {
        EngineOperationResult stubbed = EngineOperationResult.Success(
            "Найдено узлов: 1, из них альтернатив текущему шлюзу: 0.",
            RoutingWireCodec.EncodeCandidates([new RoutingCandidateDto("192.168.1.1", "Wi-Fi", RoutingNodeSourceRu.CurrentGateway, true)]));

        LiveEngineOperations withRouting = BuildOperations(new StubRouting(stubbed));

        EngineOperationResult discover = await withRouting.DiscoverRoutingNodesAsync();
        Assert.True(discover.Ok);
        IReadOnlyList<RoutingCandidateDto> decoded = RoutingWireCodec.DecodeCandidates(discover.DataJson);
        Assert.Single(decoded);
        Assert.Equal("192.168.1.1", decoded[0].Address);
        Assert.True(decoded[0].IsCurrentGateway);

        // Сборка без слоя маршрутизации (null) — честный отказ, а не пустой успех.
        LiveEngineOperations without = BuildOperations(routing: null);
        EngineOperationResult measure = await without.MeasureRoutingPathsAsync();
        Assert.False(measure.Ok);
        Assert.Contains("не подключён", measure.MessageRu, StringComparison.Ordinal);

        static LiveEngineOperations BuildOperations(IRoutingService? routing) => new(
            new FakeTweakCatalog([]),
            new SnapshotDouble(),
            new CoordinatorDouble(),
            isElevationAvailable: true,
            routing: routing);
    }

    // ── B3. окно: блок «Сервер маршрутизации» ─────────────────────────────────────────────────

    [Fact]
    public async Task Окно_добавляет_узел_и_не_сохраняет_мусор()
    {
        using RoutingHarness harness = new();

        await harness.Routing.AddNodeAsync("10.8.0.1", "домашний WG");
        RoutingNodeRowViewModel row = Assert.Single(harness.Routing.Rows);
        Assert.Equal("10.8.0.1", row.Address);
        Assert.Equal(RoutingNodeSourceRu.Manual, row.SourceRu);
        Assert.True(row.IsDefault); // первый узел становится по умолчанию — иначе выбор «ничего»
        Assert.Equal(1, harness.Saves);

        await harness.Routing.AddNodeAsync("это-не-ip", null);
        Assert.Single(harness.Routing.Rows);
        Assert.Contains("не добавлен", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Equal(1, harness.Saves); // мусор не сохранился вторым разом

        harness.Routing.AutoSelectBest = true;
        Assert.True(harness.Store.Load().RoutingAutoSelect);
        Assert.Contains("Оптимизировать", harness.Routing.StatusRu, StringComparison.Ordinal); // текст статуса объясняет, когда что применится
    }

    [Fact]
    public async Task Окно_добавляет_узел_из_полей_ввода_и_чистит_их()
    {
        using RoutingHarness harness = new();

        harness.Routing.AddAddress = "10.0.0.2";
        harness.Routing.AddLabel = "второй канал";
        await harness.Routing.AddNodeCommand.ExecuteAsync();

        RoutingNodeRowViewModel row = Assert.Single(harness.Routing.Rows);
        Assert.Equal("10.0.0.2", row.Address);
        Assert.Equal("второй канал", row.Name);
        Assert.Equal(string.Empty, harness.Routing.AddAddress);
        Assert.Equal(string.Empty, harness.Routing.AddLabel);
    }

    [Fact]
    public async Task Окно_ищет_шлюзы_через_службу_склейка_не_теряет_медиану_и_подписи()
    {
        using RoutingHarness harness = new();
        await harness.Routing.AddNodeAsync("10.8.0.1", "домашний WG");
        harness.Routing.Rows[0].ApplyMeasurement(28, 2, 0, "WG");

        harness.NextDiscovery = (
            "Найдено узлов: 3, из них альтернатив текущему шлюзу: 2.",
            RoutingWireCodec.EncodeCandidates(
            [
                new RoutingCandidateDto("192.168.1.1", "Wi-Fi", RoutingNodeSourceRu.CurrentGateway, true),
                new RoutingCandidateDto("10.8.0.1", "WireGuard", RoutingNodeSourceRu.VpnAdapter("WireGuard"), false),
                new RoutingCandidateDto("10.0.0.1", "Ethernet", RoutingNodeSourceRu.SecondGateway, false),
            ]));

        await harness.Routing.FindGatewaysAsync();

        Assert.Equal(3, harness.Routing.Rows.Count);
        RoutingNodeRowViewModel wg = harness.Routing.Rows.Single(row => row.Address == "10.8.0.1");
        Assert.Equal("28 мс", wg.MedianRu);                    // медиана сохранённого узла не потерялась
        Assert.Equal(RoutingNodeSourceRu.Manual, wg.SourceRu); // и подпись человека тоже
        Assert.Equal(RoutingNodeSourceRu.CurrentGateway, harness.Routing.Rows.Single(row => row.Address == "192.168.1.1").SourceRu);
        Assert.Contains("альтернатив", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Equal(2, harness.Saves);
    }

    [Fact]
    public async Task Исчезнувший_системный_шлюз_уходит_из_списка_ручной_остается()
    {
        using RoutingHarness harness = new();

        // Так выглядит файл после прошлого поиска: два системных узла и один рукописный.
        harness.Store.Save(new AppSettings
        {
            RoutingNodes =
            [
                new RoutingNodeEntry("192.168.1.1", "Wi-Fi", RoutingNodeSourceRu.CurrentGateway),
                new RoutingNodeEntry("10.0.0.1", "Ethernet", RoutingNodeSourceRu.SecondGateway),
                new RoutingNodeEntry("10.8.0.1", "мой", RoutingNodeSourceRu.Manual),
            ],
            RoutingDefaultNode = "10.0.0.1",
        });

        RoutingSettingsViewModel routing = harness.RebuildRouting();
        harness.NextDiscovery = (
            "Найдено узлов: 1, из них альтернатив текущему шлюзу: 0.",
            RoutingWireCodec.EncodeCandidates([new RoutingCandidateDto("192.168.1.1", "Wi-Fi", RoutingNodeSourceRu.CurrentGateway, true)]));

        await routing.FindGatewaysAsync();

        Assert.Equal(2, routing.Rows.Count); // висячий «второй шлюз» убран, рукописный остался
        Assert.Contains(routing.Rows, row => row.Address == "10.8.0.1");
        Assert.DoesNotContain(routing.Rows, row => row.Address == "10.0.0.1");
    }

    [Fact]
    public async Task Отказ_службы_при_поиске_виден_в_окне_список_не_меняется()
    {
        using RoutingHarness harness = new();
        await harness.Routing.AddNodeAsync("10.8.0.1", "мой");

        harness.DiscoveryOk = false;
        harness.NextDiscovery = ("route print не запущен: таблица маршрутов недоступна.", null);

        await harness.Routing.FindGatewaysAsync();

        Assert.Contains("Поиск шлюзов не получился", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Single(harness.Routing.Rows);
    }

    [Fact]
    public async Task Окно_измеряет_через_службу_после_подтверждения_и_помечает_лучшего()
    {
        using RoutingHarness harness = new();
        await harness.Routing.AddNodeAsync("10.8.0.1", "WG");
        await harness.Routing.AddNodeAsync("10.0.0.1", "второй");

        RoutingMeasureDto measure = new(
            GameServer,
            40,
            2,
            0,
            null,
            [
                new RoutingMeasureNodeDto("10.8.0.1", "WG", RoutingNodeSourceRu.Manual, true, 30, 1.5, 0, null),
                new RoutingMeasureNodeDto("10.0.0.1", "второй", RoutingNodeSourceRu.SecondGateway, false, null, null, null, "узел молчит"),
            ],
            "10.8.0.1",
            "Через «WG» (10.8.0.1) медиана 30 мс против 40 мс напрямую — выигрыш 10 мс. Этот путь можно применять.",
            true,
            null);

        harness.NextMeasure = ("лучший найден", RoutingWireCodec.EncodeMeasure(measure));

        await harness.Routing.MeasureAllCommand.ExecuteAsync();

        // Подтверждение человека было — измерение трогает таблицу маршрутов, пусть и на секунды.
        (string title, string message, string yes, string no) = Assert.Single(harness.Dialogs.Confirmations);
        Assert.Equal("Измерить пути", title);
        Assert.Contains("/32", message, StringComparison.Ordinal);
        Assert.Equal("Измерить", yes);

        Assert.Equal("30 мс", harness.Routing.Rows.Single(row => row.Address == "10.8.0.1").MedianRu);
        Assert.Equal("не измерялась", harness.Routing.Rows.Single(row => row.Address == "10.0.0.1").MedianRu); // честное «нет числа», а не выдуманный ноль
        Assert.True(harness.Routing.Rows.Single(row => row.Address == "10.8.0.1").IsDefault);
        Assert.Contains("выигрыш 10 мс", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Contains("Оптимизировать", harness.Routing.StatusRu, StringComparison.Ordinal); // применяет не замер, а кнопка

        AppSettings saved = harness.Store.Load();
        Assert.Equal(30, saved.RoutingNodes!.Single(node => node.Address == "10.8.0.1").MedianRttMs!.Value, 0);
        Assert.Equal("10.8.0.1", saved.RoutingDefaultNode);
    }

    [Fact]
    public async Task Отказ_подтверждения_—_ничего_не_отправлено_и_маршруты_не_тронуты()
    {
        using RoutingHarness harness = new();
        await harness.Routing.AddNodeAsync("10.8.0.1", "WG");
        int savesBefore = harness.Saves;

        harness.Dialogs.ConfirmAnswer = false;

        await harness.Routing.MeasureAllCommand.ExecuteAsync();

        Assert.DoesNotContain(harness.Channel.Requests, request => request.Command == EngineCommandKind.RouteMeasure);
        Assert.Contains("маршруты не тронуты", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Equal(savesBefore, harness.Saves);
    }

    [Fact]
    public async Task Пустой_список_измерять_нечего_до_диалога_не_доходит()
    {
        using RoutingHarness harness = new();

        await harness.Routing.MeasureAllAsync();

        Assert.Contains("Измерять нечего", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Empty(harness.Dialogs.Confirmations);
        Assert.Empty(harness.Channel.Requests.Where(request => request.Command == EngineCommandKind.RouteMeasure));
    }

    [Fact]
    public async Task Игра_не_найдена_—_ответ_службы_видно_в_окне_как_есть()
    {
        using RoutingHarness harness = new();
        await harness.Routing.AddNodeAsync("10.8.0.1", "WG");

        harness.MeasureOk = false;
        harness.NextMeasure = ("Запущенная игра не найдена: замер путей делается только до адреса сервера игры.", null);

        await harness.Routing.MeasureAllCommand.ExecuteAsync();

        Assert.Contains("Запущенная игра не найдена", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Equal("не измерялась", harness.Routing.Rows[0].MedianRu);
    }

    [Fact]
    public async Task Служба_сообщила_что_кандидатов_кроме_шлюза_нет_окно_показывает_это_без_краски()
    {
        using RoutingHarness harness = new();

        harness.NextDiscovery = (
            "Кандидатов кроме текущего шлюза (172.29.16.1) нет: второго канала или VPN-адаптера в системе не найдено. " +
            "Сравнивать пути имеет смысл только с реальными альтернативами.",
            RoutingWireCodec.EncodeCandidates([new RoutingCandidateDto("172.29.16.1", "Ethernet", RoutingNodeSourceRu.CurrentGateway, true)]));

        await harness.Routing.FindGatewaysAsync();

        Assert.Contains("Кандидатов кроме текущего шлюза", harness.Routing.StatusRu, StringComparison.Ordinal);
        Assert.Single(harness.Routing.Rows);
        Assert.Equal(RoutingNodeSourceRu.CurrentGateway, harness.Routing.Rows[0].SourceRu);
    }

    [Fact]
    public async Task Снять_выбор_узла_и_удалить_строку_команды_работают_без_магии_XAML()
    {
        using RoutingHarness harness = new();
        await harness.Routing.AddNodeAsync("10.8.0.1", "первый");
        await harness.Routing.AddNodeAsync("10.0.0.2", "второй");

        // Кнопки строк — обычные команды: ровно то, на что подписаны в разметке.
        harness.Routing.Rows[1].MakeDefaultCommand.Execute(null);
        Assert.True(harness.Routing.Rows[1].IsDefault);
        Assert.False(harness.Routing.Rows[0].IsDefault);
        Assert.Equal("10.0.0.2", harness.Store.Load().RoutingDefaultNode);

        harness.Routing.Rows[1].MakeDefaultCommand.Execute(null); // повторное нажатие снимает выбор
        Assert.False(harness.Routing.Rows[1].IsDefault);
        Assert.Null(harness.Store.Load().RoutingDefaultNode);

        harness.Routing.Rows[0].RemoveCommand.Execute(null);
        RoutingNodeRowViewModel left = Assert.Single(harness.Routing.Rows);
        Assert.Equal("10.0.0.2", left.Address);
        Assert.Contains("Маршруты в системе не менялись", harness.Routing.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Текст_блока_честный_третья_сторона_destiny_dns_и_кнопка()
    {
        string warning = RoutingSettingsViewModel.WarningRu;

        Assert.Contains("третью сторону", warning, StringComparison.Ordinal);
        Assert.Contains("Destiny 2", warning, StringComparison.Ordinal);
        Assert.Contains("решайте осознанно", warning, StringComparison.Ordinal);
        Assert.Contains("не включает молча", warning, StringComparison.Ordinal);
        Assert.Contains("«Оптимизировать»", warning, StringComparison.Ordinal);
        Assert.Contains("/32", warning, StringComparison.Ordinal);
        Assert.Contains("DNS ускоряет вход в игру, а не пинг в матче", warning, StringComparison.Ordinal);

        // Ни обещаний «нашу сеть подключим», ни «ускорителей», ни «ping 0».
        Assert.DoesNotContain("нашу сеть", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ускорител", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("пинг 0", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Настройки_блока_переживают_перезапуск_окна()
    {
        using RoutingHarness harness = new();

        await harness.Routing.AddNodeAsync("10.8.0.1", "домашний WG");
        harness.Routing.MakeDefault(harness.Routing.Rows[0]);
        harness.Routing.AutoSelectBest = true;

        RoutingSettingsViewModel second = harness.RebuildRouting();
        RoutingNodeRowViewModel row = Assert.Single(second.Rows);
        Assert.Equal("10.8.0.1", row.Address);
        Assert.True(row.IsDefault);
        Assert.True(second.AutoSelectBest);
        Assert.Equal("домашний WG", row.Name);
        Assert.Equal("не измерялась", row.MedianRu);
    }

    [Fact]
    public void Блок_живёт_в_Настройках_через_модель_вьюмы_вью()
    {
        // SettingsViewModel обязан выставлять наружу Routing — иначе XAML-блок ни к чему не привязан.
        Assert.NotNull(typeof(SettingsViewModel).GetProperty("Routing"));
        Assert.NotNull(typeof(RoutingSettingsViewModel).GetProperty("Rows"));

        // …а разметка настроек действительно содержит блок с привязками (текстовая проверка разметки —
        // живую отрисовку проверяет SelfCheck на Windows-раннере).
        string xaml = File.ReadAllText(FindRepositoryFile(Path.Combine("src", "PingForge.App", "Views", "SettingsView.xaml")));
        Assert.Contains("RoutingBlock", xaml, StringComparison.Ordinal);
        Assert.Contains("Сервер маршрутизации", xaml, StringComparison.Ordinal);
        Assert.Contains("Найти шлюзы", xaml, StringComparison.Ordinal);
        Assert.Contains("Измерить все пути", xaml, StringComparison.Ordinal);
        Assert.Contains("выбирать лучший автоматически", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding Routing}", xaml, StringComparison.Ordinal);
    }

    // ── вспомогательное ─────────────────────────────────────────────────────────────────────────

    private static PathMeasurement Window(double median, double jitter = 1, double loss = 0) =>
        new(true, median, jitter, loss, 12, "ICMP-эхо до игрового сервера");

    private static RoutingNodeEntry Node(string address, string name = "узел", string? source = null) =>
        new(address, name, source ?? RoutingNodeSourceRu.SecondGateway);

    private static OptimizationActionDescriptor RouteAction() => new(
        "route:game-server", OptimizationActionKind.RouteForGame, "Маршрут для игрового сервера", "Через выбранный узел");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static string FindRepositoryFile(string relative)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Не найден файл репозитория {relative} от {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Подменяемый route-бэкенд: таблица маршрутов и команда <c>route</c> живут в памяти.
    /// Та же поверхность, что у настоящей Windows (ICommandPort + IRouteTableReader), поэтому
    /// сравнение путей, применение и откат проверяются без привилегий и без реальной системы.
    /// </summary>
    private sealed class FakeRouteSystem
    {
        public FakeRouteSystem()
        {
            Reader = new FakeRouteReader(this);

            Command.Handler = (tool, args) =>
            {
                Assert.Equal(RouteCommandBuilder.RouteTool, tool);

                if (args.Count >= 5 && args[0] == "add")
                {
                    if (ConfirmAdds)
                    {
                        Table.Add(new RouteTableEntry(args[1], args[3], args[4], "192.168.1.50", int.Parse(args[6])));
                    }

                    return AddResult;
                }

                if (args.Count >= 2 && args[0] == "delete")
                {
                    Table.RemoveAll(entry => entry.Destination == args[1] && entry.IsHostRoute);
                    return CommandResult.Ok(string.Empty);
                }

                return CommandResult.Ok(string.Empty);
            };
        }

        public FakeCommandPort Command { get; } = new();

        public List<RouteTableEntry> Table { get; } = [];

        public FakeRouteReader Reader { get; }

        /// <summary>false — «команда прошла, а маршрута в таблице нет»: политика безопасности, чужой клиент VPN.</summary>
        public bool ConfirmAdds { get; set; } = true;

        public CommandResult AddResult { get; set; } = CommandResult.Ok(string.Empty);

        public sealed class FakeRouteReader : IRouteTableReader
        {
            public FakeRouteReader(FakeRouteSystem owner) => _owner = owner;

            private readonly FakeRouteSystem _owner;

            public int ReadCount { get; private set; }

            public Task<IReadOnlyList<RouteTableEntry>> ReadAsync(CancellationToken ct = default)
            {
                ReadCount++;
                return Task.FromResult<IReadOnlyList<RouteTableEntry>>(_owner.Table.ToList());
            }
        }
    }

    /// <summary>
    /// Замерщик с программой ответов. «Через какой узел» идёт путь, он определяет по таблице
    /// двойников: если к цели стоит единственный /32 — замер «через» его NextHop, если нет — прямой путь.
    /// Так ответы fake привязаны к реальному состоянию маршрутизации в тесте, а не к счётчику вызовов.
    /// </summary>
    private sealed class ScriptedMeasurement : IRealtimeMeasurementSource
    {
        private readonly FakeRouteSystem _system;
        private readonly Func<string?, PathMeasurement> _program;

        public ScriptedMeasurement(FakeRouteSystem system, Func<string?, PathMeasurement> program)
        {
            _system = system;
            _program = program;
        }

        public int Count { get; private set; }

        public string? ThrowOnHop { get; init; }

        public Task<PathMeasurement> MeasurePathAsync(string endpoint, int port, CancellationToken ct = default)
        {
            Count++;

            string? viaHop = RouteTableParser.FindExactHostRoute(_system.Table, endpoint)?.NextHop;
            if (viaHop is not null && viaHop == ThrowOnHop)
            {
                throw new InvalidOperationException("оборвался замер");
            }

            return Task.FromResult(_program(viaHop));
        }
    }

    private sealed class RoutingHarness : IDisposable
    {
        private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"pf_routing_ui_{Guid.NewGuid():N}.json");

        public RoutingHarness()
        {
            var inner = new JsonFileSettingsStore(_filePath);
            Store = new CountingStore(inner);
            Dialogs = new FakeDialogs();
            Channel = new FakeEngineChannel(request => request.Command switch
            {
                EngineCommandKind.RouteDiscover => NextDiscovery is { } discovery
                    ? DiscoveryOk
                        ? EngineResponse.Success(request.Id, discovery.Message, discovery.Data)
                        : EngineResponse.Failure(request.Id, discovery.Message)
                    : EngineResponse.Failure(request.Id, "Служба не ответила на поиск шлюзов."),
                EngineCommandKind.RouteMeasure => NextMeasure is { } measure
                    ? MeasureOk
                        ? EngineResponse.Success(request.Id, measure.Message, measure.Data)
                        : EngineResponse.Failure(request.Id, measure.Message)
                    : EngineResponse.Failure(request.Id, "Служба не ответила на замер путей."),
                _ => EngineResponse.Success(request.Id, "ок"),
            });
            RebuildRouting();
        }

        public FakeEngineChannel Channel { get; }

        public FakeDialogs Dialogs { get; }

        public CountingStore Store { get; }

        public RoutingSettingsViewModel Routing { get; private set; }

        public (string Message, string? Data)? NextDiscovery { get; set; }

        public bool DiscoveryOk { get; set; } = true;

        public (string Message, string? Data)? NextMeasure { get; set; }

        public bool MeasureOk { get; set; } = true;

        /// <summary>Сколько раз настройки реально записывались — по счётчику обёртки, а не по догадкам.</summary>
        public int Saves => Store.Saves;

        public RoutingSettingsViewModel RebuildRouting() =>
            Routing = new RoutingSettingsViewModel(new EngineClient(Channel), Store, Dialogs);

        public void Dispose() => TryDelete(_filePath);
    }

    /// <summary>Хранилище-обёртка со счётчиком сохранений: «мусор не сохранился» должно быть чем-то измеримо.</summary>
    private sealed class CountingStore(JsonFileSettingsStore inner) : IAppSettingsStore
    {
        public int Saves { get; private set; }

        public string SettingsFilePath => inner.SettingsFilePath;

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings)
        {
            Saves++;
            inner.Save(settings);
        }
    }

    private sealed class StubRouting(EngineOperationResult result) : IRoutingService
    {
        public Task<EngineOperationResult> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(result);

        public Task<EngineOperationResult> MeasureAsync(CancellationToken ct = default) => Task.FromResult(result);
    }

    /// <summary>Двойник операций, который НОВЫХ методов не реализует: так проверяется поведение «слой не подключён».</summary>
    private sealed class DimOnlyOps : IEngineOperations
    {
        public bool IsElevationAvailable => true;

        public Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> StatusAsync(CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> ApplyPresetAsync(TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default) => Done();
        public Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default) => Done();

        private static Task<EngineOperationResult> Done() => Task.FromResult(EngineOperationResult.Success("ок"));
    }

    /// <summary>Двойник операций С routing-переопределениями: проверяет, что диспетчер вызывает их.</summary>
    private sealed class RecordingOps : IEngineOperations
    {
        public List<string> Calls { get; } = [];

        public bool IsElevationAvailable => true;

        public Task<EngineOperationResult> DiscoverRoutingNodesAsync(CancellationToken ct = default)
        {
            Calls.Add("route-discover");
            return Task.FromResult(EngineOperationResult.Success("узлы найдены"));
        }

        public Task<EngineOperationResult> MeasureRoutingPathsAsync(CancellationToken ct = default)
        {
            Calls.Add("route-measure");
            return Task.FromResult(EngineOperationResult.Success("пути измерены"));
        }

        public Task<EngineOperationResult> PingAsync(EnginePeerIdentity? peer = null, CancellationToken ct = default) => Done("ping");
        public Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default) => Done("capabilities");
        public Task<EngineOperationResult> StatusAsync(CancellationToken ct = default) => Done("status");
        public Task<EngineOperationResult> ApplyPresetAsync(TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default) => Done("apply-preset");
        public Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default) => Done("apply-tweaks");
        public Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default) => Done("rollback");
        public Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default) => Done("repair-network");
        public Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default) => Done("journal-tail");
        public Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default) => Done("realtime");
        public Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default) => Done("perf-profile");
        public Task<EngineOperationResult> ReadSystemStateAsync(CancellationToken ct = default) => Done("system-state");

        private Task<EngineOperationResult> Done(string name)
        {
            Calls.Add(name);
            return Task.FromResult(EngineOperationResult.Success(name));
        }
    }

    private sealed class SnapshotDouble : ISnapshotService
    {
        public Task<(SystemSnapshot Snapshot, RestorePointResultKind RestorePointStatus, string RestorePointMessageRu)>
            CreateSnapshotAsync(string label, TweakProfilePreset preset, IReadOnlyList<ITweak> tweaks, bool requestWindowsRestorePoint, CancellationToken ct = default) =>
            throw new NotSupportedException("В этих тестах снапшоты не создаются.");

        public Task<SnapshotRollbackReport> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default) =>
            throw new NotSupportedException("В этих тестах откат не выполняется.");

        public Task<IReadOnlyList<SystemSnapshot>> GetAllSnapshotsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemSnapshot>>([]);
    }

    private sealed class CoordinatorDouble : ITweakCoordinator
    {
        public Task<TweakBatchReport> ExecuteBatchAsync(
            string batchTitle,
            TweakProfilePreset preset,
            IReadOnlyList<ITweak> selectedTweaks,
            bool requestWindowsRestorePoint,
            CancellationToken ct = default) =>
            throw new NotSupportedException("В этих тестах пачки твиков не выполняются.");

        public Task<int> RecoverInterruptedOperationsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("В этих тестах восстановление не нужно.");
    }
}
