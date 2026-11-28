using PingForge.Core.Models;

namespace PingForge.Orchestration.Realtime;

/// <summary>
/// Профиль нагрузки на компьютер. Смысл простой: живая оптимизация — это частые замеры, а замеры
/// занимают и процессор, и сеть. Пользователь должен сам выбрать, чем платить: точностью реакции
/// или тишиной в диспетчере задач.
/// </summary>
public enum PerfProfile
{
    /// <summary>Экономно: реже замеры, пауза на батарее и когда игра свёрнута. Для слабых ноутбуков.</summary>
    Economy,

    /// <summary>Обычный: интервалы как в движке (2 с), пауза только когда игра свёрнута.</summary>
    Balanced,

    /// <summary>Максимум реакции: замеры вдвое чаще. Заметнее нагрузка, нужен только для турнирных матчей.</summary>
    MaxPerformance
}

/// <summary>
/// Бюджет ресурсов живой оптимизации. Все правила здесь — про то, чтобы программа не мешала играть:
/// вся тяжёлая работа (замеры, чтение таблиц Windows) идёт порциями, а не постоянным потоком.
/// </summary>
public sealed record ResourceBudgetOptions(
    PerfProfile Profile = PerfProfile.Balanced,
    bool PauseWhenOnBattery = true,
    bool PauseWhenGameMinimized = true,
    int StopAfterGameClosedMinutes = 5,
    int TrayIconRefreshSeconds = 2)
{
    /// <summary>Во сколько раз растягивается интервал контроля по сравнению с базовым (2 с).</summary>
    public double IntervalMultiplier => Profile switch
    {
        PerfProfile.Economy => 3.0,
        PerfProfile.MaxPerformance => 0.5,
        _ => 1.0
    };

    public TimeSpan EffectiveMonitorInterval(TimeSpan baseInterval)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(baseInterval.TotalMilliseconds * IntervalMultiplier);
        return interval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : interval;
    }

    /// <summary>
    /// Сколько раз за минуту программа обращается к сети и к таблицам Windows. Это честная цифра
    /// нагрузки: пользователь видит её в интерфейсе, а не догадывается.
    /// </summary>
    public int MeasurementsPerMinute(TimeSpan baseInterval, int samplesPerWindow)
    {
        double windowSeconds = EffectiveMonitorInterval(baseInterval).TotalSeconds;
        double windowCount = 60.0 / windowSeconds;
        return (int)Math.Round(windowCount * Math.Max(1, samplesPerWindow), MidpointRounding.AwayFromZero);
    }

    public string DescribeRu(TimeSpan baseInterval, int samplesPerWindow) =>
        $"Профиль «{DescribeProfile(Profile)}»: контроль каждые {EffectiveMonitorInterval(baseInterval).TotalSeconds:0.#} с, " +
        $"до {MeasurementsPerMinute(baseInterval, samplesPerWindow)} проб в минуту. " +
        (PauseWhenOnBattery ? "На батарее оптимизация встаёт на паузу. " : "На батарее оптимизация продолжает работать. ") +
        (PauseWhenGameMinimized ? "Когда игра свёрнута — замеры останавливаются. " : "Замеры идут и при свёрнутой игре. ") +
        $"Если игра закрыта, оптимизация сама останавливается через {StopAfterGameClosedMinutes} мин и снимает изменения.";

    public static string DescribeProfile(PerfProfile profile) => profile switch
    {
        PerfProfile.Economy => "экономно",
        PerfProfile.MaxPerformance => "максимум реакции",
        _ => "обычный"
    };
}

public enum TrayCommand
{
    StartOptimization,
    StopOptimization,
    ShowWindow,
    OpenSettings,
    RevertAllChanges,
    Exit
}

public sealed record TrayMenuItem(
    TrayCommand Command,
    string TitleRu,
    bool IsEnabled,
    string TooltipRu,
    bool IsChecked = false);

/// <summary>
/// Меню значка в трее. Логика отделена от Windows: её можно проверить тестами, а WPF-слой только
/// показывает то, что здесь получилось. Правила: ни одного действия, которое молча меняет систему;
/// инструментальные подсказки — с цифрами, но без обещаний.
/// </summary>
public static class TrayMenuBuilder
{
    public static IReadOnlyList<TrayMenuItem> Build(
        RealtimeSnapshot snapshot,
        bool isAdministrator,
        int revertableChangeCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool isRunning = snapshot.IsRunning;
        string pingText = snapshot.CurrentRttMs is { } current
            ? $"сейчас {current:0} мс"
            : "замеров пока нет";

        var items = new List<TrayMenuItem>
        {
            isRunning
                ? new TrayMenuItem(TrayCommand.StopOptimization, "Остановить оптимизацию",
                    true, StopTooltipRu(snapshot, pingText))
                : new TrayMenuItem(TrayCommand.StartOptimization, "Оптимизировать соединение",
                    isAdministrator ? snapshot.State != RealtimeOptimizationState.FindingGame : true,
                    isAdministrator
                        ? "Найти запущенную игру, замерить пинг и попробовать сделать лучше"
                        : "Без прав администратора будут только замеры: менять сеть нельзя — нажмите, чтобы увидеть цифры"),
        };

        if (revertableChangeCount > 0)
        {
            items.Add(new TrayMenuItem(TrayCommand.RevertAllChanges,
                $"Вернуть как было ({revertableChangeCount})",
                true,
                "Снять все изменения этого сеанса: маршруты и политики, которые поставила программа"));
        }

        items.Add(new TrayMenuItem(TrayCommand.ShowWindow, "Показать окно", true, "Открыть главное окно программы"));
        items.Add(new TrayMenuItem(TrayCommand.OpenSettings, "Настройки…", true, "Профиль, замеры, трей, экономия ресурсов"));
        items.Add(new TrayMenuItem(TrayCommand.Exit, "Выход", true,
            "Закрыть программу. Изменения будут сняты: без работающего приложения маршрут и политика QoS не нужны"));

        return items;
    }

    /// <summary>
    /// Способ замера словами для трея: та же пара «до/после», что называет окно, — чтобы человек
    /// не читал в окне один способ, а в меню другой. Способ не назван — строки нет вовсе.
    /// </summary>
    public static string MethodRu(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool known = !string.IsNullOrWhiteSpace(snapshot.FinalMethodTag)
            || !string.IsNullOrWhiteSpace(snapshot.BaselineMethodTag);

        return known
            ? ProbeMethodTags.DescribePairRu(snapshot.BaselineMethodTag, snapshot.FinalMethodTag)
            : string.Empty;
    }

    private static string StopTooltipRu(RealtimeSnapshot snapshot, string pingText)
    {
        var facts = new List<string>(4) { pingText, SpreadRu(snapshot) };

        // Выигрыш живёт здесь, а не в надписи значка: в 63 знака он не всегда влезает, а терять
        // его совсем нельзя — в меню место есть.
        if (snapshot.ImprovementMs is { } gain)
        {
            facts.Add(gain >= 1 ? $"выигрыш {gain:0} мс к исходному" : "выигрыша к исходному нет");
        }

        string method = MethodRu(snapshot);
        if (method.Length > 0)
        {
            facts.Add(method);
        }

        return $"Снять маршрут и политику QoS, вернуть сеть как было ({string.Join("; ", facts)})";
    }

    /// <summary>
    /// Разброс и потери словами для меню значка. Неизмеренное — «не измерено», а не «0 мс»:
    /// живой замер потерь не измеряет вовсе, и ноль здесь был бы выдумкой.
    /// </summary>
    public static string SpreadRu(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string jitter = snapshot.CurrentJitterMs is { } jitterMs
            ? $"разброс {jitterMs:0.#} мс"
            : "разброс не измерен";
        string loss = snapshot.CurrentLossPercent is { } lossPercent
            ? $"потери {lossPercent:0.#} %"
            : "потери не измерены";

        return $"{jitter}, {loss}";
    }

    /// <summary>
    /// Текст рядом со значком: короткий и без вранья. Нет измеренного пинга — так и пишем,
    /// и никогда не показываем «0 мс» вместо «нет данных».
    /// </summary>
    public static string TooltipFor(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.CurrentRttMs is not { } rtt)
        {
            // Замера нет — так и говорим; способа тоже нет, и придумывать его не из чего.
            return Fit("Пинга нет — замеров пока не было", snapshot.State, null, null);
        }

        string improvement = snapshot.ImprovementMs is { } gain
            ? gain >= 1 ? $"выигрыш {gain:0} мс" : "выигрыша нет"
            : null;

        return Fit($"пинг {rtt:0} мс", snapshot.State, MethodShortRu(snapshot), improvement);
    }

    /// <summary>
    /// Сколько знаков помещается в подсказку значка. WinForms обрезает её сам, поэтому длину держим
    /// здесь: обрезанное посреди слова «· оптимизаци» — это не подсказка, а мусор.
    /// </summary>
    public const int MaxIconTextLength = 63;

    /// <summary>
    /// Короткое имя способа для значка. Пусто — когда способ не назван: молчание честнее выдумки.
    /// </summary>
    public static string MethodShortRu(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(snapshot.FinalMethodTag))
        {
            return ProbeMethodTags.ShortRu(snapshot.FinalMethodTag);
        }

        return string.IsNullOrWhiteSpace(snapshot.BaselineMethodTag)
            ? string.Empty
            : ProbeMethodTags.ShortRu(snapshot.BaselineMethodTag) + " (до)";
    }

    /// <summary>
    /// Надпись из частей по убыванию важности: сначала «пинг, выигрыш, состояние, способ», затем
    /// выигрыш уступает место способу, потом состояние говорит одним словом. Обрезки посреди слова
    /// не бывает: не влезло — часть не показывается, а не режется.
    /// </summary>
    private static string Fit(string ping, RealtimeOptimizationState state, string? method, string? improvement)
    {
        string full = Join(ping, improvement, IconStateRu(state), method);
        if (full.Length <= MaxIconTextLength)
        {
            return full;
        }

        string withoutGain = Join(ping, null, IconStateRu(state), method);
        if (withoutGain.Length <= MaxIconTextLength)
        {
            return withoutGain;
        }

        string oneWord = Join(ping, null, IconStateShortRu(state), method);
        return oneWord.Length <= MaxIconTextLength ? oneWord : Join(ping, null, null, method);
    }

    private static string Join(string ping, string? improvement, string? state, string? method)
    {
        var parts = new List<string>(4) { ping };
        if (!string.IsNullOrEmpty(improvement))
        {
            parts.Add(improvement);
        }

        if (!string.IsNullOrEmpty(state))
        {
            parts.Add(state);
        }

        if (!string.IsNullOrEmpty(method))
        {
            parts.Add(method);
        }

        return "Custom Tweak: " + string.Join(" · ", parts);
    }

    /// <summary>Состояние для значка — словами, но короче, чем в окне.</summary>
    public static string IconStateRu(RealtimeOptimizationState state) => state switch
    {
        RealtimeOptimizationState.ActiveMonitoring => "оптимизация работает",
        RealtimeOptimizationState.Degraded => "работает, качество просело",
        RealtimeOptimizationState.MeasuringBaseline => "идёт замер",
        RealtimeOptimizationState.ReadOnlyNoAdmin => "только замеры (нет прав)",
        RealtimeOptimizationState.Stopped => "остановлено",
        RealtimeOptimizationState.Failed => "не удалось: смотрите окно",
        _ => "простой"
    };

    private static string IconStateShortRu(RealtimeOptimizationState state) => state switch
    {
        RealtimeOptimizationState.ActiveMonitoring => "работает",
        RealtimeOptimizationState.Degraded => "просело",
        RealtimeOptimizationState.MeasuringBaseline => "замер",
        RealtimeOptimizationState.ReadOnlyNoAdmin => "без прав",
        RealtimeOptimizationState.Failed => "сбой",
        _ => IconStateRu(state)
    };
}
