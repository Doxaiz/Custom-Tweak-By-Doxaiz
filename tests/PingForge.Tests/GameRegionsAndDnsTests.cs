using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Routing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Задача D: регионы игр и честная маркировка DNS.
///
/// Что доказывается без сети и без игр:
///   • у каждой игры справочника в окне видны ровно её регионы и ровно её опубликованные порты;
///   • у игры вне справочника — честное «неизвестно», а не чужие регионы;
///   • DNS подписан как «вход, а не пинг»: ни в одном тексте нет обещания «DNS снизил пинг»;
///   • список резолверов один на «Твики», «Проверить сеть» и совет DNS.
///
/// «Пинга до региона» здесь нет нарочно: адресов дата-центров игры не публикуют, а придумывать
/// числа — прямое нарушение правила «только измеренные значения».
/// </summary>
public sealed class GameRegionsAndDnsTests
{
    private static GameRegionsViewModel Regions => new();

    // ── Регионы и порты ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Все_игры_каталога_попадают_в_окно_со_своими_регионами()
    {
        GameRegionsViewModel regions = Regions;

        Assert.Equal(GameRouteCatalog.Targets.Count, regions.Cards.Count);

        foreach (GameRouteTarget target in GameRouteCatalog.Targets)
        {
            GameRegionCardViewModel? card = regions.Find(target.ProcessName);

            Assert.NotNull(card);
            Assert.Equal(target.GameRu, card!.GameRu);
            Assert.Equal(string.Join("|", target.RegionsRu), string.Join("|", card.RegionsRu));
            Assert.Equal(string.Join("|", target.PublishedPortsRu), string.Join("|", card.PublishedPortsRu));
        }
    }

    [Fact]
    public void Строка_где_серверы_называет_регион_и_честно_про_матчмейкер()
    {
        string line = Regions.RegionsLineRu("cs2.exe");

        Assert.Contains("Европа", line, StringComparison.Ordinal);
        Assert.Contains("матчмейкер", line, StringComparison.Ordinal);
        Assert.Contains("пинга до региона", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" мс", line, StringComparison.Ordinal); // чисел без замера здесь нет
    }

    [Fact]
    public void Игра_вне_справочника_не_получает_чужих_регионов()
    {
        GameRegionsViewModel regions = Regions;
        string unknown = regions.RegionsLineRu("my-own-game.exe");

        Assert.Contains("неизвестны", unknown, StringComparison.Ordinal);
        Assert.Contains("не будет", unknown, StringComparison.Ordinal);

        // Ни один регион из каталога не приписан чужой игре.
        foreach (GameRouteTarget target in GameRouteCatalog.Targets)
        {
            foreach (string region in target.RegionsRu)
            {
                Assert.DoesNotContain($"Где серверы: {region}", unknown, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Порты_берутся_из_каталога_и_не_выдумываются()
    {
        GameRegionsViewModel regions = Regions;

        string cs2 = regions.PortsLineRu("cs2.exe");
        Assert.Contains("27015", cs2, StringComparison.Ordinal);
        Assert.Contains("UDP", cs2, StringComparison.Ordinal);

        string minecraft = regions.PortsLineRu("javaw.exe");
        Assert.Contains("25565", minecraft, StringComparison.Ordinal);

        string unknown = regions.PortsLineRu("my-own-game.exe");
        Assert.Contains("не публикуются", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("27015", unknown, StringComparison.Ordinal);
    }

    // ── DNS: вход, а не пинг ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Карточка_DNS_говорит_про_вход_и_не_обещает_пинга()
    {
        DnsCardViewModel dns = Regions.Dns;

        Assert.Equal("DNS — это про вход, а не про пинг", dns.TitleRu);
        Assert.Contains("не снижает пинг в матче", dns.ExplanationRu, StringComparison.Ordinal);
        Assert.Contains("маршрутом до сервера", dns.ExplanationRu, StringComparison.Ordinal);
        Assert.DoesNotContain("снизил пинг", dns.ExplanationRu, StringComparison.Ordinal);
        Assert.Contains("не ускоритель пинга", dns.ListTitleRu, StringComparison.Ordinal);
        Assert.NotEmpty(dns.Resolvers);
    }

    [Fact]
    public void Список_резолверов_совпадает_с_каталогом()
    {
        DnsCardViewModel dns = Regions.Dns;

        Assert.Equal(GameRouteCatalog.FreeDnsRoutes.Count, dns.Resolvers.Count);

        for (int i = 0; i < GameRouteCatalog.FreeDnsRoutes.Count; i++)
        {
            PublicDnsRoute route = GameRouteCatalog.FreeDnsRoutes[i];
            FreeDnsRowViewModel row = dns.Resolvers[i];

            Assert.Equal(route.ProviderRu, row.ProviderRu);
            Assert.Contains(route.PrimaryIp, row.AddressesRu, StringComparison.Ordinal);
            Assert.Contains(route.SecondaryIp, row.AddressesRu, StringComparison.Ordinal);
            Assert.Equal(route.NoteRu, row.NoteRu);
        }
    }

    [Fact]
    public void Один_список_резолверов_на_программу()
    {
        IReadOnlyList<(string Name, string Primary, string Secondary)> candidates =
            GameRegionsViewModel.BenchmarkCandidates;

        Assert.Equal(GameRouteCatalog.FreeDnsRoutes.Count, candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            Assert.Equal(GameRouteCatalog.FreeDnsRoutes[i].ProviderRu, candidates[i].Name);
            Assert.Equal(GameRouteCatalog.FreeDnsRoutes[i].PrimaryIp, candidates[i].Primary);
            Assert.Equal(GameRouteCatalog.FreeDnsRoutes[i].SecondaryIp, candidates[i].Secondary);
        }
    }

    [Fact]
    public void Совет_DNS_в_проверке_сети_не_обещает_снижения_пинга()
    {
        NetworkCheckItemResult item = NetworkCheckEvaluators.EvaluateDns(
        [
            Dns(NetworkCheckEvaluators.CurrentDnsName, "192.168.1.1", warm: 40, score: 40),
            Dns("Cloudflare (1.1.1.1)", "1.1.1.1", warm: 8, score: 8),
        ]);

        Assert.Equal(NetworkCheckStatus.Attention, item.Status);
        Assert.Contains("вход", item.DetailRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не зависит", item.DetailRu, StringComparison.Ordinal);
        Assert.DoesNotContain("снизил пинг", item.DetailRu, StringComparison.Ordinal);
        Assert.DoesNotContain("снижает пинг", item.DetailRu, StringComparison.Ordinal);
    }

    [Fact]
    public void В_разметке_окна_есть_регионы_и_карточка_DNS()
    {
        string games = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "checked-GamesView.xaml"));
        string tweaks = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "checked-TweaksView.xaml"));

        Assert.Contains("RegionsLineRu", games, StringComparison.Ordinal);
        Assert.Contains("PortsLineRu", games, StringComparison.Ordinal);
        Assert.Contains("GameRegionsCard", games, StringComparison.Ordinal);
        Assert.Contains("DnsCard", tweaks, StringComparison.Ordinal);
        Assert.Contains("{Binding Dns}", tweaks, StringComparison.Ordinal);
        Assert.Contains("Resolvers", tweaks, StringComparison.Ordinal);
    }

    private static DnsResolverBenchmarkResult Dns(string name, string ip, double? warm, double score) =>
        new(name, ip, string.Empty, true, 11, 11, warm, warm, warm, 100, score, false, string.Empty);
}
