using System.Text.Json;
using System.Text.RegularExpressions;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Routing;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Числа в текстах: те, что можно посчитать, считаются из каталога, а не вписываются руками.
///
/// Повод: в макете полтора года стояло «ручной выбор 28 твиков», хотя в каталоге их 47. Никто не врал
/// специально — цифру вписали один раз и забыли. Такие числа опаснее явной ошибки: они выглядят как факт.
/// Поэтому теперь число твиков берётся из `data["tweakCount"]`, игры и способы — из справочника маршрутов,
/// а расхождение страницы с каталогом ловит `check_ui_preview.js` (проверка «числа в текстах сверены
/// с каталогом»). Эти тесты держат правило в исходниках, чтобы вписанная цифра не вернулась.
/// </summary>
public sealed class NumbersInTextsTests
{
    private static string Read(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"нет копии {name} для проверки: {path}");
        return File.ReadAllText(path);
    }

    private static IReadOnlyList<ITweak> Catalog() =>
        new TweakCatalog(new TweakPlatformPorts()).GetAllTweaks();

    private static int Count(string badge) =>
        Catalog().Count(t => BadgeRu(t.Descriptor.Badge) == badge);

    private static string BadgeRu(GameRecommendationBadge badge) => badge switch
    {
        GameRecommendationBadge.Recommended => "включать: да",
        GameRecommendationBadge.Optional => "по желанию",
        GameRecommendationBadge.Caution => "осторожно",
        GameRecommendationBadge.NotRecommended => "не советуем",
        _ => badge.ToString(),
    };

    [Fact]
    public void Число_твиков_в_программе_совпадает_с_каталогом()
    {
        int count = Catalog().Count;
        string xaml = Read("checked-OptimizeView.xaml");

        // Надпись «Из 47 твиков применяются только те…» — не украшение, а обещание. Пусть считается.
        Match match = Regex.Match(xaml, @"Из (\d+) твиков");
        Assert.True(match.Success, "в «Оптимизации» нет надписи «Из N твиков» — проверьте, не переписали ли её");
        Assert.Equal(count, int.Parse(match.Groups[1].Value));
    }

    [Fact]
    public void Числа_вердиктов_в_README_совпадают_с_каталогом()
    {
        string readme = Read("checked-README.md");

        Match all = Regex.Match(readme, @"\*\*(\d+) твиков\*\*");
        Assert.True(all.Success, "в README нет строки «**N твиков**»");
        Assert.Equal(Catalog().Count, int.Parse(all.Groups[1].Value));

        Match yes = Regex.Match(readme, @"(\d+) «включать: да»");
        Match maybe = Regex.Match(readme, @"(\d+) «по желанию»");
        Match careful = Regex.Match(readme, @"(\d+) «осторожно»");
        Match no = Regex.Match(readme, @"(\d+) «не советуем»");
        Assert.True(yes.Success && maybe.Success && careful.Success && no.Success,
            "в README пропали числа вердиктов — их должно быть четыре");

        Assert.Equal(Count("включать: да"), int.Parse(yes.Groups[1].Value));
        Assert.Equal(Count("по желанию"), int.Parse(maybe.Groups[1].Value));
        Assert.Equal(Count("осторожно"), int.Parse(careful.Groups[1].Value));
        Assert.Equal(Count("не советуем"), int.Parse(no.Groups[1].Value));
    }

    [Fact]
    public void Размер_пресета_низкого_пинга_в_README_совпадает_с_каталогом()
    {
        int lowPing = Catalog().Count(t => t.Descriptor.IncludedInLowPingPreset);
        string readme = Read("checked-README.md");

        Match match = Regex.Match(readme, @"«Низкого пинга» \((\d+) твиков\)");
        Assert.True(match.Success, "в README нет размера пресета «Низкий пинг»");
        Assert.Equal(lowPing, int.Parse(match.Groups[1].Value));
    }

    [Fact]
    public void Макет_берёт_числа_из_каталога_а_не_из_памяти()
    {
        string renderer = Read("checked-render-ui-preview.py");

        // Вписанная цифра рядом со словом — это то, как «28 твиков» прожило до 47.
        Assert.DoesNotMatch(new Regex(@"\d+\s*твик"), renderer);
        Assert.DoesNotMatch(new Regex(@"\d+\s*игр"), renderer);
        Assert.DoesNotMatch(new Regex(@"\d+\s*способ"), renderer);

        // А нужные числа приходят из данных: плитка оглавления, игры и способы, пресеты.
        Assert.Contains("{tweaks} твиков", renderer, StringComparison.Ordinal);
        Assert.Contains("data[\"tweakCount\"]", renderer, StringComparison.Ordinal);
        Assert.Contains("game_count", renderer, StringComparison.Ordinal);
        Assert.Contains("ways_count", renderer, StringComparison.Ordinal);
        Assert.Contains("preset_triplet", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void Проверка_страницы_сверяет_числа_с_каталогом()
    {
        string checker = Read("checked-check-ui-preview.js");

        Assert.Contains("catalog.json", checker, StringComparison.Ordinal);
        Assert.Contains("числа в текстах сверены с каталогом", checker, StringComparison.Ordinal);
        Assert.Contains("allowedTweaks", checker, StringComparison.Ordinal);
        Assert.Contains("allowedWays", checker, StringComparison.Ordinal);
    }

    [Fact]
    public void Каталог_в_репозитории_согласован_сам_с_собой()
    {
        // docs/catalog.json лежит в репозитории и перерисовывается из кода; если он разойдётся с собой,
        // то и страница, и проверка чисел будут сверяться с неправдой.
        using JsonDocument document = JsonDocument.Parse(Read("checked-catalog.json"));
        JsonElement root = document.RootElement;

        int tweakCount = root.GetProperty("tweakCount").GetInt32();
        Assert.Equal(root.GetProperty("tweaks").GetArrayLength(), tweakCount);
        Assert.Equal(Catalog().Count, tweakCount);

        JsonElement routes = root.GetProperty("routes");
        Assert.Equal(routes.GetProperty("games").GetArrayLength(), routes.GetProperty("gameCount").GetInt32());
        Assert.Equal(GameRouteCatalog.Targets.Count, routes.GetProperty("gameCount").GetInt32());
        Assert.Equal(GameRouteCatalog.FreeDnsRoutes.Count, routes.GetProperty("dns").GetArrayLength());
        int ways = routes.GetProperty("pathKinds").GetArrayLength();
        int freeWays = routes.GetProperty("freePathCount").GetInt32();
        Assert.InRange(freeWays, 1, ways);
    }
}
