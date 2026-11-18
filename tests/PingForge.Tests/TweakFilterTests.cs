using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Быстрые фильтры каталога: «только рекомендуемое», «скрыть не советуем», «только из пресетов».
///
/// Зачем это отдельными тестами: фильтр — это обещание «я покажу только подходящее». Ошибка здесь
/// тихая: человек снимет фильтр и не поймёт, почему карточек стало больше, или решит, что твиков
/// в каталоге меньше, чем есть. Поэтому проверяется и отбор, и подпись с числами, и то, что фильтр
/// ничего не применяет.
/// </summary>
public sealed class TweakFilterTests
{
    private static TweakRowViewModel Row(
        string code,
        string title,
        GameRecommendationBadge badge,
        bool inPreset = false,
        string focus = "Пинг и сеть") =>
        new(
            code,
            title,
            "Что делает твик.",
            TweakVerdict.ShortRu(badge),
            "почему так",
            isRecommended: badge == GameRecommendationBadge.Recommended,
            enabledByDefault: TweakVerdict.EnabledByDefault(badge),
            isInLowPingPreset: inPreset,
            isInMaxSpeedPreset: false,
            isInFpsPreset: false,
            focusRu: focus,
            isDangerous: badge == GameRecommendationBadge.NotRecommended);

    private static List<TweakRowViewModel> Catalog() =>
    [
        Row("DNS_CACHE", "Кэш DNS", GameRecommendationBadge.Recommended, inPreset: true),
        Row("TCP_ACK", "TCP ACK", GameRecommendationBadge.Optional, inPreset: true),
        Row("NIC_OFFLOAD", "Разгрузка карты", GameRecommendationBadge.Caution),
        Row("NIC_THROTTLE", "Троттлинг", GameRecommendationBadge.NotRecommended),
        Row("GAME_DVR", "Игровая запись", GameRecommendationBadge.Optional),
    ];

    private static TweaksViewModel ViewModel(out List<TweakRowViewModel> rows)
    {
        rows = Catalog();
        var presets = new List<PresetViewModel>
        {
            new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 2),
        };

        return new TweaksViewModel(new EngineClient(new FakeEngineChannel()), new FakeDialogs(), rows, presets);
    }

    private static void Turn(TweaksViewModel tweaks, TweakQuickFilter filter, bool on) =>
        tweaks.Filters.Single(option => option.Filter == filter).IsOn = on;

    [Fact]
    public void Только_рекомендуемое_оставляет_рекомендованные()
    {
        TweaksViewModel tweaks = ViewModel(out _);

        Turn(tweaks, TweakQuickFilter.OnlyRecommended, true);

        Assert.Equal(["DNS_CACHE"], tweaks.VisibleRows.Select(row => row.Code).ToArray());
        Assert.All(tweaks.VisibleRows, row => Assert.True(row.IsRecommended));
    }

    [Fact]
    public void Скрыть_не_советуем_убирает_только_вредные()
    {
        TweaksViewModel tweaks = ViewModel(out List<TweakRowViewModel> rows);

        Turn(tweaks, TweakQuickFilter.HideNotRecommended, true);

        Assert.Equal(rows.Count - 1, tweaks.VisibleRows.Count);
        Assert.DoesNotContain(tweaks.VisibleRows, row => row.IsDangerous);
        Assert.Contains(tweaks.VisibleRows, row => row.Code == "NIC_OFFLOAD");
    }

    [Fact]
    public void Только_из_пресетов_оставляет_участников_пресетов()
    {
        TweaksViewModel tweaks = ViewModel(out _);

        Turn(tweaks, TweakQuickFilter.OnlyInPresets, true);

        Assert.Equal(["DNS_CACHE", "TCP_ACK"], tweaks.VisibleRows.Select(row => row.Code).ToArray());
    }

    [Fact]
    public void Фильтры_складываются_между_собой_и_с_поиском()
    {
        TweaksViewModel tweaks = ViewModel(out _);

        Turn(tweaks, TweakQuickFilter.HideNotRecommended, true);
        Turn(tweaks, TweakQuickFilter.OnlyRecommended, true);
        Assert.Equal(["DNS_CACHE"], tweaks.VisibleRows.Select(row => row.Code).ToArray());

        tweaks.SearchRu = "dns";
        Assert.Equal(["DNS_CACHE"], tweaks.VisibleRows.Select(row => row.Code).ToArray());

        tweaks.SearchRu = "троттлинг";
        // «Не советуем» скрыт фильтром — значит и поиск по нему ничего не найдёт.
        Assert.Empty(tweaks.VisibleRows);
        Assert.Contains("снимите фильтр", tweaks.EmptyLineRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Фильтры_не_меняют_отметки_и_пресеты()
    {
        TweaksViewModel tweaks = ViewModel(out List<TweakRowViewModel> rows);
        int checkedBefore = tweaks.CheckedCount;

        Turn(tweaks, TweakQuickFilter.OnlyRecommended, true);
        Turn(tweaks, TweakQuickFilter.OnlyInPresets, true);
        Turn(tweaks, TweakQuickFilter.OnlyInPresets, false);

        Assert.Equal(checkedBefore, tweaks.CheckedCount);
        Assert.Equal(rows.Count, tweaks.Rows.Count);
        Assert.All(rows.Where(row => row.IsRecommended), row => Assert.True(row.IsOn));
    }

    [Fact]
    public void Строка_фильтров_называет_включённое_и_числа()
    {
        TweaksViewModel tweaks = ViewModel(out List<TweakRowViewModel> rows);

        Assert.False(tweaks.HasActiveFilters);
        Assert.Equal(string.Empty, tweaks.FiltersLineRu);

        Turn(tweaks, TweakQuickFilter.OnlyRecommended, true);

        Assert.True(tweaks.HasActiveFilters);
        Assert.Contains(TweakQuickFilters.TitleRu(TweakQuickFilter.OnlyRecommended), tweaks.FiltersLineRu, StringComparison.Ordinal);
        Assert.Contains($"Показано 1 из {rows.Count}", tweaks.FiltersLineRu, StringComparison.Ordinal);
        Assert.Equal([TweakQuickFilter.OnlyRecommended], tweaks.ActiveFilters.ToArray());
    }

    [Fact]
    public void Снять_фильтры_возвращает_весь_каталог()
    {
        TweaksViewModel tweaks = ViewModel(out List<TweakRowViewModel> rows);

        Turn(tweaks, TweakQuickFilter.OnlyRecommended, true);
        Turn(tweaks, TweakQuickFilter.HideNotRecommended, true);
        Assert.True(tweaks.VisibleRows.Count < rows.Count);

        tweaks.ClearQuickFilters();

        Assert.False(tweaks.HasActiveFilters);
        Assert.Equal(rows.Count, tweaks.VisibleRows.Count);
        Assert.Equal(string.Empty, tweaks.FiltersLineRu);
    }

    [Fact]
    public void Фильтр_и_поиск_не_спорят_друг_с_другом_после_снятия()
    {
        TweaksViewModel tweaks = ViewModel(out List<TweakRowViewModel> rows);

        tweaks.SearchRu = "a";
        int withSearch = tweaks.VisibleRows.Count;
        Turn(tweaks, TweakQuickFilter.HideNotRecommended, true);
        Assert.True(tweaks.VisibleRows.Count <= withSearch);

        tweaks.ClearSearch();
        Assert.Equal(rows.Count - 1, tweaks.VisibleRows.Count);
    }
}
