using System.Windows.Input;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Главный экран: большая кнопка, состояние и честный итог.
///
/// Два правила, зашитых в код и проверяемых тестами:
///   • при запуске программа ничего не делает — ни одного запроса к службе, пока пользователь не нажал;
///   • всё, что меняет систему, идёт через подтверждение и сообщается результатом, а не «кнопка мигнула».
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private readonly IUserDialogs _dialogs;
    private string _statusRu = "Готово к работе. Нажмите «Оптимизировать соединение» — до этого программа ничего не меняет.";
    private string _qualityRu = "Замеров пока не было";
    private string? _gameTitleRu;
    private string? _endpoint;
    private double? _baselineRttMs;
    private double? _currentRttMs;
    private double? _improvementMs;
    private bool _isOptimizing;
    private bool _hasRights;

    public MainViewModel(EngineClient engine, IUserDialogs dialogs)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        OptimizeCommand = new AsyncRelayCommand(ToggleOptimizationAsync, () => true);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync, () => true);
        RepairNetworkCommand = new AsyncRelayCommand(RepairNetworkAsync, () => true);
    }

    public AsyncRelayCommand OptimizeCommand { get; }

    public AsyncRelayCommand RefreshStatusCommand { get; }

    public AsyncRelayCommand RepairNetworkCommand { get; }

    /// <summary>Идёт ли оптимизация прямо сейчас.</summary>
    public bool IsOptimizing
    {
        get => _isOptimizing;
        private set
        {
            if (Set(ref _isOptimizing, value))
            {
                OnPropertyChanged(nameof(ButtonTextRu), nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !IsOptimizing;

    /// <summary>Надпись на большой кнопке. Одна кнопка делает и то, и другое — так просил пользователь.</summary>
    public string ButtonTextRu => IsOptimizing ? "Остановить и вернуть как было" : "Оптимизировать соединение";

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public string QualityRu
    {
        get => _qualityRu;
        private set => Set(ref _qualityRu, value);
    }

    public string? GameTitleRu
    {
        get => _gameTitleRu;
        private set => Set(ref _gameTitleRu, value);
    }

    public string? Endpoint
    {
        get => _endpoint;
        private set => Set(ref _endpoint, value);
    }

    public double? BaselineRttMs
    {
        get => _baselineRttMs;
        private set => Set(ref _baselineRttMs, value);
    }

    public double? CurrentRttMs
    {
        get => _currentRttMs;
        private set => Set(ref _currentRttMs, value);
    }

    public double? ImprovementMs
    {
        get => _improvementMs;
        private set => Set(ref _improvementMs, value);
    }

    /// <summary>Есть ли права: без них кнопка оптимизации честно говорит, что будут только замеры.</summary>
    public bool HasRights
    {
        get => _hasRights;
        private set
        {
            if (Set(ref _hasRights, value))
            {
                OnPropertyChanged(nameof(RightsNoteRu));
            }
        }
    }

    public string RightsNoteRu => HasRights
        ? "Права администратора есть: можно менять настройки."
        : "Прав администратора нет: будут только замеры, менять настройки Windows программа не станет.";

    /// <summary>«120 мс → 78 мс» или честное «не измерено».</summary>
    public string RttLineRu => BaselineRttMs is null || CurrentRttMs is null
        ? "Задержка: не измерена"
        : $"Задержка: было {BaselineRttMs:0.#} мс, сейчас {CurrentRttMs:0.#} мс" +
          (ImprovementMs is > 0.5 ? $", стало лучше на {ImprovementMs:0.#} мс" : ", заметного выигрыша нет");

    /// <summary>
    /// Нажатие большой кнопки. Запуск или остановка — решается по текущему состоянию,
    /// а не по тому, что «кнопка должна делать по замыслу».
    /// </summary>
    public async Task ToggleOptimizationAsync()
    {
        if (IsOptimizing)
        {
            await StopAsync().ConfigureAwait(true);
            return;
        }

        await StartAsync().ConfigureAwait(true);
    }

    public async Task StartAsync()
    {
        if (IsOptimizing)
        {
            return;
        }

        StatusRu = "Ищу запущенную игру и меряю задержку «до»…";

        try
        {
            EngineResponse response = await _engine
                .SendAsync(EngineCommandKind.RealtimeStart)
                .ConfigureAwait(true);

            ApplyStateFromJson(response.DataJson);

            if (!response.Ok && !response.MessageRu.Contains("только замеры", StringComparison.Ordinal))
            {
                StatusRu = response.MessageRu;
                await _dialogs.InformAsync("Оптимизация не запустилась", response.MessageRu).ConfigureAwait(true);
                return;
            }

            IsOptimizing = true;
            StatusRu = response.MessageRu;
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
            await _dialogs.InformAsync("Служба недоступна", ex.Message).ConfigureAwait(true);
        }
    }

    public async Task StopAsync()
    {
        if (!IsOptimizing)
        {
            return;
        }

        StatusRu = "Останавливаюсь и снимаю свои изменения…";

        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.RealtimeStop).ConfigureAwait(true);
            ApplyStateFromJson(response.DataJson);
            IsOptimizing = false;
            StatusRu = response.MessageRu;
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
            await _dialogs.InformAsync("Служба недоступна", ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>Обновить состояние (то, что окно делает по таймеру раз в пару секунд — экономно).</summary>
    public async Task RefreshStatusAsync()
    {
        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.Status).ConfigureAwait(true);
            ApplyStateFromJson(response.DataJson);

            if (!response.Ok)
            {
                StatusRu = response.MessageRu;
            }
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
    }

    /// <summary>«Починить сеть»: снять остатки после аварии. Всегда с подтверждением.</summary>
    public async Task RepairNetworkAsync()
    {
        bool confirmed = await _dialogs.ConfirmAsync(
            "Починить сеть",
            "Программа снимает только то, что добавляла сама: свои маршруты, свои политики QoS и незакрытые операции " +
            "из журнала. Чужие маршруты и настройки не трогает. Продолжить?",
            "Починить",
            "Отмена").ConfigureAwait(true);

        if (!confirmed)
        {
            StatusRu = "Починка отменена — ничего не менялось.";
            return;
        }

        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.RepairNetwork).ConfigureAwait(true);
            StatusRu = response.MessageRu;
            await _dialogs.InformAsync("Починка сети", response.MessageRu).ConfigureAwait(true);
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
            await _dialogs.InformAsync("Служба недоступна", ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>Разобрать ответ службы. Отсутствующие поля — не ноль, а «неизвестно».</summary>
    private void ApplyStateFromJson(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(dataJson);
            System.Text.Json.JsonElement root = document.RootElement;

            if (root.TryGetProperty("gameTitleRu", out System.Text.Json.JsonElement game) && game.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                GameTitleRu = game.GetString();
            }

            if (root.TryGetProperty("endpoint", out System.Text.Json.JsonElement endpoint) && endpoint.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                Endpoint = endpoint.GetString();
            }

            BaselineRttMs = ReadNumber(root, "baselineRttMs");
            CurrentRttMs = ReadNumber(root, "currentRttMs");
            ImprovementMs = ReadNumber(root, "improvementMs");

            if (root.TryGetProperty("qualityLabelRu", out System.Text.Json.JsonElement quality) && quality.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                QualityRu = quality.GetString() ?? QualityRu;
            }

            if (root.TryGetProperty("isElevationAvailable", out System.Text.Json.JsonElement rights) &&
                rights.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                HasRights = rights.GetBoolean();
            }

            if (root.TryGetProperty("realtimeState", out System.Text.Json.JsonElement state) && state.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string wire = state.GetString() ?? "idle";
                IsOptimizing = wire is "active-monitoring" or "degraded" or "applying-actions" or "choosing-route" or "measuring-baseline";
            }

            OnPropertyChanged(nameof(RttLineRu));
        }
        catch (System.Text.Json.JsonException)
        {
            // Молча пропустить битый JSON нельзя: состояние остаётся прежним, а текст говорит правду.
            StatusRu = "Служба ответила неразборчиво: показываю прежнее состояние, а не выдуманные цифры.";
        }
    }

    private static double? ReadNumber(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out System.Text.Json.JsonElement value)
        && value.ValueKind == System.Text.Json.JsonValueKind.Number
        && value.TryGetDouble(out double number)
            ? number
            : null;
}
