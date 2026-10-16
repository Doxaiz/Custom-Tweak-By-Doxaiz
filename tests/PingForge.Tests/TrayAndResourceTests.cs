using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Трей и экономный профиль. Здесь проверяется ровно то, что пользователь называет «программа грузит
/// компьютер» и «пусть работает в трее»: цифры нагрузки, паузы и меню значка без вранья.
/// </summary>
public sealed class TrayAndResourceTests
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(2);

    private static RealtimeSnapshot Snapshot(
        RealtimeOptimizationState state,
        double? currentRttMs = null,
        double? baselineRttMs = null,
        double? improvementMs = null) => new(
        State: state,
        StatusRu: "тест",
        GameTitleRu: "Fortnite",
        Endpoint: "3.4.5.6:9000",
        BaselineRttMs: baselineRttMs,
        CurrentRttMs: currentRttMs,
        FloorRttMs: null,
        CurrentJitterMs: null,
        CurrentLossPercent: null,
        ImprovementMs: improvementMs,
        QualityLabelRu: "тест",
        AppliedActions: Array.Empty<OptimizationActionDescriptor>(),
        Warnings: Array.Empty<string>(),
        UpdatedUtc: new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc));

    // ── профили потребления ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Профили_растягивают_интервал_ровно_как_обещано()
    {
        Assert.Equal(3.0, new ResourceBudgetOptions(PerfProfile.Economy).IntervalMultiplier);
        Assert.Equal(1.0, new ResourceBudgetOptions(PerfProfile.Balanced).IntervalMultiplier);
        Assert.Equal(0.5, new ResourceBudgetOptions(PerfProfile.MaxPerformance).IntervalMultiplier);

        Assert.Equal(TimeSpan.FromSeconds(6), new ResourceBudgetOptions(PerfProfile.Economy).EffectiveMonitorInterval(BaseInterval));
        Assert.Equal(TimeSpan.FromSeconds(2), new ResourceBudgetOptions(PerfProfile.Balanced).EffectiveMonitorInterval(BaseInterval));
        Assert.Equal(TimeSpan.FromSeconds(1), new ResourceBudgetOptions(PerfProfile.MaxPerformance).EffectiveMonitorInterval(BaseInterval));
    }

    [Fact]
    public void Даже_самый_быстрый_профиль_не_опускается_ниже_полусекунды()
    {
        var aggressive = new ResourceBudgetOptions(PerfProfile.MaxPerformance);
        Assert.Equal(TimeSpan.FromMilliseconds(500), aggressive.EffectiveMonitorInterval(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.FromMilliseconds(500), aggressive.EffectiveMonitorInterval(TimeSpan.Zero));
    }

    [Fact]
    public void Экономный_профиль_тратит_меньше_проб_чем_обычный_а_обычный_меньше_чем_максимум()
    {
        int economy = new ResourceBudgetOptions(PerfProfile.Economy).MeasurementsPerMinute(BaseInterval, 12);
        int balanced = new ResourceBudgetOptions(PerfProfile.Balanced).MeasurementsPerMinute(BaseInterval, 12);
        int max = new ResourceBudgetOptions(PerfProfile.MaxPerformance).MeasurementsPerMinute(BaseInterval, 12);

        Assert.True(economy < balanced, $"экономно ({economy}) должно быть меньше обычного ({balanced})");
        Assert.True(balanced < max, $"обычный ({balanced}) должен быть меньше максимума ({max})");
        Assert.Equal(120, economy);
        Assert.Equal(360, balanced);
        Assert.Equal(720, max);
    }

    [Fact]
    public void Число_проб_в_минуту_не_может_быть_нулевым_даже_при_нулевых_пробах_в_окне()
    {
        int perMinute = new ResourceBudgetOptions(PerfProfile.Economy).MeasurementsPerMinute(BaseInterval, 0);
        Assert.Equal(10, perMinute);
    }

    [Fact]
    public void Описание_профиля_говорит_и_про_частоту_и_про_паузы()
    {
        var text = new ResourceBudgetOptions(PerfProfile.Economy).DescribeRu(BaseInterval, 12);

        Assert.Contains("экономно", text);
        Assert.Contains("каждые 6", text);
        Assert.Contains("120 проб в минуту", text);
        Assert.Contains("паузу", text);
        Assert.Contains("свёрнута", text);
        Assert.Contains("5 мин", text);
    }

    [Fact]
    public void Если_пользователь_выключил_паузы_это_видно_в_описании()
    {
        var text = new ResourceBudgetOptions(PerfProfile.Balanced, PauseWhenOnBattery: false, PauseWhenGameMinimized: false)
            .DescribeRu(BaseInterval, 12);

        Assert.Contains("продолжает работать", text);
        Assert.Contains("идут и при свёрнутой игре", text);
        Assert.DoesNotContain("встаёт на паузу", text);
    }

    [Fact]
    public void По_умолчанию_паузы_включены_и_игра_снимается_через_пять_минут()
    {
        var defaults = new ResourceBudgetOptions();

        Assert.True(defaults.PauseWhenOnBattery);
        Assert.True(defaults.PauseWhenGameMinimized);
        Assert.Equal(5, defaults.StopAfterGameClosedMinutes);
        Assert.Equal(2, defaults.TrayIconRefreshSeconds);
        Assert.Equal(PerfProfile.Balanced, defaults.Profile);
    }

    [Fact]
    public void Русские_названия_профилей_на_месте()
    {
        Assert.Equal("экономно", ResourceBudgetOptions.DescribeProfile(PerfProfile.Economy));
        Assert.Equal("обычный", ResourceBudgetOptions.DescribeProfile(PerfProfile.Balanced));
        Assert.Equal("максимум реакции", ResourceBudgetOptions.DescribeProfile(PerfProfile.MaxPerformance));
    }

    // ── меню трея ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Когда_оптимизация_работает_первым_пунктом_идёт_остановка()
    {
        var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, improvementMs: 4),
            isAdministrator: true, revertableChangeCount: 2);

        Assert.Equal(TrayCommand.StopOptimization, menu[0].Command);
        Assert.Contains("58 мс", menu[0].TooltipRu);
        Assert.DoesNotContain(menu, m => m.Command == TrayCommand.StartOptimization);
    }

    [Fact]
    public void Когда_оптимизация_не_запущена_первым_пунктом_идёт_запуск()
    {
        var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.Idle), isAdministrator: true, revertableChangeCount: 0);

        Assert.Equal(TrayCommand.StartOptimization, menu[0].Command);
        Assert.True(menu[0].IsEnabled);
        Assert.DoesNotContain(menu, m => m.Command == TrayCommand.StopOptimization);
    }

    [Fact]
    public void Без_прав_администратора_пункт_запуска_честно_говорит_что_будут_только_замеры()
    {
        var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.ReadOnlyNoAdmin, currentRttMs: 64),
            isAdministrator: false, revertableChangeCount: 0);

        var start = menu.Single(m => m.Command == TrayCommand.StartOptimization);
        Assert.True(start.IsEnabled, "замеры доступны и без прав");
        Assert.Contains("только замеры", start.TooltipRu);
        Assert.Contains("менять сеть нельзя", start.TooltipRu);
    }

    [Fact]
    public void Пока_идёт_поиск_игры_запуск_из_трея_заблокирован_чтобы_не_запускать_дважды()
    {
        var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.FindingGame),
            isAdministrator: true, revertableChangeCount: 0);

        Assert.False(menu.Single(m => m.Command == TrayCommand.StartOptimization).IsEnabled);
    }

    [Fact]
    public void Возврат_изменений_появляется_только_когда_есть_что_возвращать_и_с_числом()
    {
        var without = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.Idle), true, revertableChangeCount: 0);
        Assert.DoesNotContain(without, m => m.Command == TrayCommand.RevertAllChanges);

        var with = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58), true, revertableChangeCount: 3);
        var revert = with.Single(m => m.Command == TrayCommand.RevertAllChanges);
        Assert.Equal("Вернуть как было (3)", revert.TitleRu);
        Assert.True(revert.IsEnabled);
    }

    [Fact]
    public void В_меню_всегда_есть_окно_настройки_и_выход()
    {
        foreach (bool admin in new[] { true, false })
        {
            var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.Idle), admin, 0);
            Assert.Contains(menu, m => m.Command == TrayCommand.ShowWindow);
            Assert.Contains(menu, m => m.Command == TrayCommand.OpenSettings);
            Assert.Contains(menu, m => m.Command == TrayCommand.Exit);
            Assert.All(menu, m =>
            {
                Assert.False(string.IsNullOrWhiteSpace(m.TitleRu));
                Assert.False(string.IsNullOrWhiteSpace(m.TooltipRu));
            });
        }
    }

    [Fact]
    public void Выход_предупреждает_что_изменения_будут_сняты()
    {
        var exit = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58), true, 0)
            .Single(m => m.Command == TrayCommand.Exit);

        Assert.Contains("Изменения будут сняты", exit.TooltipRu);
    }

    [Fact]
    public void Ни_один_пункт_меню_не_обещает_молчаливых_изменений()
    {
        var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58), true, 2);
        string all = string.Join(" | ", menu.Select(m => m.TitleRu + " " + m.TooltipRu));

        Assert.DoesNotContain("тихо", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("автоматически изменит", all, StringComparison.OrdinalIgnoreCase);
    }

    // ── подсказка рядом со значком ─────────────────────────────────────────────────────────────

    [Fact]
    public void Без_замеров_подсказка_прямо_говорит_что_пинга_нет_а_не_рисует_ноль()
    {
        string tooltip = TrayMenuBuilder.TooltipFor(Snapshot(RealtimeOptimizationState.Idle));

        Assert.Contains("Пинга нет — замеров пока не было", tooltip);
        Assert.DoesNotContain("0 мс", tooltip);
    }

    [Fact]
    public void При_измеренном_пинге_подсказка_показывает_цифру_и_выигрыш()
    {
        string tooltip = TrayMenuBuilder.TooltipFor(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, baselineRttMs: 62, improvementMs: 4));

        Assert.Contains("пинг 58 мс", tooltip);
        Assert.Contains("выигрыш 4 мс", tooltip);
        Assert.Contains("оптимизация работает", tooltip);
    }

    [Fact]
    public void Если_выигрыша_нет_подсказка_не_хвастается_а_говорит_правду()
    {
        string tooltip = TrayMenuBuilder.TooltipFor(
            Snapshot(RealtimeOptimizationState.Degraded, currentRttMs: 70, baselineRttMs: 62, improvementMs: -8));

        Assert.Contains("выигрыша к исходному нет", tooltip);
        Assert.Contains("просело", tooltip);
    }

    [Fact]
    public void Пол_задержки_не_выдаётся_за_текущий_пинг()
    {
        // Планка (floor) — это лучшее, что физически возможно; в подсказке трея её быть не должно.
        string tooltip = TrayMenuBuilder.TooltipFor(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, baselineRttMs: 62, improvementMs: 4));

        Assert.Contains("пинг 58 мс", tooltip);
        Assert.DoesNotContain("46 мс", tooltip);
    }

    [Fact]
    public void Без_прав_подсказка_честно_называет_состояние()
    {
        string tooltip = TrayMenuBuilder.TooltipFor(
            Snapshot(RealtimeOptimizationState.ReadOnlyNoAdmin, currentRttMs: 64));

        Assert.Contains("только замеры (нет прав)", tooltip);
        Assert.Contains("пинг 64 мс", tooltip);
    }

    [Fact]
    public void Все_состояния_движка_имеют_человеческое_описание_в_трее()
    {
        foreach (var state in Enum.GetValues<RealtimeOptimizationState>())
        {
            string tooltip = TrayMenuBuilder.TooltipFor(Snapshot(state, currentRttMs: 50));
            Assert.False(string.IsNullOrWhiteSpace(tooltip));
            Assert.StartsWith("Custom Tweak:", tooltip);
            Assert.DoesNotContain("undefined", tooltip, StringComparison.OrdinalIgnoreCase);
        }
    }
}
