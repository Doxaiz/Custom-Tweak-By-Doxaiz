using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Порядок, группы и поиск в разделе «Твики». Проверяется без окна: правила — это чистые функции,
/// а «как это выглядит» проверяет витрина и живой прогон.
///
/// Почему эти тесты вообще нужны: у каталога 47 карточек, и раньше порядок был один — «по подкатегории,
/// затем по названию». Человек видел «осторожно» и «не советуем» вперемешку с рекомендованным и не
/// понимал, с чего начать. Теперь порядок — часть продукта, значит и проверяться должен как продукт.
/// </summary>
public sealed class TweakSortingTests
{
    private const string Ping = "Пинг и сеть";
    private const string Speed = "Скорость";
    private const string Fps = "Кадры";
    private const string Input = "Ввод";
    private const string System = "Система";

    private static TweakRowViewModel Row(
        string code,
        string title,
        string verdict,
        string focus,
        bool dangerous = false,
        string what = "Что делает твик.")
    {
        bool recommended = verdict == TweakVerdict.ShortRu(GameRecommendationBadge.Recommended);
        return new TweakRowViewModel(
            code,
            title,
            what,
            verdict,
            "почему так",
            isRecommended: recommended,
            enabledByDefault: recommended,
            isInLowPingPreset: false,
            isInMaxSpeedPreset: false,
            isInFpsPreset: false,
            focusRu: focus,
            isDangerous: dangerous);
    }

    private static List<TweakRowViewModel> Sample() =>
    [
        Row("TCP_ACK", "TCP ACK: без задержек", TweakVerdict.ShortRu(GameRecommendationBadge.Optional), Ping),
        Row("DNS_CACHE", "Кэш DNS", TweakVerdict.ShortRu(GameRecommendationBadge.Recommended), Ping),
        Row("NIC_OFFLOAD", "Отключить разгрузку карты", TweakVerdict.ShortRu(GameRecommendationBadge.NotRecommended), Ping, dangerous: true),
        Row("NIC_THROTTLE", "Отключить троттлинг", TweakVerdict.ShortRu(GameRecommendationBadge.Caution), Speed),
        Row("POWER_PLAN", "План питания: максимум", TweakVerdict.ShortRu(GameRecommendationBadge.Recommended), Fps),
        Row("GAME_MODE", "Игровой режим", TweakVerdict.ShortRu(GameRecommendationBadge.Recommended), Fps),
        Row("MOUSE_ACCEL", "Ускорение мыши выключить", TweakVerdict.ShortRu(GameRecommendationBadge.Recommended), Input),
        Row("KB_FILTER", "Фильтр клавиш", TweakVerdict.ShortRu(GameRecommendationBadge.Optional), Input),
        Row("TEMP_CLEAN", "Чистка временных файлов", TweakVerdict.ShortRu(GameRecommendationBadge.Optional), System),
    ];

    private static (TweaksViewModel Tweaks, List<TweakRowViewModel> Rows) ViewModel(int count = 0)
    {
        var channel = new FakeEngineChannel();
        var dialogs = new FakeDialogs();
        List<TweakRowViewModel> rows = count > 0
            ? Enumerable.Range(0, count).Select(i => Row(
                $"TWEAK_{i:00}",
                $"Твик {i:00}",
                i % 4 == 0
                    ? TweakVerdict.ShortRu(GameRecommendationBadge.Recommended)
                    : TweakVerdict.ShortRu(GameRecommendationBadge.Optional),
                (i % 5) switch
                {
                    0 => Ping,
                    1 => Speed,
                    2 => Fps,
                    3 => Input,
                    _ => System,
                })).ToList()
            : Sample();

        var presets = new List<PresetViewModel>
        {
            new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 5),
        };

        return (new TweaksViewModel(new EngineClient(channel), dialogs, rows, presets), rows);
    }

    [Fact]
    public void Вес_вердикта_совпадает_с_текстом_вердикта()
    {
        // Сортировка опирается на текст вердикта. Если в каталоге поменяют формулировку и забудут
        // вес, порядок тихо станет произвольным — этот тест ловит именно такой рассинхрон.
        Assert.Equal(0, TweakVerdict.Rank(TweakVerdict.ShortRu(GameRecommendationBadge.Recommended)));
        Assert.Equal(1, TweakVerdict.Rank(TweakVerdict.ShortRu(GameRecommendationBadge.Optional)));
        Assert.Equal(2, TweakVerdict.Rank(TweakVerdict.ShortRu(GameRecommendationBadge.Caution)));
        Assert.Equal(3, TweakVerdict.Rank(TweakVerdict.ShortRu(GameRecommendationBadge.NotRecommended)));

        Assert.Equal(4, TweakVerdict.Rank("нет вердикта"));
        Assert.Equal(4, TweakVerdict.Rank(null));
    }

    [Fact]
    public void Порядок_по_назначению_ставит_рекомендованное_выше_внутри_группы()
    {
        (TweaksViewModel tweaks, _) = ViewModel();

        string[] ping = tweaks.Groups.Single(group => group.TitleRu == Ping).Rows.Select(row => row.Code).ToArray();

        Assert.Equal(["DNS_CACHE", "TCP_ACK", "NIC_OFFLOAD"], ping);
    }

    [Fact]
    public void Группы_идут_в_порядке_каталога_и_пустых_среди_них_нет()
    {
        (TweaksViewModel tweaks, _) = ViewModel();

        Assert.Equal([Ping, Speed, Fps, Input, System],
            tweaks.Groups.Select(group => group.TitleRu).ToArray());
        Assert.All(tweaks.Groups, group => Assert.NotEmpty(group.Rows));
    }

    [Fact]
    public void Алфавитный_порядок_не_смотрит_на_вердикт()
    {
        (TweaksViewModel tweaks, _) = ViewModel();
        tweaks.SelectOrder(TweakOrder.Alphabetical);

        IReadOnlyList<TweakRowViewModel> rows = tweaks.VisibleRows;
        string[] titles = rows.Select(row => row.TitleRu).ToArray();

        Assert.Equal(titles.OrderBy(title => title, StringComparer.OrdinalIgnoreCase).ToArray(), titles);
        Assert.Single(tweaks.Groups);
    }

    [Fact]
    public void Сначала_полезное_поднимает_рекомендованное_выше_остального()
    {
        (TweaksViewModel tweaks, _) = ViewModel();
        tweaks.SelectOrder(TweakOrder.RecommendedFirst);

        int[] ranks = tweaks.VisibleRows.Select(row => row.VerdictRank).ToArray();

        Assert.Equal(ranks.OrderBy(rank => rank).ToArray(), ranks);
        Assert.Equal(0, ranks[0]);

        // В этом режиме группа одна — иначе «сначала полезное» спорило бы с «по назначению».
        Assert.Single(tweaks.Groups);
        Assert.Equal(TweakOrdering.TitleRu(TweakOrder.RecommendedFirst), tweaks.Groups[0].TitleRu);
    }

    [Fact]
    public void Поиск_находит_по_названию_описанию_коду_и_назначению()
    {
        List<TweakRowViewModel> rows = Sample();
        Assert.True(TweakOrdering.MatchesSearch(rows[0], "ack"));
        Assert.True(TweakOrdering.MatchesSearch(rows[0], "TCP_"));
        Assert.True(TweakOrdering.MatchesSearch(rows[0], "что делает"));
        Assert.True(TweakOrdering.MatchesSearch(rows[0], Ping));
        Assert.False(TweakOrdering.MatchesSearch(rows[0], "маршрут"));
        Assert.True(TweakOrdering.MatchesSearch(rows[0], "   "));
        Assert.True(TweakOrdering.MatchesSearch(rows[0], null));
    }

    [Fact]
    public void Поиск_без_совпадений_не_показывает_весь_каталог()
    {
        (TweaksViewModel tweaks, List<TweakRowViewModel> rows) = ViewModel();

        tweaks.SearchRu = "нет-такого-твика";

        Assert.Empty(tweaks.VisibleRows);
        Assert.Empty(tweaks.Groups);
        Assert.False(tweaks.HasVisibleRows);
        Assert.Contains("Ничего не найдено", tweaks.SearchLineRu, StringComparison.Ordinal);
        Assert.Contains("ничего нет", tweaks.EmptyLineRu, StringComparison.Ordinal);

        // Отметки при поиске не меняются: поиск — это про то, что видно, а не про то, что применится.
        Assert.Equal(rows.Count, tweaks.Rows.Count);
    }

    [Fact]
    public void Поиск_складывается_с_фильтром_по_кодам()
    {
        (TweaksViewModel tweaks, _) = ViewModel();

        tweaks.ShowOnlyCodes(["DNS_CACHE", "GAME_MODE", "MOUSE_ACCEL"]);
        Assert.Equal(3, tweaks.VisibleRows.Count);

        tweaks.SearchRu = "dns";
        Assert.Equal(["DNS_CACHE"], tweaks.VisibleRows.Select(row => row.Code).ToArray());
        Assert.True(tweaks.IsFiltered);

        tweaks.ClearSearch();
        Assert.Equal(3, tweaks.VisibleRows.Count);

        tweaks.ClearFilter();
        Assert.Equal(9, tweaks.VisibleRows.Count);
    }

    [Fact]
    public void Заголовок_группы_считает_включённые_и_обновляется_по_ползунку()
    {
        (TweaksViewModel tweaks, _) = ViewModel();

        TweakGroupViewModel input = tweaks.Groups.Single(group => group.TitleRu == Input);
        int onBefore = input.Rows.Count(row => row.IsOn);
        Assert.Equal(TweakOrdering.CountRu(input.Rows.Count, onBefore), input.CountRu);

        bool raised = false;
        input.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TweakGroupViewModel.CountRu))
            {
                raised = true;
            }
        };

        // В группе «Ввод» рекомендованное включено, «по желанию» — нет: есть что переключить.
        TweakRowViewModel off = input.Rows.First(row => !row.IsOn);
        off.IsOn = true;

        Assert.True(raised, "заголовок группы не сообщил о новом числе включённых");
        Assert.Equal(TweakOrdering.CountRu(input.Rows.Count, onBefore + 1), input.CountRu);
        Assert.Contains("включено", input.CountRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Число_склоняется_по_русски()
    {
        Assert.Equal("1 твик", $"{1} {TweakOrdering.Plural(1, "твик", "твика", "твиков")}");
        Assert.Equal("2 твика", $"{2} {TweakOrdering.Plural(2, "твик", "твика", "твиков")}");
        Assert.Equal("4 твика", $"{4} {TweakOrdering.Plural(4, "твик", "твика", "твиков")}");
        Assert.Equal("5 твиков", $"{5} {TweakOrdering.Plural(5, "твик", "твика", "твиков")}");
        Assert.Equal("11 твиков", $"{11} {TweakOrdering.Plural(11, "твик", "твика", "твиков")}");
        Assert.Equal("14 твиков", $"{14} {TweakOrdering.Plural(14, "твик", "твика", "твиков")}");
        Assert.Equal("21 твик", $"{21} {TweakOrdering.Plural(21, "твик", "твика", "твиков")}");
        Assert.Equal("22 твика", $"{22} {TweakOrdering.Plural(22, "твик", "твика", "твиков")}");
        Assert.Equal("47 твиков", $"{47} {TweakOrdering.Plural(47, "твик", "твика", "твиков")}");
    }

    [Fact]
    public void Порядок_не_зависит_от_того_в_каком_порядке_пришли_строки()
    {
        (TweaksViewModel first, List<TweakRowViewModel> rows) = ViewModel(24);
        first.SelectOrder(TweakOrder.RecommendedFirst);

        var shuffled = rows.OrderBy(row => row.Code.GetHashCode(StringComparison.Ordinal)).Reverse().ToList();
        var channel = new FakeEngineChannel();
        var tweaks2 = new TweaksViewModel(
            new EngineClient(channel),
            new FakeDialogs(),
            shuffled,
            new List<PresetViewModel> { new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 5) });
        tweaks2.SelectOrder(TweakOrder.RecommendedFirst);

        Assert.Equal(first.VisibleRows.Select(row => row.Code).ToArray(), tweaks2.VisibleRows.Select(row => row.Code).ToArray());
    }

    [Fact]
    public void Неизвестная_подкатегория_не_теряется_из_списка()
    {
        List<TweakRowViewModel> rows = Sample();
        rows.Add(Row("NEW_GROUP_THING", "Твик из новой подкатегории", TweakVerdict.ShortRu(GameRecommendationBadge.Optional), "Новая подкатегория"));

        IReadOnlyList<TweakGroupViewModel> groups = TweakOrdering.BuildGroups(rows, TweakOrder.ByPurpose);

        Assert.Equal("Новая подкатегория", groups[^1].TitleRu);
        Assert.Contains(groups, group => group.Rows.Any(row => row.Code == "NEW_GROUP_THING"));
        Assert.Equal(rows.Count, groups.Sum(group => group.Rows.Count));
    }

    [Fact]
    public void Пилюли_порядка_отмечают_текущий_режим()
    {
        (TweaksViewModel tweaks, _) = ViewModel();

        Assert.True(tweaks.Orders.Single(option => option.Order == TweakOrder.ByPurpose).IsSelected);
        Assert.False(tweaks.Orders.Single(option => option.Order == TweakOrder.Alphabetical).IsSelected);

        TweakOrderOptionViewModel alphabetical = tweaks.Orders.Single(option => option.Order == TweakOrder.Alphabetical);
        alphabetical.IsSelected = true;   // так это делает нажатие на пилюлю

        Assert.Equal(TweakOrder.Alphabetical, tweaks.Order);
        Assert.True(alphabetical.IsSelected);
        Assert.False(tweaks.Orders.Single(option => option.Order == TweakOrder.ByPurpose).IsSelected);
    }
}
