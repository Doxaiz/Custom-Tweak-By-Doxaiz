using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;

namespace PingForge.App.ViewModels;

/// <summary>
/// Сборка списка твиков для окна. Источник — тот же каталог, что применяет служба: если карточка
/// говорит «включать: да», то и служба включит именно это. Тексты вердиктов не пишутся в интерфейсе —
/// они берутся из <see cref="TweakVerdict"/>, поэтому окно не может придумать своё название решения.
///
/// Состав пресетов берётся не из пометок в разметке, а из <see cref="ITweakCatalog.GetByPreset"/>:
/// тем же вызовом пользуется служба, когда применяет пресет. Подпись «19 твиков» физически не может
/// разойтись с тем, что применится.
/// </summary>
public static class TweakRowFactory
{
    /// <summary>Все твики каталога, отсортированные по подкатегории, затем по названию.</summary>
    public static IReadOnlyList<TweakRowViewModel> BuildAll(ITweakCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        IReadOnlyDictionary<string, HashSet<TweakProfilePreset>> membership = BuildMembership(catalog);

        // Каталог — конкретный TweakCatalog: описания (заголовки, влияние, подкатегории) живут в нём,
        // а через интерфейс ITweakCatalog доступны только сами твики.
        TweakCatalog concrete = catalog as TweakCatalog
            ?? throw new ArgumentException("Нужен каталог PingForge.Tweaks: интерфейсу нужны описания твиков.", nameof(catalog));

        return concrete.GetDefinitions()
            .OrderBy(definition => FocusOrder(definition.EffectiveFocus))
            .ThenBy(definition => definition.TitleRu, StringComparer.CurrentCulture)
            .Select(definition => Build(definition, membership.GetValueOrDefault(definition.Code)))
            .ToList();
    }

    /// <summary>Одна строка по описанию твика и его принадлежности к пресетам.</summary>
    public static TweakRowViewModel Build(TweakDefinition definition, IReadOnlySet<TweakProfilePreset>? presets = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        presets ??= new HashSet<TweakProfilePreset>();

        return new TweakRowViewModel(
            definition.Code,
            definition.TitleRu,
            definition.WhatItDoesRu,
            TweakVerdict.ShortRu(definition.Badge),
            TweakVerdict.WhyRu(definition.Badge),
            isRecommended: definition.Badge == GameRecommendationBadge.Recommended,
            enabledByDefault: TweakVerdict.EnabledByDefault(definition.Badge),
            isInLowPingPreset: presets.Contains(TweakProfilePreset.LowLatencyGaming),
            isInMaxSpeedPreset: presets.Contains(TweakProfilePreset.MaxThroughput),
            isInFpsPreset: presets.Contains(TweakProfilePreset.FpsAndResponsiveness),
            focusRu: FocusRu(definition.EffectiveFocus),
            isDangerous: definition.Badge == GameRecommendationBadge.NotRecommended);
    }

    /// <summary>Кто в каком пресете — один проход по каталогу на все три авто-пресета.</summary>
    public static IReadOnlyDictionary<string, HashSet<TweakProfilePreset>> BuildMembership(ITweakCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var membership = new Dictionary<string, HashSet<TweakProfilePreset>>(StringComparer.OrdinalIgnoreCase);
        var presets = new[]
        {
            TweakProfilePreset.LowLatencyGaming,
            TweakProfilePreset.MaxThroughput,
            TweakProfilePreset.FpsAndResponsiveness,
        };

        foreach (TweakProfilePreset preset in presets)
        {
            foreach (ITweak tweak in catalog.GetByPreset(preset))
            {
                if (!membership.TryGetValue(tweak.Id, out HashSet<TweakProfilePreset>? set))
                {
                    set = [];
                    membership[tweak.Id] = set;
                }

                set.Add(preset);
            }
        }

        return membership;
    }

    /// <summary>
    /// Канонический порядок подкатегорий: по нему строятся группы в разделе «Твики».
    /// Один список на всё окно — чтобы порядок групп и порядок каталога не разъехались.
    /// </summary>
    public static IReadOnlyList<string> FocusGroupsRu { get; } =
    [
        FocusRu(TweakFocus.Ping),
        FocusRu(TweakFocus.Speed),
        FocusRu(TweakFocus.Fps),
        FocusRu(TweakFocus.Input),
        FocusRu(TweakFocus.System),
    ];

    /// <summary>Порядок подкатегорий в интерфейсе: пинг → скорость → кадры → ввод → система.</summary>
    public static int FocusOrder(TweakFocus focus) => focus switch
    {
        TweakFocus.Ping => 0,
        TweakFocus.Speed => 1,
        TweakFocus.Fps => 2,
        TweakFocus.Input => 3,
        _ => 4,
    };

    public static string FocusRu(TweakFocus focus) => focus switch
    {
        TweakFocus.Ping => "Пинг и сеть",
        TweakFocus.Speed => "Скорость",
        TweakFocus.Fps => "Кадры",
        TweakFocus.Input => "Ввод",
        _ => "Система",
    };

    /// <summary>Состав пресетов для окна: числа считаются по каталогу, поэтому подпись не может разойтись с делом.</summary>
    public static IReadOnlyList<PresetViewModel> BuildPresets(ITweakCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return
        [
            new(
                TweakProfilePreset.LowLatencyGaming,
                "low-latency",
                "Низкий пинг",
                "Только то, что сокращает задержку и не портит устойчивость соединения",
                catalog.GetByPreset(TweakProfilePreset.LowLatencyGaming).Count),
            new(
                TweakProfilePreset.MaxThroughput,
                "max-speed",
                "Скорость",
                "Пропускная способность и буферы: когда важнее скорость загрузки, чем задержка",
                catalog.GetByPreset(TweakProfilePreset.MaxThroughput).Count),
            new(
                TweakProfilePreset.FpsAndResponsiveness,
                "fps",
                "Кадры и отзывчивость",
                "Питание, фон, планировщик и ввод: кадры не рвутся, отклик ровнее",
                catalog.GetByPreset(TweakProfilePreset.FpsAndResponsiveness).Count),
        ];
    }
}
