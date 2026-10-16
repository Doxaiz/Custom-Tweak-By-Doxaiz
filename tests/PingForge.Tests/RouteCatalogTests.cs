using System.Net;
using PingForge.Routing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Справочник маршрутов — это то, что пользователь читает как «обещание». Поэтому проверяем не только
/// количество записей, но и честность формулировок: платное названо платным, небезопасное — небезопасным,
/// а запрещённых обещаний («пинг 0», «обходит блокировки») в текстах нет ни одного.
/// </summary>
public sealed class RouteCatalogTests
{
    private static readonly string[] ForbiddenClaims =
    {
        "пинг 0", "пинг ноль", "нулевой пинг", "мгновенн", "обходит блокировк", "невидим для анти",
        "гарантированн", "на 100 %", "всегда снижает"
    };

    [Fact]
    public void Справочник_содержит_двадцать_две_игры_и_каждая_описана()
    {
        Assert.Equal(22, GameRouteCatalog.Targets.Count);
        Assert.All(GameRouteCatalog.Targets, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.GameRu), "у игры должно быть название");
            Assert.EndsWith(".exe", t.ProcessName, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(t.RegionsRu);
            Assert.False(string.IsNullOrWhiteSpace(t.NoteRu), $"у игры {t.GameRu} нет честной оговорки");
        });
    }

    [Fact]
    public void Имена_процессов_уникальны_иначе_детектор_потока_будет_путаться()
    {
        var duplicates = GameRouteCatalog.Targets
            .GroupBy(t => t.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Названия_игр_уникальны()
    {
        var duplicates = GameRouteCatalog.Targets
            .GroupBy(t => t.GameRu, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Популярные_бесплатные_игры_на_месте_и_помечены_бесплатными()
    {
        var free = GameRouteCatalog.Targets.Where(t => t.IsFreeToPlay).Select(t => t.GameRu).ToHashSet();

        Assert.Contains("Fortnite", free);
        Assert.Contains("Counter-Strike 2", free);
        Assert.Contains("Dota 2", free);
        Assert.Contains("VALORANT", free);
        Assert.Contains("Apex Legends", free);
        Assert.True(free.Count >= 10, $"бесплатных игр должно быть не меньше десяти, а их {free.Count}");
    }

    [Fact]
    public void Платные_игры_не_помечены_бесплатными()
    {
        var paid = GameRouteCatalog.Targets.Where(t => !t.IsFreeToPlay).Select(t => t.GameRu).ToList();

        Assert.Contains("PUBG: Battlegrounds", paid);
        Assert.Contains("Escape from Tarkov", paid);
        Assert.DoesNotContain("Fortnite", paid);
    }

    [Fact]
    public void Для_игр_с_ядерным_анти_читом_прямо_сказано_что_фильтр_не_поднимается()
    {
        string[] kernelAntiCheat = { "VALORANT", "League of Legends", "Call of Duty: Warzone", "Apex Legends" };

        foreach (var game in kernelAntiCheat)
        {
            var target = GameRouteCatalog.Targets.Single(t => t.GameRu == game);
            bool filterOff = target.NoteRu.Contains("не поднима", StringComparison.OrdinalIgnoreCase) ||
                             target.NoteRu.Contains("выключен", StringComparison.OrdinalIgnoreCase);
            Assert.True(filterOff, $"для {game} должно быть сказано, что фильтр не поднимается, а написано: {target.NoteRu}");
            Assert.Contains("маршрут", target.NoteRu);
        }
    }

    [Fact]
    public void Пять_бесплатных_DNS_все_с_рабочими_адресами()
    {
        Assert.Equal(5, GameRouteCatalog.FreeDnsRoutes.Count);

        foreach (var dns in GameRouteCatalog.FreeDnsRoutes)
        {
            Assert.True(IPAddress.TryParse(dns.PrimaryIp, out var primary), $"не разобран адрес {dns.PrimaryIp}");
            Assert.True(IPAddress.TryParse(dns.SecondaryIp, out var secondary), $"не разобран адрес {dns.SecondaryIp}");
            Assert.NotEqual(primary, secondary);
            Assert.False(string.IsNullOrWhiteSpace(dns.NoteRu));
        }

        var providers = GameRouteCatalog.FreeDnsRoutes.Select(d => d.ProviderRu).ToList();
        Assert.Contains(providers, p => p.Contains("Cloudflare"));
        Assert.Contains(providers, p => p.Contains("Yandex"));
    }

    [Fact]
    public void Восемь_способов_сократить_путь_и_ровно_четыре_бесплатных()
    {
        Assert.Equal(8, GameRouteCatalog.PathKinds.Count);
        Assert.Equal(4, GameRouteCatalog.FreePathCount);

        var free = GameRouteCatalog.PathKinds.Where(p => GameRouteCatalog.IsFreePath(p.KindRu)).ToList();
        Assert.Equal(GameRouteCatalog.FreePathCount, free.Count);
        Assert.Contains(free, p => p.KindRu == "Прямой путь");
        Assert.Contains(free, p => p.KindRu.Contains("IPv6"));
        Assert.Contains(free, p => p.KindRu.Contains("Кабель"));
        Assert.Contains(free, p => p.KindRu.Contains("роутера"));
    }

    [Fact]
    public void Второй_канал_не_выдаётся_за_бесплатный()
    {
        var second = GameRouteCatalog.PathKinds.Single(p => p.KindRu == "Второй канал");

        Assert.False(GameRouteCatalog.IsFreePath(second.KindRu));
        Assert.DoesNotContain("0 ₽", second.CostRu);
        Assert.Contains("тариф", second.CostRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Безопасно", second.SafetyRu);
    }

    [Fact]
    public void Каждый_способ_в_списке_бесплатных_действительно_без_платы()
    {
        Assert.All(GameRouteCatalog.PathKinds.Where(p => GameRouteCatalog.IsFreePath(p.KindRu)), p =>
        {
            Assert.StartsWith("0 ₽", p.CostRu);
            Assert.StartsWith("Безопасно", p.SafetyRu);
        });

        // И наоборот: если в цене стоит ноль — способ обязан быть в списке бесплатных,
        // иначе проза и данные разойдутся.
        var zeroCost = GameRouteCatalog.PathKinds.Where(p => p.CostRu.StartsWith("0 ₽", StringComparison.Ordinal)).ToList();
        Assert.All(zeroCost, p => Assert.True(GameRouteCatalog.IsFreePath(p.KindRu),
            $"способ «{p.KindRu}» стоит 0 ₽, но не помечен бесплатным"));
    }

    [Fact]
    public void Платное_и_небезопасное_названо_своими_словами()
    {
        var vps = GameRouteCatalog.PathKinds.Single(p => p.KindRu.Contains("VPS"));
        var vpn = GameRouteCatalog.PathKinds.Single(p => p.KindRu.Contains("VPN"));
        var boosters = GameRouteCatalog.PathKinds.Single(p => p.KindRu.Contains("ускорители"));

        Assert.Equal("Аренда VPS", vps.CostRu);
        Assert.Contains("нужен ваш сервер", vps.SafetyRu);

        Assert.Equal("Обычно подписка", vpn.CostRu);
        Assert.StartsWith("Осторожно", vpn.SafetyRu);

        Assert.StartsWith("Не советуем", boosters.SafetyRu);
        Assert.Contains("Ни один такой сервис в программу не встроен", boosters.SafetyRu);
    }

    [Fact]
    public void У_каждого_способа_есть_цена_и_ответ_про_безопасность()
    {
        Assert.All(GameRouteCatalog.PathKinds, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.WhatRu));
            Assert.False(string.IsNullOrWhiteSpace(p.CostRu), $"у способа {p.KindRu} не указана цена");
            Assert.False(string.IsNullOrWhiteSpace(p.SafetyRu), $"у способа {p.KindRu} не сказано про безопасность");
        });
    }

    [Fact]
    public void Итоговая_оговорка_называет_число_бесплатных_способов_из_данных()
    {
        string note = GameRouteCatalog.HonestNoteRu;

        Assert.Contains("ровно четыре", note);
        Assert.Contains("не встраивает сторонние VPN", note);
        Assert.Contains("не подменяет ваш трафик", note);

        // Каждый бесплатный способ назван в оговорке человеческим языком, без «ipv6» и «wi-fi».
        Assert.Equal(GameRouteCatalog.FreePathKindsRu.Count, GameRouteCatalog.FreePathProseRu.Count);
        foreach (var prose in GameRouteCatalog.FreePathProseRu)
        {
            Assert.Contains(prose, note);
        }

        Assert.DoesNotContain("ipv6", note);
        Assert.DoesNotContain("wi-fi", note);

        Assert.Contains("второй канал", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не бесплатно", note);
    }

    [Fact]
    public void Сводка_по_справочнику_считает_то_же_что_и_каталог()
    {
        string summary = GameRouteCatalog.DescribeSummaryRu();

        Assert.Contains($"{GameRouteCatalog.Targets.Count}", summary);
        Assert.Contains($"{GameRouteCatalog.FreeDnsRoutes.Count}", summary);
        Assert.Contains($"{GameRouteCatalog.PathKinds.Count}", summary);
        Assert.Contains($"{GameRouteCatalog.FreePathCount}", summary);
        Assert.Contains("бесплатных из них — 4", summary);
    }

    [Fact]
    public void Список_бесплатных_способов_сходится_с_данными_и_со_своими_же_именами_для_фразы()
    {
        Assert.Equal(GameRouteCatalog.FreePathCount, GameRouteCatalog.FreePathKindsRu.Count);
        Assert.Equal(GameRouteCatalog.FreePathKindsRu.Count, GameRouteCatalog.FreePathProseRu.Count);

        foreach (var kind in GameRouteCatalog.FreePathKindsRu)
        {
            Assert.Contains(GameRouteCatalog.PathKinds, p => p.KindRu == kind);
            Assert.True(GameRouteCatalog.IsFreePath(kind));
        }

        foreach (var prose in GameRouteCatalog.FreePathProseRu)
        {
            Assert.False(string.IsNullOrWhiteSpace(prose));
            Assert.Equal(prose, prose.Trim());
        }
    }

    [Fact]
    public void Ни_один_платный_способ_не_подписан_нулём_рублей()
    {
        var paid = GameRouteCatalog.PathKinds.Where(p => !GameRouteCatalog.IsFreePath(p.KindRu)).ToList();

        Assert.Equal(4, paid.Count);
        Assert.All(paid, p => Assert.DoesNotContain("0 ₽", p.CostRu));
    }

    [Fact]
    public void Нигде_нет_запрещённых_обещаний()
    {
        var texts = GameRouteCatalog.Targets.SelectMany(t => new[] { t.GameRu, t.NoteRu })
            .Concat(GameRouteCatalog.Targets.SelectMany(t => t.RegionsRu))
            .Concat(GameRouteCatalog.Targets.SelectMany(t => t.PublishedPortsRu))
            .Concat(GameRouteCatalog.FreeDnsRoutes.SelectMany(d => new[] { d.ProviderRu, d.NoteRu }))
            .Concat(GameRouteCatalog.PathKinds.SelectMany(p => new[] { p.KindRu, p.WhatRu, p.CostRu, p.SafetyRu }))
            .Concat(new[] { GameRouteCatalog.HonestNoteRu, GameRouteCatalog.DescribeSummaryRu() })
            .ToList();

        foreach (var text in texts)
        {
            foreach (var claim in ForbiddenClaims)
            {
                Assert.False(text.Contains(claim, StringComparison.OrdinalIgnoreCase),
                    $"в тексте «{text}» встретилось запрещённое обещание «{claim}»");
            }
        }
    }

    [Fact]
    public void Порта_описаны_как_официальные_а_не_выдуманные()
    {
        var withPorts = GameRouteCatalog.Targets.Where(t => t.PublishedPortsRu.Count > 0).ToList();
        Assert.True(withPorts.Count >= 5, "хотя бы у пяти игр должны быть официально описанные порты");

        Assert.All(withPorts, t => Assert.All(t.PublishedPortsRu,
            port => Assert.Contains("UDP", port, StringComparison.OrdinalIgnoreCase)));

        // Там, где разработчик портов не публикует, это сказано прямо — и объяснено, как программа работает без них.
        var withoutPorts = GameRouteCatalog.Targets.Where(t => t.PublishedPortsRu.Count == 0).ToList();
        Assert.NotEmpty(withoutPorts);
        Assert.All(withoutPorts, t => Assert.False(string.IsNullOrWhiteSpace(t.NoteRu)));
    }
}
