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
        double? improvementMs = null,
        double? floorRttMs = null,
        double? currentJitterMs = null,
        double? currentLossPercent = null,
        string? finalMethodTag = null,
        string? baselineMethodTag = null) => new(
        State: state,
        StatusRu: "тест",
        GameTitleRu: "Fortnite",
        Endpoint: "3.4.5.6:9000",
        BaselineRttMs: baselineRttMs,
        CurrentRttMs: currentRttMs,
        FloorRttMs: floorRttMs,
        CurrentJitterMs: currentJitterMs,
        CurrentLossPercent: currentLossPercent,
        ImprovementMs: improvementMs,
        QualityLabelRu: "тест",
        AppliedActions: Array.Empty<OptimizationActionDescriptor>(),
        Warnings: Array.Empty<string>(),
        UpdatedUtc: new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc),
        BaselineMethodTag: baselineMethodTag ?? string.Empty,
        FinalMethodTag: finalMethodTag ?? string.Empty);

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
    public void Меню_трея_называет_разброс_и_потери_а_неизмеренное_не_делает_нулём()
    {
        var measured = TrayMenuBuilder.Build(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, currentJitterMs: 3.1, currentLossPercent: 0),
            true, 0);

        Assert.Contains("разброс 3.1 мс", measured[0].TooltipRu, StringComparison.Ordinal);
        Assert.Contains("потери 0 %", measured[0].TooltipRu, StringComparison.Ordinal);

        var unknown = TrayMenuBuilder.Build(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58),
            true, 0);

        Assert.Contains("разброс не измерен", unknown[0].TooltipRu, StringComparison.Ordinal);
        Assert.Contains("потери не измерены", unknown[0].TooltipRu, StringComparison.Ordinal);
        Assert.DoesNotContain("разброс 0", unknown[0].TooltipRu, StringComparison.Ordinal);
        Assert.DoesNotContain("потери 0", unknown[0].TooltipRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Текст_у_значка_не_показывает_ноль_вместо_неизмеренного()
    {
        string tooltip = TrayMenuBuilder.TooltipFor(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, currentJitterMs: null, currentLossPercent: null));

        Assert.Contains("пинг 58 мс", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("разброс 0", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Когда_оптимизация_не_запущена_первым_пунктом_идёт_запуск()
    {
        var menu = TrayMenuBuilder.Build(Snapshot(RealtimeOptimizationState.Idle), true, 0);

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
            true, 0);

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
        // Значок короткий: «пинг + состояние» не оставляют места выигрышу, и он не выдумывается
        // и не показывается обрезанным. Остаются пинг и честное состояние, а выигрыш — в меню.
        var snapshot = Snapshot(RealtimeOptimizationState.Degraded, currentRttMs: 70, baselineRttMs: 62, improvementMs: -8);

        string tooltip = TrayMenuBuilder.TooltipFor(snapshot);

        Assert.Contains("пинг 70 мс", tooltip);
        Assert.Contains("просело", tooltip);
        Assert.DoesNotContain("выигрыш", tooltip);

        var menu = TrayMenuBuilder.Build(snapshot, isAdministrator: true, revertableChangeCount: 0);
        string stop = menu.Single(m => m.Command == TrayCommand.StopOptimization).TooltipRu;

        Assert.Contains("сейчас 70 мс", stop);
        Assert.Contains("выигрыша к исходному нет", stop);
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

    // ── способ замера в трее ───────────────────────────────────────────────────────────────────

    /// <summary>Способы, которые служба действительно пишет в снимок. Незнакомая метка тоже в списке:
    /// она не должна превращаться в выдуманный «живой поток».</summary>
    private static readonly string[] AllTags =
    [
        PingForge.Core.Models.ProbeMethodTags.LiveTcp,
        PingForge.Core.Models.ProbeMethodTags.LiveUdp,
        PingForge.Core.Models.ProbeMethodTags.Icmp,
        PingForge.Core.Models.ProbeMethodTags.TcpHandshake,
        PingForge.Core.Models.ProbeMethodTags.LegacyLiveFlow,
        "новая-метка-из-будущего",
    ];

    [Fact]
    public void Трей_называет_способ_замера_тем_же_словом_что_и_окно()
    {
        foreach (string tag in AllTags)
        {
            var menu = TrayMenuBuilder.Build(
                Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, finalMethodTag: tag),
                isAdministrator: true, revertableChangeCount: 0);

            string stop = menu.Single(m => m.Command == TrayCommand.StopOptimization).TooltipRu;

            Assert.Contains("способ", stop, StringComparison.Ordinal);
            Assert.Contains(PingForge.Core.Models.ProbeMethodTags.Ru(tag), stop, StringComparison.Ordinal);

            string tooltip = TrayMenuBuilder.TooltipFor(
                Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58, finalMethodTag: tag));

            Assert.Contains(PingForge.Core.Models.ProbeMethodTags.ShortRu(tag), tooltip, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Без_названного_способа_трей_о_способе_молчит_а_не_выдумывает_его()
    {
        var menu = TrayMenuBuilder.Build(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58),
            isAdministrator: true, revertableChangeCount: 0);

        string stop = menu.Single(m => m.Command == TrayCommand.StopOptimization).TooltipRu;
        string tooltip = TrayMenuBuilder.TooltipFor(
            Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58));

        Assert.Equal(string.Empty, TrayMenuBuilder.MethodRu(Snapshot(RealtimeOptimizationState.ActiveMonitoring, currentRttMs: 58)));
        Assert.DoesNotContain("способ", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("живой", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("ICMP", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("TCP", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Разные_способы_до_и_после_видны_в_меню_трея()
    {
        var menu = TrayMenuBuilder.Build(
            Snapshot(
                RealtimeOptimizationState.ActiveMonitoring,
                currentRttMs: 58,
                finalMethodTag: PingForge.Core.Models.ProbeMethodTags.TcpHandshake,
                baselineMethodTag: PingForge.Core.Models.ProbeMethodTags.LiveTcp),
            isAdministrator: true, revertableChangeCount: 0);

        string stop = menu.Single(m => m.Command == TrayCommand.StopOptimization).TooltipRu;

        Assert.Contains("«до»: живой поток игры (TCP-статистика)", stop, StringComparison.Ordinal);
        Assert.Contains("«после»: TCP-рукопожатие", stop, StringComparison.Ordinal);
    }

    [Fact]
    public void Надпись_у_значка_помещается_в_предел_и_не_рвётся_посреди_слова()
    {
        // Windows обрезает подсказку значка сама: «… · оптимизаци» — это не подсказка. Значит надпись
        // обязана уложиться в предел, а не укорачиваться ножницами в окне.
        foreach (var state in Enum.GetValues<RealtimeOptimizationState>())
        {
            string?[] tags = [.. AllTags, null];
            foreach (string? tag in tags)
            {
                foreach (double? rtt in new double?[] { null, 58, 1234 })
                {
                    foreach (double? gain in new double?[] { null, 4, -8 })
                    {
                        var snapshot = Snapshot(
                            state,
                            currentRttMs: rtt,
                            baselineRttMs: rtt is null ? null : 62,
                            improvementMs: gain,
                            finalMethodTag: tag);

                        string tooltip = TrayMenuBuilder.TooltipFor(snapshot);

                        Assert.True(
                            tooltip.Length <= TrayMenuBuilder.MaxIconTextLength,
                            $"надпись длиннее предела ({tooltip.Length}): «{tooltip}»");
                        Assert.StartsWith("Custom Tweak:", tooltip, StringComparison.Ordinal);
                        Assert.False(
                            char.IsWhiteSpace(tooltip[^1]),
                            $"надпись кончается пробелом: «{tooltip}»");
                        Assert.False(
                            tooltip.EndsWith("·", StringComparison.Ordinal),
                            $"надпись кончается разделителем: «{tooltip}»");
                    }
                }
            }
        }
    }

    [Fact]
    public void В_тесной_надписи_способ_уступает_место_только_выигрышу_а_не_наоборот()
    {
        // Длинное состояние и длинный способ вместе с выигрышем не влезают: выигрыш уходит
        // в подсказку меню, а состояние и способ остаются. Выдуманных чисел при этом не появляется.
        var snapshot = Snapshot(
            RealtimeOptimizationState.Degraded,
            currentRttMs: 1234,
            baselineRttMs: 62,
            improvementMs: -8,
            finalMethodTag: PingForge.Core.Models.ProbeMethodTags.TcpHandshake);

        string tooltip = TrayMenuBuilder.TooltipFor(snapshot);

        Assert.Contains("пинг 1234 мс", tooltip, StringComparison.Ordinal);
        Assert.Contains("TCP-рукопожатие", tooltip, StringComparison.Ordinal);
        Assert.Contains("просело", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("выигрыш", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("выигрыша к исходному нет", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Короткое_имя_способа_не_длиннее_надписи_и_не_молчит()
    {
        foreach (string tag in AllTags)
        {
            string shortName = PingForge.Core.Models.ProbeMethodTags.ShortRu(tag);
            Assert.False(string.IsNullOrWhiteSpace(shortName));
            Assert.True(shortName.Length <= 20, $"слишком длинное имя способа: «{shortName}»");
        }

        Assert.Equal("способ не назван", PingForge.Core.Models.ProbeMethodTags.ShortRu(null));
        Assert.Equal("способ не назван", PingForge.Core.Models.ProbeMethodTags.ShortRu(""));
        // Незнакомая метка — незнакомая, а не «живой поток»: не выдаём одно за другое.
        Assert.Equal("способ незнаком", PingForge.Core.Models.ProbeMethodTags.ShortRu("новая-метка-из-будущего"));
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
