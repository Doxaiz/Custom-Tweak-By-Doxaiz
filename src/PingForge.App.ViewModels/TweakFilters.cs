namespace PingForge.App.ViewModels;

/// <summary>
/// Быстрые фильтры каталога твиков. Порядок отвечает на вопрос «с чего начать», поиск — «где тот
/// самый твик», а фильтр — «покажи только то, что мне подходит». Все три условия независимы и
/// складываются: человек может искать «dns» среди рекомендованного, и это должно работать.
/// </summary>
public enum TweakQuickFilter
{
    /// <summary>Оставить только твики с вердиктом «включать: да».</summary>
    OnlyRecommended = 0,

    /// <summary>Убрать из списка «не советуем» — то, что вредит и включено не будет.</summary>
    HideNotRecommended = 1,

    /// <summary>Оставить только то, что входит хотя бы в один авто-пресет.</summary>
    OnlyInPresets = 2,
}

/// <summary>Подписи, пояснения и само условие отбора — в одном месте, чтобы окно не придумывало своё.</summary>
public static class TweakQuickFilters
{
    public static IReadOnlyList<TweakQuickFilter> All { get; } =
    [
        TweakQuickFilter.OnlyRecommended,
        TweakQuickFilter.HideNotRecommended,
        TweakQuickFilter.OnlyInPresets,
    ];

    public static string TitleRu(TweakQuickFilter filter) => filter switch
    {
        TweakQuickFilter.OnlyRecommended => "Только рекомендуемое",
        TweakQuickFilter.HideNotRecommended => "Скрыть «не советуем»",
        TweakQuickFilter.OnlyInPresets => "Только из пресетов",
        _ => filter.ToString(),
    };

    /// <summary>Что фильтр сделает со списком — словами, а не «фильтр 3».</summary>
    public static string HintRu(TweakQuickFilter filter) => filter switch
    {
        TweakQuickFilter.OnlyRecommended =>
            "Оставить твики, у которых вердикт «включать: да». Остальные никуда не деваются — снимите фильтр и они вернутся.",
        TweakQuickFilter.HideNotRecommended =>
            "Убрать из списка твики с вердиктом «не советуем»: они помечены, потому что ломают устойчивость соединения или загрузки.",
        TweakQuickFilter.OnlyInPresets =>
            "Оставить только то, что входит хотя бы в один авто-пресет: низкий пинг, скорость или кадры.",
        _ => string.Empty,
    };

    /// <summary>Подходит ли карточка под фильтр. Состояние ползунка здесь не участвует.</summary>
    public static bool Matches(TweakRowViewModel row, TweakQuickFilter filter)
    {
        ArgumentNullException.ThrowIfNull(row);

        return filter switch
        {
            TweakQuickFilter.OnlyRecommended => row.IsRecommended,
            TweakQuickFilter.HideNotRecommended => !row.IsDangerous,
            TweakQuickFilter.OnlyInPresets => row.IsInLowPingPreset || row.IsInMaxSpeedPreset || row.IsInFpsPreset,
            _ => true,
        };
    }
}
