using PingForge.Core.Models;
using PingForge.Core.Tweaks;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Требование к продукту звучало так: «твики оптимизации ПК с пометкой — стоит включать или нет» и
/// «распределить твики по пресетам». Здесь проверяется, что и то и другое — часть каталога, а не
/// подпись, нарисованная в интерфейсе: вердикты, подкатегории и составы пресетов считаются из данных.
/// </summary>
public sealed class TweakVerdictAndPresetTests
{
    private static TweakCatalog FullCatalog() => new(new TweakPlatformPorts());

    private static IReadOnlyList<TweakDefinition> Definitions() => FullCatalog().GetDefinitions();

    private static IReadOnlyList<TweakDescriptor> Descriptors() =>
        Definitions().Select(d => d.ToDescriptor()).ToList();

    // ── вердикт «стоит включать или нет» ──────────────────────────────────────────────────────

    [Fact]
    public void У_каждого_вердикта_есть_короткая_формулировка_причина_и_состояние_по_умолчанию()
    {
        foreach (var badge in Enum.GetValues<GameRecommendationBadge>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TweakVerdict.ShortRu(badge)));
            Assert.False(string.IsNullOrWhiteSpace(TweakVerdict.BadgeRu(badge)));
            Assert.False(string.IsNullOrWhiteSpace(TweakVerdict.WhyRu(badge)));
            Assert.False(string.IsNullOrWhiteSpace(TweakVerdict.DefaultStateRu(badge)));
        }
    }

    [Fact]
    public void Вердикты_различаются_между_собой_и_не_дублируют_плашки()
    {
        var shorts = Enum.GetValues<GameRecommendationBadge>().Select(TweakVerdict.ShortRu).ToList();
        Assert.Equal(shorts.Count, shorts.Distinct().Count());

        var badges = Enum.GetValues<GameRecommendationBadge>().Select(TweakVerdict.BadgeRu).ToList();
        Assert.Equal(badges.Count, badges.Distinct().Count());
    }

    [Fact]
    public void По_умолчанию_включено_только_то_что_советуем_включать()
    {
        Assert.True(TweakVerdict.EnabledByDefault(GameRecommendationBadge.Recommended));
        Assert.False(TweakVerdict.EnabledByDefault(GameRecommendationBadge.Optional));
        Assert.False(TweakVerdict.EnabledByDefault(GameRecommendationBadge.Caution));
        Assert.False(TweakVerdict.EnabledByDefault(GameRecommendationBadge.NotRecommended));
    }

    [Fact]
    public void Легенда_вердиктов_совпадает_с_перечислением()
    {
        var legend = TweakVerdict.Legend;

        Assert.Equal(Enum.GetValues<GameRecommendationBadge>().Length, legend.Count);
        Assert.All(legend, item =>
        {
            Assert.Equal(TweakVerdict.ShortRu(item.Badge), item.ShortRu);
            Assert.Equal(TweakVerdict.WhyRu(item.Badge), item.WhyRu);
        });
    }

    [Fact]
    public void Каждый_твик_каталога_имеет_вердикт_и_объяснение()
    {
        var descriptors = Descriptors();

        Assert.Equal(47, descriptors.Count);
        Assert.All(descriptors, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(TweakVerdict.ShortRu(d.Badge)), $"у твика {d.Code} нет вердикта");
            Assert.False(string.IsNullOrWhiteSpace(TweakVerdict.WhyRu(d.Badge)), $"у твика {d.Code} нет причины вердикта");
        });
    }

    [Fact]
    public void Рекомендованных_двадцать_три_и_именно_они_включены_по_умолчанию()
    {
        var descriptors = Descriptors();

        var recommended = descriptors.Where(d => TweakVerdict.EnabledByDefault(d.Badge)).ToList();
        Assert.Equal(23, recommended.Count);
        Assert.All(recommended, d => Assert.Equal(GameRecommendationBadge.Recommended, d.Badge));
    }

    [Fact]
    public void Плашки_каталога_распределены_и_сумма_равна_числу_твиков()
    {
        var byBadge = Descriptors().GroupBy(d => d.Badge).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(23, byBadge[GameRecommendationBadge.Recommended]);
        Assert.Equal(18, byBadge[GameRecommendationBadge.Optional]);
        Assert.Equal(3, byBadge[GameRecommendationBadge.Caution]);
        Assert.Equal(3, byBadge[GameRecommendationBadge.NotRecommended]);
        Assert.Equal(47, byBadge.Values.Sum());
    }

    // ── подкатегории ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Подкатегории_сходятся_с_обещанными_числами()
    {
        var byFocus = Definitions().GroupBy(d => d.EffectiveFocus).ToDictionary(g => g.Key, g => g.Count());

        // «Снятие троттлинга MMCSS» ушло из «Пинга» в «ФПС и кадры»: по документации оно не влияет
        // на задержку в матче, а ограничивает фоновый трафик — держать его в «Пинге» было неправдой.
        Assert.Equal(11, byFocus[TweakFocus.Ping]);
        Assert.Equal(7, byFocus[TweakFocus.Speed]);
        Assert.Equal(14, byFocus[TweakFocus.Fps]);
        Assert.Equal(10, byFocus[TweakFocus.Input]);
        Assert.Equal(5, byFocus[TweakFocus.System]);
        Assert.Equal(47, byFocus.Values.Sum());
    }

    [Fact]
    public void Каждая_подкатегория_непустая_иначе_плитка_в_интерфейсе_будет_пустой()
    {
        Assert.All(Enum.GetValues<TweakFocus>(), focus =>
            Assert.Contains(Definitions(), d => d.EffectiveFocus == focus));
    }

    [Fact]
    public void Твики_инпут_лага_лежат_в_подкатегории_ввода()
    {
        var descriptors = Definitions();

        Assert.All(descriptors.Where(d => d.Category == TweakCategory.InputLatency), d =>
            Assert.True(d.EffectiveFocus == TweakFocus.Input || d.EffectiveFocus == TweakFocus.Fps,
                $"твик {d.Code} про ввод не может быть в подкатегории {d.EffectiveFocus}"));
    }

    // ── пресеты: составы разные и собираются правилом ─────────────────────────────────────────

    [Fact]
    public void Три_рабочих_пресета_имеют_разные_составы_а_не_одинаковые_наборы()
    {
        var catalog = FullCatalog();

        var low = catalog.GetByPreset(TweakProfilePreset.LowLatencyGaming);
        var speed = catalog.GetByPreset(TweakProfilePreset.MaxThroughput);
        var fps = catalog.GetByPreset(TweakProfilePreset.FpsAndResponsiveness);

        Assert.Equal(19, low.Count);
        Assert.Equal(5, speed.Count);
        Assert.Equal(23, fps.Count);

        var sets = new[] { low.Select(t => t.Id).ToHashSet(), speed.Select(t => t.Id).ToHashSet(), fps.Select(t => t.Id).ToHashSet() };
        Assert.NotEqual(sets[0], sets[1]);
        Assert.NotEqual(sets[0], sets[2]);
        Assert.NotEqual(sets[1], sets[2]);

        Assert.Equal(low.Count + speed.Count + fps.Count, sets.Sum(s => s.Count));
    }

    [Fact]
    public void В_пресеты_не_попадают_твики_с_вердиктом_осторожно_или_не_советуем()
    {
        var catalog = FullCatalog();
        var presets = new[]
        {
            TweakProfilePreset.LowLatencyGaming,
            TweakProfilePreset.MaxThroughput,
            TweakProfilePreset.FpsAndResponsiveness,
        };

        foreach (var preset in presets)
        {
            Assert.All(catalog.GetByPreset(preset), t => Assert.True(
                t.Descriptor.Badge is GameRecommendationBadge.Recommended or GameRecommendationBadge.Optional,
                $"в пресет {preset} попал твик {t.Id} с плашкой {t.Descriptor.Badge}"));
        }
    }

    [Fact]
    public void Пресет_кадров_берётся_по_правилу_подкатегорий_а_не_по_ручным_пометкам()
    {
        var catalog = FullCatalog();
        var expected = catalog.GetDefinitions()
            .Where(d => PresetPolicy.IsAllowedInPreset(d.ToDescriptor(), TweakProfilePreset.FpsAndResponsiveness) &&
                        d.EffectiveFocus is TweakFocus.Fps or TweakFocus.Input or TweakFocus.System)
            .Select(d => d.Code)
            .ToHashSet();

        var actual = catalog.GetByPreset(TweakProfilePreset.FpsAndResponsiveness).Select(t => t.Id).ToHashSet();

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, code => catalog.GetDefinitions().Single(d => d.Code == code).Badge
            == GameRecommendationBadge.NotRecommended);
    }

    [Fact]
    public void Откат_и_свой_пресет_видят_весь_каталог_но_опасное_в_них_помечено()
    {
        var catalog = FullCatalog();

        var rollback = catalog.GetByPreset(TweakProfilePreset.RestoreSnapshotDefault);
        var custom = catalog.GetByPreset(TweakProfilePreset.CustomUserPreset);

        // Откат идёт по снапшоту, а «свой набор» — это выбор человека: здесь видны все твики каталога.
        Assert.Equal(47, rollback.Count);
        Assert.Equal(47, custom.Count);

        // При этом «не советуем» остаются помеченными и выключенными по умолчанию: человек может
        // включить их сам, но программа не поставит их ни молча, ни автоматически.
        var risky = rollback.Where(t => t.Descriptor.Badge == GameRecommendationBadge.NotRecommended).ToList();
        Assert.Equal(3, risky.Count);
        Assert.All(risky, t =>
        {
            Assert.False(TweakVerdict.EnabledByDefault(t.Descriptor.Badge));
            Assert.False(t.Descriptor.IncludedInLowPingPreset);
            Assert.False(t.Descriptor.IncludedInMaxSpeedPreset);
            Assert.False(t.Descriptor.IncludedInFpsPreset);
        });
    }

    [Fact]
    public void Твики_не_советуем_выключены_по_умолчанию_и_объяснены()
    {
        var notRecommended = Descriptors()
            .Where(d => d.Badge == GameRecommendationBadge.NotRecommended)
            .ToList();

        Assert.Equal(3, notRecommended.Count);
        Assert.All(notRecommended, d =>
        {
            Assert.False(TweakVerdict.EnabledByDefault(d.Badge));
            Assert.Contains("Не советуем", TweakVerdict.WhyRu(d.Badge), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(d.GamingJustificationRu));
        });
    }

    [Fact]
    public void Ни_один_твик_не_попадает_в_пресет_если_он_вредит_стабильности()
    {
        var catalog = FullCatalog();

        Assert.All(catalog.GetByPreset(TweakProfilePreset.LowLatencyGaming), t =>
            Assert.True(t.Descriptor.StabilityImpactDirection >= 0,
                $"в пресет «низкий пинг» попал твик {t.Id}, который ухудшает стабильность"));
    }

    [Fact]
    public void Каталог_проходит_проверки_целостности_и_запретов()
    {
        var tweaks = FullCatalog().GetAllTweaks();

        Assert.Empty(PresetPolicy.ValidateCatalog(tweaks));
        Assert.Empty(CatalogSafetyAudit.AuditCatalog(tweaks));
    }
}
