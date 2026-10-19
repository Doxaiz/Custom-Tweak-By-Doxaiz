using System.Net;
using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Probing;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Задача G: «Проверить сеть» одной кнопкой. Правила, которые держит этот набор:
///   • у каждого пункта — честные статусы, включая «проверить нельзя» и «не применимо»;
///   • bufferbloat без согласия не запускается: нагрузка начинается только кнопкой «Понятно»;
///   • «применить рекомендованное» не применит того, чего нет в каталоге, а при пустых
///     рекомендациях честно говорит «ничего не предлагаем» и кнопки нет;
///   • числа в итогах — только те, что пришли из замеров; неизмеренное не становится нулём.
/// </summary>
public sealed class NetworkSelfCheckTests
{
    private static readonly string[] CatalogCodes = ["MTU_SUBINTERFACE_OPTIMAL", "DNS_SERVERS_FASTEST"];

    // ── Двойники ─────────────────────────────────────────────────────────────────────────────

    private sealed class FakeRunner : INetworkSelfCheck
    {
        public int RunCalls { get; private set; }

        public int BufferbloatCalls { get; private set; }

        public NetworkSelfCheckReport? Report { get; set; }

        public NetworkCheckItemResult? Bufferbloat { get; set; }

        public Exception? Throw { get; set; }

        public Task<NetworkSelfCheckReport> RunAsync(CancellationToken ct = default)
        {
            RunCalls++;
            if (Throw is not null)
            {
                return Task.FromException<NetworkSelfCheckReport>(Throw);
            }

            return Task.FromResult(Report ?? MixedReport());
        }

        public Task<NetworkCheckItemResult> RunBufferbloatAsync(CancellationToken ct = default)
        {
            BufferbloatCalls++;
            return Task.FromResult(Bufferbloat ?? Item(NetworkCheckItemKind.Bufferbloat, NetworkCheckStatus.Ok));
        }
    }

    private static NetworkCheckItemResult Item(
        NetworkCheckItemKind kind,
        NetworkCheckStatus status,
        NetworkCheckPriority priority = NetworkCheckPriority.Info,
        string? tweakCode = null) =>
        new(kind, kind.ToString(), status, priority, "деталь замера", "действие", tweakCode);

    private static NetworkSelfCheckReport MixedReport() => new(
    [
        Item(NetworkCheckItemKind.Mtu, NetworkCheckStatus.Attention, NetworkCheckPriority.Medium, "MTU_SUBINTERFACE_OPTIMAL"),
        Item(NetworkCheckItemKind.Dns, NetworkCheckStatus.Ok),
        Item(NetworkCheckItemKind.DoubleNat, NetworkCheckStatus.CannotCheck),
        Item(NetworkCheckItemKind.Wifi, NetworkCheckStatus.NotApplicable),
        Item(NetworkCheckItemKind.BackgroundDownloads, NetworkCheckStatus.Ok),
        Item(NetworkCheckItemKind.RightsAndService, NetworkCheckStatus.Ok),
    ]);

    private static NetworkCheckViewModel Create(FakeRunner runner, List<string>? applied = null) =>
        new(runner, codes => applied?.AddRange(codes), CatalogCodes);

    // ── Модель окна: кнопка, согласие, рекомендации ──────────────────────────────────────────

    [Fact]
    public void До_нажатия_кнопки_замеров_нет_и_статус_честный()
    {
        var runner = new FakeRunner();
        NetworkCheckViewModel model = Create(runner);

        Assert.Equal(0, runner.RunCalls);
        Assert.Empty(model.Items);
        Assert.False(model.HasResult);
        Assert.False(model.HasRecommendations);
        Assert.Contains("ещё не запускалась", model.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Прогон_даёт_семь_пунктов_а_bufferbloat_ждёт_согласия()
    {
        var runner = new FakeRunner();
        NetworkCheckViewModel model = Create(runner);

        await model.CheckCommand.ExecuteAsync();

        Assert.Equal(7, model.Items.Count);
        Assert.Equal(
            [NetworkCheckItemKind.Mtu, NetworkCheckItemKind.Dns, NetworkCheckItemKind.DoubleNat,
             NetworkCheckItemKind.Bufferbloat, NetworkCheckItemKind.Wifi,
             NetworkCheckItemKind.BackgroundDownloads, NetworkCheckItemKind.RightsAndService],
            model.Items.Select(i => i.Result.Kind).ToArray());

        NetworkCheckItemViewModel bloat = model.Items.Single(i => i.Result.Kind == NetworkCheckItemKind.Bufferbloat);
        Assert.Equal(NetworkCheckStatus.NotChecked, bloat.Result.Status);
        Assert.True(bloat.NeedsConsent);
        Assert.Contains("на 10 секунд займём канал", bloat.DetailRu, StringComparison.Ordinal);
        Assert.Equal(0, runner.BufferbloatCalls);   // главное: нагрузка без согласия не началась

        Assert.True(model.HasResult);
        Assert.Contains("стоит поправить — 1", model.SummaryRu, StringComparison.Ordinal);
        Assert.Contains(model.FindingsRu, line => line.Contains("Проверено и в порядке", StringComparison.Ordinal));
        Assert.Equal(["MTU_SUBINTERFACE_OPTIMAL"], model.RecommendedCodes);
    }

    [Fact]
    public async Task Согласие_запускает_bufferbloat_ровно_один_раз()
    {
        var runner = new FakeRunner
        {
            Bufferbloat = Item(NetworkCheckItemKind.Bufferbloat, NetworkCheckStatus.Ok),
        };
        NetworkCheckViewModel model = Create(runner);
        await model.CheckCommand.ExecuteAsync();

        NetworkCheckItemViewModel bloat = model.Items.Single(i => i.Result.Kind == NetworkCheckItemKind.Bufferbloat);
        await bloat.ConsentCommand!.ExecuteAsync();

        Assert.Equal(1, runner.BufferbloatCalls);
        NetworkCheckItemViewModel after = model.Items.Single(i => i.Result.Kind == NetworkCheckItemKind.Bufferbloat);
        Assert.Equal(NetworkCheckStatus.Ok, after.Result.Status);
        Assert.False(after.NeedsConsent);
        Assert.Contains("не проверено — 0", model.SummaryRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Рекомендации_сверяются_с_каталогом_и_уходят_в_применение_как_есть()
    {
        var runner = new FakeRunner
        {
            Report = new NetworkSelfCheckReport(
            [
                Item(NetworkCheckItemKind.Mtu, NetworkCheckStatus.Attention, NetworkCheckPriority.Medium, "MTU_SUBINTERFACE_OPTIMAL"),
                Item(NetworkCheckItemKind.Dns, NetworkCheckStatus.Attention, NetworkCheckPriority.Low, "КОДА_ТАКОГО_НЕТ"),
                Item(NetworkCheckItemKind.DoubleNat, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.Wifi, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.BackgroundDownloads, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.RightsAndService, NetworkCheckStatus.Ok),
            ]),
        };
        var applied = new List<string>();
        NetworkCheckViewModel model = Create(runner, applied);
        await model.CheckCommand.ExecuteAsync();

        // Код-призрак отсечён каталогом ещё до кнопки применения.
        Assert.Equal(["MTU_SUBINTERFACE_OPTIMAL"], model.RecommendedCodes);

        model.ApplyRecommendedCommand.Execute(null);
        Assert.Equal(["MTU_SUBINTERFACE_OPTIMAL"], applied);
    }

    [Fact]
    public async Task Нет_рекомендаций_значит_ничего_не_предлагаем_и_кнопки_нет()
    {
        var runner = new FakeRunner
        {
            Report = new NetworkSelfCheckReport(
            [
                Item(NetworkCheckItemKind.Mtu, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.Dns, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.DoubleNat, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.Wifi, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.BackgroundDownloads, NetworkCheckStatus.Ok),
                Item(NetworkCheckItemKind.RightsAndService, NetworkCheckStatus.Ok),
            ]),
        };
        var applied = new List<string>();
        NetworkCheckViewModel model = Create(runner, applied);
        await model.CheckCommand.ExecuteAsync();

        Assert.False(model.HasRecommendations);
        Assert.Contains("ничего не предлагаем", model.RecommendationLineRu, StringComparison.Ordinal);
        Assert.False(model.ApplyRecommendedCommand.CanExecute(null));

        model.ApplyRecommendedCommand.Execute(null);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task Падение_прогона_говорится_честно_и_не_оставляет_вчерашних_результатов()
    {
        var runner = new FakeRunner { Throw = new InvalidOperationException("транспорт недоступен") };
        NetworkCheckViewModel model = Create(runner);

        await model.CheckCommand.ExecuteAsync();

        Assert.Empty(model.Items);
        Assert.False(model.HasResult);
        Assert.Contains("не выполнилась", model.StatusRu, StringComparison.Ordinal);
    }

    // ── Применение через раздел «Твики»: отмечаются ровно рекомендованные ───────────────────

    [Fact]
    public void Выбор_кодов_для_применения_отмечает_ровно_их_и_больше_ничего()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        var tweaks = new TweaksViewModel(
            new EngineClient(new FakeEngineChannel()),
            new FakeDialogs(),
            TweakRowFactory.BuildAll(catalog),
            TweakRowFactory.BuildPresets(catalog));

        int matched = tweaks.SelectCodesForApply(["MTU_SUBINTERFACE_OPTIMAL", "DNS_SERVERS_FASTEST", "КОДА_ТАКОГО_НЕТ"]);

        Assert.Equal(2, matched);
        Assert.Equal(2, tweaks.CheckedCount);
        Assert.All(tweaks.Rows.Where(r => r.IsOn), row =>
            Assert.Contains(row.Code, new[] { "MTU_SUBINTERFACE_OPTIMAL", "DNS_SERVERS_FASTEST" }));
    }

    // ── Оценщик MTU ──────────────────────────────────────────────────────────────────────────

    private static PathMtuDiscoveryResult MtuFound(int optimal) =>
        new(MtuDiscoveryOutcome.OptimalFound, 576, 1500, optimal, optimal - 28, "измерено");

    [Fact]
    public void MTU_совпадает_с_измеренным_значит_в_порядке()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateMtu(MtuFound(1500), 1500, "якоря");

        Assert.Equal(NetworkCheckStatus.Ok, item.Status);
        Assert.Null(item.TweakCode);
    }

    [Fact]
    public void MTU_пути_меньше_настроенного_значит_стоит_поправить_с_числами()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateMtu(MtuFound(1492), 1500, "якоря");

        Assert.Equal(NetworkCheckStatus.Attention, item.Status);
        Assert.Equal("MTU_SUBINTERFACE_OPTIMAL", item.TweakCode);
        Assert.Contains("1500", item.DetailRu, StringComparison.Ordinal);
        Assert.Contains("1492", item.DetailRu, StringComparison.Ordinal);
    }

    [Fact]
    public void MTU_не_измерен_значит_проверить_нельзя()
    {
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateMtu(
                new PathMtuDiscoveryResult(MtuDiscoveryOutcome.UndeterminedIcmpBlocked, 576, 1500, null, null, "ICMP закрыт"),
                1500, "якоря").Status);
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateMtu(null, null, "якоря").Status);
    }

    // ── Оценщик DNS ──────────────────────────────────────────────────────────────────────────

    private static DnsResolverBenchmarkResult Dns(string name, string ip, double? warm, double score, bool hijacked = false, bool reachable = true) =>
        new(name, ip, string.Empty, reachable, 11, reachable ? 11 : 0, warm, warm, warm, reachable ? 100 : 0, score, hijacked, string.Empty);

    [Fact]
    public void DNS_настроенный_и_есть_самый_быстрый_значит_в_порядке()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateDns(
        [
            Dns(NetworkCheckEvaluators.CurrentDnsName, "192.168.1.1", 5, score: 5),
            Dns("Cloudflare DNS", "1.1.1.1", 14, score: 14),
        ]);

        Assert.Equal(NetworkCheckStatus.Ok, item.Status);
        Assert.Null(item.TweakCode);
    }

    [Fact]
    public void DNS_публичный_измеренно_быстрее_значит_рекомендация_и_все_времена_видны()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateDns(
        [
            Dns(NetworkCheckEvaluators.CurrentDnsName, "192.168.1.1", 45, score: 45),
            Dns("Cloudflare DNS", "1.1.1.1", 12, score: 12),
            Dns("Google Public DNS", "8.8.8.8", 20, score: 20),
        ]);

        Assert.Equal(NetworkCheckStatus.Attention, item.Status);
        Assert.Equal("DNS_SERVERS_FASTEST", item.TweakCode);
        Assert.Contains("Cloudflare DNS", item.DetailRu, StringComparison.Ordinal);
        Assert.Contains("Google Public DNS", item.DetailRu, StringComparison.Ordinal);
        Assert.Contains("не зависит", item.DetailRu, StringComparison.Ordinal);   // честное «DNS ≠ пинг в бою»
    }

    [Fact]
    public void DNS_разница_в_пределах_погрешности_менять_не_предлагаем()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateDns(
        [
            Dns(NetworkCheckEvaluators.CurrentDnsName, "192.168.1.1", 17, score: 17),
            Dns("Cloudflare DNS", "1.1.1.1", 12, score: 12),
        ]);

        Assert.Equal(NetworkCheckStatus.Ok, item.Status);
        Assert.Null(item.TweakCode);
    }

    [Fact]
    public void DNS_без_ответов_или_только_перехват_значит_проверить_нельзя()
    {
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateDns([Dns("Cloudflare DNS", "1.1.1.1", null, 0, reachable: false)]).Status);
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateDns([Dns("Cloudflare DNS", "1.1.1.1", 3, 503, hijacked: true)]).Status);
        // Текущего в замере нет — сравнивать не с чем, рекомендации тоже нет, хотя победитель назван.
        NetworkCheckItemResult noCurrent = NetworkCheckEvaluators.EvaluateDns([Dns("Cloudflare DNS", "1.1.1.1", 12, 12)]);
        Assert.Equal(NetworkCheckStatus.CannotCheck, noCurrent.Status);
        Assert.Null(noCurrent.TweakCode);
    }

    // ── Оценщик двойного NAT ─────────────────────────────────────────────────────────────────

    private static RouteHopMetric Hop(int n, string ip) => new(n, ip, 1, false);

    [Fact]
    public void NAT_два_частных_хопа_подряд_это_двойной_NAT()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateDoubleNat(
            ["192.168.1.50"], "192.168.1.1", [Hop(1, "192.168.1.1"), Hop(2, "10.10.0.1")]);

        Assert.Equal(NetworkCheckStatus.Attention, item.Status);
        Assert.Contains("192.168.1.1", item.DetailRu, StringComparison.Ordinal);
        Assert.Contains("10.10.0.1", item.DetailRu, StringComparison.Ordinal);
    }

    [Fact]
    public void NAT_один_частный_хоп_и_дальше_публичная_сеть_значит_в_порядке()
    {
        Assert.Equal(NetworkCheckStatus.Ok,
            NetworkCheckEvaluators.EvaluateDoubleNat(
                ["192.168.1.50"], "192.168.1.1", [Hop(1, "192.168.1.1"), Hop(2, "85.140.0.1")]).Status);
        Assert.Equal(NetworkCheckStatus.Ok,
            NetworkCheckEvaluators.EvaluateDoubleNat(["85.140.1.2"], null, []).Status);
    }

    [Fact]
    public void NAT_CGNAT_адрес_это_NAT_провайдера()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateDoubleNat(["100.64.5.5"], "100.64.0.1", []);

        Assert.Equal(NetworkCheckStatus.Attention, item.Status);
        Assert.Contains("CGNAT", item.DetailRu, StringComparison.Ordinal);
    }

    [Fact]
    public void NAT_без_ответов_хопов_проверить_нельзя()
    {
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateDoubleNat(["192.168.1.50"], null, [RouteHopMetric.Timeout(1)]).Status);
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateDoubleNat([], null, []).Status);
    }

    // ── Wi-Fi: разбор netsh и оценщик ────────────────────────────────────────────────────────

    [Fact]
    public void WiFi_разбор_netsh_считает_сети_и_каналы_на_двух_языках()
    {
        WifiScanResult en = WifiNetworksParser.Parse(
            "SSID 1 : Home\n    BSSID 1 : aa:bb\n         Channel            : 6\n" +
            "SSID 2 : Neigh\n    BSSID 1 : cc:dd\n         Channel            : 6\n" +
            "    BSSID 2 : ee:ff\n         Channel            : 11\n");
        Assert.Equal(2, en.NetworkCount);
        Assert.Equal(2, en.BssidsPerChannel[6]);
        Assert.Equal(1, en.BssidsPerChannel[11]);

        WifiScanResult ru = WifiNetworksParser.Parse("SSID 1 : Dom\n    BSSID 1 : aa:bb\n         Канал            : 1\n");
        Assert.Equal(1, ru.NetworkCount);
        Assert.Equal(1, ru.BssidsPerChannel[1]);

        Assert.Equal(0, WifiNetworksParser.Parse("мусор без сетей").NetworkCount);
    }

    [Fact]
    public void WiFi_проводное_подключение_честно_не_применимо()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateWifi(false, null, null);

        Assert.Equal(NetworkCheckStatus.NotApplicable, item.Status);
        Assert.Equal("Не применимо", item.StatusRu);
    }

    [Fact]
    public void WiFi_шумный_эфир_стоит_поправить_тихий_в_порядке_а_сбой_проверить_нельзя()
    {
        var busy = new WifiScanResult(12, new Dictionary<int, int> { [6] = 7, [1] = 3 });
        NetworkCheckItemResult busyItem = NetworkCheckEvaluators.EvaluateWifi(true, busy, null);
        Assert.Equal(NetworkCheckStatus.Attention, busyItem.Status);
        Assert.Contains("12", busyItem.DetailRu, StringComparison.Ordinal);

        var quiet = new WifiScanResult(2, new Dictionary<int, int> { [36] = 1 });
        Assert.Equal(NetworkCheckStatus.Ok, NetworkCheckEvaluators.EvaluateWifi(true, quiet, null).Status);
        Assert.Equal(NetworkCheckStatus.CannotCheck, NetworkCheckEvaluators.EvaluateWifi(true, null, "netsh молчит").Status);
    }

    // ── Фон и права ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Фон_DO_качает_или_трафик_заметен_значит_стоит_поправить()
    {
        Assert.Equal(NetworkCheckStatus.Attention,
            NetworkCheckEvaluators.EvaluateBackground(true, 3, null, null).Status);
        NetworkCheckItemResult traffic = NetworkCheckEvaluators.EvaluateBackground(false, 0, 12, 1);
        Assert.Equal(NetworkCheckStatus.Attention, traffic.Status);
        Assert.Contains("12", traffic.DetailRu, StringComparison.Ordinal);
        Assert.Contains("не определяет", traffic.DetailRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Фон_тихо_в_порядке_а_без_данных_проверить_нельзя()
    {
        Assert.Equal(NetworkCheckStatus.Ok,
            NetworkCheckEvaluators.EvaluateBackground(false, 0, 0, 0).Status);
        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateBackground(null, 0, null, null).Status);
    }

    [Fact]
    public void Права_без_администратора_не_ошибка_а_без_службы_стоит_поправить()
    {
        Assert.Equal(NetworkCheckStatus.Ok,
            NetworkCheckEvaluators.EvaluateRightsAndService(true, true, null).Status);
        NetworkCheckItemResult noAdmin = NetworkCheckEvaluators.EvaluateRightsAndService(false, true, null);
        Assert.Equal(NetworkCheckStatus.Ok, noAdmin.Status);
        Assert.Contains("не ошибка", noAdmin.DetailRu, StringComparison.Ordinal);
        Assert.Equal(NetworkCheckStatus.Attention,
            NetworkCheckEvaluators.EvaluateRightsAndService(true, false, "канал недоступен").Status);
    }

    // ── Bufferbloat: оценщик и сам тестер на двойниках ───────────────────────────────────────

    [Fact]
    public void Bufferbloat_оценщик_считает_рост_из_медиан()
    {
        Assert.Equal(NetworkCheckStatus.Ok,
            NetworkCheckEvaluators.EvaluateBufferbloat(
                new BufferbloatResult(BufferbloatOutcome.Measured, 20, 30, 50_000_000, 10, "пробы")).Status);

        NetworkCheckItemResult high = NetworkCheckEvaluators.EvaluateBufferbloat(
            new BufferbloatResult(BufferbloatOutcome.Measured, 20, 200, 50_000_000, 10, "пробы"));
        Assert.Equal(NetworkCheckStatus.Attention, high.Status);
        Assert.Equal(NetworkCheckPriority.High, high.Priority);
        Assert.Contains("200", high.DetailRu, StringComparison.Ordinal);
        Assert.Contains("SQM", high.ActionRu, StringComparison.Ordinal);

        Assert.Equal(NetworkCheckStatus.CannotCheck,
            NetworkCheckEvaluators.EvaluateBufferbloat(
                new BufferbloatResult(BufferbloatOutcome.LoadFailed, 20, null, 10, 10, "не нагрузилось")).Status);
    }

    private sealed class FakeIcmp : IIcmpProbe
    {
        public Func<int, IcmpProbeReply> ReplyForCall { get; set; } = _ => new IcmpProbeReply(IcmpProbeStatus.Success, 10, null);

        public int Calls { get; private set; }

        public bool RequiresAdministratorRights => false;

        public Task<IcmpProbeReply> SendAsync(IPAddress target, int payloadBytes, bool dontFragment, int ttl, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(ReplyForCall(Calls++));
    }

    private sealed class FakeLoad : ILoadStreamSource
    {
        public long Bytes { get; set; } = 5_000_000;

        public bool Started { get; private set; }

        public async Task<long> DownloadAsync(CancellationToken ct)
        {
            Started = true;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Срок нагрузки — байты при нас, как велит контракт источника.
            }

            return Bytes;
        }
    }

    // Сроки с запасом: на загруженном сборочном раннере Task.Delay(25) легко превращается
    // в полсотни миллисекунд, и слишком короткая фаза честно не набирала минимум проб.
    private static BufferbloatOptions Quick => new(
        IdleDuration: TimeSpan.FromMilliseconds(600),
        LoadDuration: TimeSpan.FromSeconds(3),
        SampleInterval: TimeSpan.FromMilliseconds(10),
        ParallelStreams: 2);

    [Fact]
    public async Task Bufferbloat_тестер_меряет_простой_и_рост_под_нагрузкой()
    {
        var load = new FakeLoad();
        var icmp = new FakeIcmp
        {
            // До старта нагрузки задержка 10 мс, под нагрузкой — 110 мс.
            ReplyForCall = _ => new IcmpProbeReply(IcmpProbeStatus.Success, load.Started ? 110 : 10, null),
        };
        var tester = new BufferbloatLoadTester(icmp, load, Quick);

        BufferbloatResult result = await tester.MeasureAsync(IPAddress.Parse("1.1.1.1"));

        Assert.Equal(BufferbloatOutcome.Measured, result.Outcome);
        Assert.Equal(10.0, result.IdleMedianMs);
        Assert.Equal(110.0, result.LoadedMedianMs);
        Assert.Equal(100.0, result.GrowthMs);
        Assert.Equal(10_000_000L, result.BytesDownloaded);
    }

    [Fact]
    public async Task Bufferbloat_тестер_честно_сдаётся_когда_нагрузить_или_измерить_нельзя()
    {
        var noBytes = new BufferbloatLoadTester(new FakeIcmp(), new FakeLoad { Bytes = 10 }, Quick);
        Assert.Equal(BufferbloatOutcome.LoadFailed,
            (await noBytes.MeasureAsync(IPAddress.Parse("1.1.1.1"))).Outcome);

        var silent = new BufferbloatLoadTester(
            new FakeIcmp { ReplyForCall = _ => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null) },
            new FakeLoad(), Quick);
        Assert.Equal(BufferbloatOutcome.NotEnoughSamples,
            (await silent.MeasureAsync(IPAddress.Parse("1.1.1.1"))).Outcome);

        var denied = new BufferbloatLoadTester(
            new FakeIcmp { ReplyForCall = _ => new IcmpProbeReply(IcmpProbeStatus.PermissionDenied, 0, null) },
            new FakeLoad(), Quick);
        Assert.Equal(BufferbloatOutcome.IcmpBlocked,
            (await denied.MeasureAsync(IPAddress.Parse("1.1.1.1"))).Outcome);
    }

    // ── Сторож разметки ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Сторож_кнопка_проверки_сети_живёт_в_разделе_оптимизации()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-OptimizeView.xaml");
        Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
        string xaml = File.ReadAllText(path);

        Assert.Contains("Проверить сеть", xaml, StringComparison.Ordinal);
        Assert.Contains("NetworkCheck.CheckCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("NetworkCheckCard", xaml, StringComparison.Ordinal);
        Assert.Contains("Применить рекомендованное", xaml, StringComparison.Ordinal);
        Assert.Contains("HasRecommendations", xaml, StringComparison.Ordinal);
        Assert.Contains("ConsentCommand", xaml, StringComparison.Ordinal);   // «Понятно» — согласие на bufferbloat
    }
}
