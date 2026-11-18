using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Подпись «что именно отмечено»: разбивка выбора по назначению и её повтор в подтверждении.
///
/// Зачем: подтверждение раньше называло только число («применить 12 твиков?»), а число ничего не
/// говорит о составе — можно незаметно применить двенадцать правок ввода и ни одной сетевой.
/// Разбивка считается по строкам, поэтому подпись не может разойтись с тем, что уйдёт в службу.
/// </summary>
public sealed class TweakSelectionSummaryTests
{
    private static TweakRowViewModel Row(string code, string focus, bool recommended) =>
        new(
            code,
            $"Твик {code}",
            "Что делает твик.",
            TweakVerdict.ShortRu(recommended ? GameRecommendationBadge.Recommended : GameRecommendationBadge.Optional),
            "почему так",
            isRecommended: recommended,
            enabledByDefault: recommended,
            isInLowPingPreset: false,
            isInMaxSpeedPreset: false,
            isInFpsPreset: false,
            focusRu: focus,
            isDangerous: false);

    private static (TweaksViewModel Tweaks, FakeDialogs Dialogs) ViewModel()
    {
        var dialogs = new FakeDialogs();
        var rows = new List<TweakRowViewModel>
        {
            Row("DNS_CACHE", "Пинг и сеть", recommended: true),
            Row("TCP_ACK", "Пинг и сеть", recommended: true),
            Row("GAME_DVR", "Кадры", recommended: true),
            Row("MOUSE_ACCEL", "Ввод", recommended: false),
            Row("TEMP_CLEAN", "Система", recommended: false),
            Row("NEW_FOCUS_THING", "Новая подкатегория", recommended: false),
        };

        var presets = new List<PresetViewModel>
        {
            new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 2),
        };

        return (new TweaksViewModel(new EngineClient(new FakeEngineChannel()), dialogs, rows, presets), dialogs);
    }

    [Fact]
    public void Пустой_выбор_не_рисует_разбивку()
    {
        (TweaksViewModel tweaks, _) = ViewModel();
        tweaks.SelectNone();

        Assert.Equal(string.Empty, tweaks.SelectionBreakdownRu);
        Assert.Equal("Отмечено 0 из 6", tweaks.SelectionLineRu);
    }

    [Fact]
    public void Разбивка_считает_отмеченные_по_назначению()
    {
        (TweaksViewModel tweaks, _) = ViewModel();

        Assert.Equal("Пинг и сеть — 2 · Кадры — 1", tweaks.SelectionBreakdownRu);

        tweaks.SelectAll();

        Assert.Equal(
            "Пинг и сеть — 2 · Кадры — 1 · Ввод — 1 · Система — 1 · Новая подкатегория — 1",
            tweaks.SelectionBreakdownRu);
    }

    [Fact]
    public void Порядок_подкатегорий_как_в_каталоге_а_незнакомая_в_конце()
    {
        (TweaksViewModel tweaks, _) = ViewModel();
        tweaks.SelectAll();

        string breakdown = tweaks.SelectionBreakdownRu;

        Assert.True(
            breakdown.IndexOf("Пинг и сеть", StringComparison.Ordinal) < breakdown.IndexOf("Кадры", StringComparison.Ordinal));
        Assert.True(
            breakdown.IndexOf("Кадры", StringComparison.Ordinal) < breakdown.IndexOf("Ввод", StringComparison.Ordinal));
        Assert.True(
            breakdown.IndexOf("Система", StringComparison.Ordinal) < breakdown.IndexOf("Новая подкатегория", StringComparison.Ordinal));
    }

    [Fact]
    public void Разбивка_обновляется_при_переключении_ползунка()
    {
        (TweaksViewModel tweaks, _) = ViewModel();
        bool raised = false;
        tweaks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TweaksViewModel.SelectionBreakdownRu))
            {
                raised = true;
            }
        };

        tweaks.Rows.Single(row => row.Code == "MOUSE_ACCEL").IsOn = true;

        Assert.True(raised);
        Assert.Contains("Ввод — 1", tweaks.SelectionBreakdownRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Подтверждение_называет_состав_выбора()
    {
        (TweaksViewModel tweaks, FakeDialogs dialogs) = ViewModel();

        await tweaks.ApplyCheckedAsync();

        (string title, string message, _, _) = dialogs.Confirmations.Single();
        Assert.Equal("Применить 3 твика?", title);
        Assert.Contains("Что именно: Пинг и сеть — 2 · Кадры — 1.", message, StringComparison.Ordinal);
        Assert.Contains("запомнит прежние значения", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Отмена_подтверждения_не_меняет_ничего_и_не_шлёт_команду()
    {
        var channel = new FakeEngineChannel();
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var rows = new List<TweakRowViewModel> { Row("DNS_CACHE", "Пинг и сеть", recommended: true) };
        var tweaks = new TweaksViewModel(
            new EngineClient(channel),
            dialogs,
            rows,
            new List<PresetViewModel> { new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 1) });

        await tweaks.ApplyCheckedAsync();

        Assert.Empty(channel.Requests);
        Assert.Contains("отменено", tweaks.StatusRu, StringComparison.Ordinal);
        Assert.True(rows[0].IsOn);
    }
}
