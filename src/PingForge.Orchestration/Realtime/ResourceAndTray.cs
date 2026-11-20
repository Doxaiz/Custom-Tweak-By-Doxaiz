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
                    true, $"Снять маршрут и политику QoS, вернуть сеть как было ({pingText}; {SpreadRu(snapshot)})")
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

        string state = snapshot.State switch
        {
            RealtimeOptimizationState.ActiveMonitoring => "оптимизация работает",
            RealtimeOptimizationState.Degraded => "оптимизация работает, качество просело",
            RealtimeOptimizationState.MeasuringBaseline => "идёт замер",
            RealtimeOptimizationState.ReadOnlyNoAdmin => "только замеры (нет прав)",
            RealtimeOptimizationState.Stopped => "остановлено",
            RealtimeOptimizationState.Failed => "не удалось: смотрите окно",
            _ => "простой"
        };

        if (snapshot.CurrentRttMs is not { } rtt)
        {
            return $"Custom Tweak: {state}. Пинга нет — замеров пока не было.";
        }

        string improvement = snapshot.ImprovementMs is { } gain
            ? gain >= 1 ? $", выигрыш {gain:0} мс к исходному" : ", выигрыша к исходному нет"
            : string.Empty;

        return $"Custom Tweak: пинг {rtt:0} мс{improvement} · {state}";
    }
}
