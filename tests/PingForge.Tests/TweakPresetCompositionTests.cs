using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// «Показать состав» у пресета: до применения человек может увидеть, какие именно твики изменятся.
///
/// Зачем это тестами: пресет — это кнопка, которая меняет десятки настроек Windows. Раньше она
/// называла только число («применится 19»), и проверить, что внутри, можно было лишь применив.
/// Состав берётся из того же вызова каталога, которым пользуется служба, поэтому подпись и кнопка
/// физически не могут разойтись с тем, что применится.
/// </summary>
public sealed class TweakPresetCompositionTests
{
    private static TweakCatalog Catalog() => new(new TweakPlatformPorts());

    private static TweaksViewModel ViewModel(TweakCatalog catalog, IReadOnlyList<PresetViewModel>? presets = null) =>
        new(
            new EngineClient(new FakeEngineChannel()),
            new FakeDialogs(),
            TweakRowFactory.BuildAll(catalog),
            presets ?? TweakRowFactory.BuildPresets(catalog));

    [Fact]
    public void Состав_пресета_совпадает_с_числом_в_подписи()
    {
        TweakCatalog catalog = Catalog();

        IReadOnlyList<PresetViewModel> presets = TweakRowFactory.BuildPresets(catalog);

        Assert.Equal(3, presets.Count);
        Assert.All(presets, preset =>
        {
            Assert.True(preset.Count > 0);
            Assert.Equal(preset.Count, preset.CodesOrEmpty.Count);
            Assert.True(preset.HasCodes);
            // Коды уникальны: один твик не может попасть в пресет дважды.
            Assert.Equal(preset.CodesOrEmpty.Count, preset.CodesOrEmpty.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public void Состав_пресета_совпадает_с_каталогом_по_кодам()
    {
        TweakCatalog catalog = Catalog();
        var rowCodes = TweakRowFactory.BuildAll(catalog).Select(row => row.Code).ToHashSet(StringComparer.Ordinal);

        foreach (PresetViewModel preset in TweakRowFactory.BuildPresets(catalog))
        {
            Assert.All(preset.CodesOrEmpty, code => Assert.Contains(code, rowCodes));
        }
    }

    [Fact]
    public void Показать_состав_фильтрует_карточки_и_не_трогает_отметки()
    {
        TweaksViewModel tweaks = ViewModel(Catalog());
        PresetViewModel preset = tweaks.Presets[0];
        tweaks.SelectedPreset = preset;
        int checkedBefore = tweaks.CheckedCount;

        tweaks.ShowPresetTweaks();

        Assert.True(tweaks.IsFiltered);
        Assert.Equal(preset.CodesOrEmpty.Count, tweaks.VisibleRows.Count);
        Assert.Equal(
            preset.CodesOrEmpty.OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            tweaks.VisibleRows.Select(row => row.Code).OrderBy(code => code, StringComparer.Ordinal).ToArray());
        Assert.Equal(checkedBefore, tweaks.CheckedCount);
        Assert.Contains("Отметки и ползунки не менялись", tweaks.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Без_выбранного_пресета_кнопка_недоступна_и_состав_не_включается()
    {
        TweaksViewModel tweaks = ViewModel(Catalog());

        Assert.Null(tweaks.SelectedPreset);
        Assert.False(tweaks.ShowPresetCommand.CanExecute(null));

        tweaks.ShowPresetTweaks();

        Assert.False(tweaks.IsFiltered);
        Assert.Contains("Пресет не выбран", tweaks.StatusRu, StringComparison.Ordinal);

        tweaks.SelectedPreset = tweaks.Presets[1];
        Assert.True(tweaks.ShowPresetCommand.CanExecute(null));
    }

    [Fact]
    public void Пресет_без_состава_честно_говорит_об_этом()
    {
        TweakCatalog catalog = Catalog();
        var безСостава = new List<PresetViewModel>
        {
            new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 19),
        };

        TweaksViewModel tweaks = ViewModel(catalog, безСостава);
        tweaks.SelectedPreset = tweaks.Presets[0];

        Assert.False(tweaks.Presets[0].HasCodes);
        tweaks.ShowPresetTweaks();

        Assert.False(tweaks.IsFiltered);
        Assert.Contains("каталог не отдал", tweaks.StatusRu, StringComparison.Ordinal);
        Assert.Contains("Применение пресета от этого не меняется", tweaks.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Состав_пресета_складывается_с_поиском()
    {
        TweaksViewModel tweaks = ViewModel(Catalog());
        tweaks.SelectedPreset = tweaks.Presets[2];
        tweaks.ShowPresetTweaks();

        int afterComposition = tweaks.VisibleRows.Count;
        tweaks.SearchRu = "zzz-такого-нет";

        Assert.Empty(tweaks.VisibleRows);
        Assert.True(tweaks.IsFiltered);
        Assert.True(afterComposition > 0);

        tweaks.ClearSearch();
        Assert.Equal(afterComposition, tweaks.VisibleRows.Count);
    }
}
