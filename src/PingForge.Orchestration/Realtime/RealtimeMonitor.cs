namespace PingForge.Orchestration.Realtime;

/// <summary>
/// Фоновый контроль живой оптимизации: раз в интервал спрашивает у оптимизатора одно окно замера.
///
/// Зачем отдельно: сам оптимизатор умеет ровно один шаг (<see cref="RealtimeOptimizer.TickOnceAsync"/>),
/// и это правильно — его поведение проверяется тестом. А «кто-то должен звать этот шаг по времени»
/// — задача службы. Раньше её не было, поэтому после запуска оптимизации числа замирали на первом
/// замере: в интерфейсе было написано «контроль каждые 2 с», а контроля не происходило.
///
/// Правила:
///   • в покое и после отказа цикл ничего не делает — незачем будить сеть и процессор;
///   • ошибка одного окна не убивает контроль: она сообщается и цикл идёт дальше;
///   • остановка — по токену, без «фоновых сирот» после закрытия службы.
/// </summary>
public sealed class RealtimeMonitor
{
    private readonly RealtimeOptimizer _optimizer;
    private readonly Func<TimeSpan> _interval;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _clock;

    public RealtimeMonitor(
        RealtimeOptimizer optimizer,
        Func<TimeSpan>? interval = null,
        Action<string>? log = null,
        Func<DateTime>? clock = null)
    {
        _optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        _interval = interval ?? (() => TimeSpan.FromSeconds(2));
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Сколько окон уже сделано — по этому числу видно, что контроль действительно идёт.</summary>
    public int Ticks { get; private set; }

    /// <summary>Последнее сообщение оптимизатора или ошибки окна.</summary>
    public string? LastNoteRu { get; private set; }

    /// <summary>Идёт ли контроль. Меняется синхронно с состоянием оптимизатора.</summary>
    public bool IsActive => _optimizer.State is RealtimeOptimizationState.ActiveMonitoring
        or RealtimeOptimizationState.Degraded
        or RealtimeOptimizationState.ReadOnlyNoAdmin;

    /// <summary>Один шаг контроля. Тот же код, что и в цикле, — так его можно проверить тестом.</summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        if (!IsActive)
        {
            return false;
        }

        try
        {
            RealtimeSnapshot snapshot = await _optimizer.TickOnceAsync(ct).ConfigureAwait(false);
            Ticks++;
            LastNoteRu = snapshot.StatusRu;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Одно плохое окно — не повод прекращать контроль. Пишем причину и продолжаем.
            LastNoteRu = $"окно контроля не удалось: {ex.Message}";
            _log?.Invoke($"Живая оптимизация: окно контроля не удалось ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Цикл до отмены. В покое спит, но не крутится вхолостую.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = _interval();

            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!IsActive)
            {
                continue;
            }

            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
