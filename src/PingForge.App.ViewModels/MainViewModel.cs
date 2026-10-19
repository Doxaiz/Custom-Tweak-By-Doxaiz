using System.Windows.Input;
using PingForge.Analysis.Charting;
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
    private readonly bool _pollStatusWhileOptimizing;
    private int _pollGeneration;
    private string _statusRu = "Готово к работе. Нажмите «Оптимизировать соединение» — до этого программа ничего не меняет.";
    private string _qualityRu = "Замеров пока не было";
    private string? _gameTitleRu;
    private string? _endpoint;
    private double? _baselineRttMs;
    private double? _currentRttMs;
    private double? _improvementMs;
    private bool _isOptimizing;
    private bool _hasRights;
    private string? _noticeTitleRu;
    private string? _noticeRu;
    private bool _versionWarningShown;

    /// <remarks>
    /// runsAsAdministrator — запущено ли окно с правами администратора. Раньше это поле начиналось с «нет»
    /// и ждало ответа службы: на снимке живой Windows выходило «Прав администратора нет» — при том, что
    /// журнал той же программы писал «есть». Права окна знает сама Windows; ответ службы их уточняет.
    /// </remarks>
    public MainViewModel(
        EngineClient engine,
        IUserDialogs dialogs,
        bool runsAsAdministrator = false,
        bool pollStatusWhileOptimizing = false,
        NetworkCheckViewModel? networkCheck = null,
        ServerPathViewModel? serverPath = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _hasRights = runsAsAdministrator;
        _pollStatusWhileOptimizing = pollStatusWhileOptimizing;
        NetworkCheck = networkCheck ?? NetworkCheckViewModel.Unavailable();
        ServerPath = serverPath ?? ServerPathViewModel.Unavailable();

        OptimizeCommand = new AsyncRelayCommand(ToggleOptimizationAsync, () => true);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync, () => true);
        RepairNetworkCommand = new AsyncRelayCommand(RepairNetworkAsync, () => true);
        DismissNoticeCommand = new RelayCommand(() => ShowNotice(null, null));
    }

    /// <summary>
    /// График сети (задача F): строится ТОЛЬКО из замеров живой оптимизации, которые отдаёт
    /// команда status. График не запускает собственных зондов и не отправляет пакетов —
    /// все точки приходят из уже идущих замеров оптимизации.
    /// </summary>
    public NetworkChartViewModel Chart { get; } = new();

    /// <summary>
    /// «Проверить сеть» (задача G): семь проверок чтения и замеров рядом с главной кнопкой.
    /// Собирается приложением с настоящими измерениями; по умолчанию — честное «не подключена».
    /// Ничего не меняет: рекомендации применяются обычным путём раздела «Твики».
    /// </summary>
    public NetworkCheckViewModel NetworkCheck { get; }

    /// <summary>
    /// «Путь до сервера» (задача H): трасса до адреса найденного игрового сервера, только при живой
    /// сессии. Собирается приложением с настоящим ICMP-транспортом; по умолчанию — честный отказ
    /// без единого пакета. Ничего не меняет: это эхо-запросы с TTL.
    /// </summary>
    public ServerPathViewModel ServerPath { get; }

    /// <summary>
    /// Проверка обновлений (задача I) для раздела «О программе» — той же страницы, что показывает
    /// эту модель. Живёт здесь, а не собственной моделью окна: «О программе» привязана к главному
    /// экрану. Сама по себе никуда не ходит: запрос уходит только по кнопке.
    /// </summary>
    public UpdateCheckViewModel Updates { get; } = new();

    /// <summary>Закрыть ответ программы, когда он прочитан.</summary>
    public RelayCommand DismissNoticeCommand { get; }

    /// <summary>
    /// Заголовок ответа программы. Ответ показывается прямо на экране, а не модальным окном.
    ///
    /// Почему так, а не MessageBox: на живой Windows окно сообщения с флагом
    /// MessageBoxOptions.DefaultDesktopOnly не вставало в модальный цикл окна — главное окно
    /// на всё это время становилось «(Not Responding)», и ни меню, ни кнопки не работали.
    /// Это увидел прогон «как человек»: семь одинаковых снимков и ни одного переключённого раздела.
    /// Встроенный ответ нельзя не заметить, он не блокирует окно и остаётся на экране, пока его не прочтут.
    /// </summary>
    public string? NoticeTitleRu
    {
        get => _noticeTitleRu;
        private set => Set(ref _noticeTitleRu, value);
    }

    /// <summary>Текст ответа. Пусто — ответа нет.</summary>
    public string? NoticeRu
    {
        get => _noticeRu;
        private set => Set(ref _noticeRu, value);
    }

    /// <summary>Показывать ли карточку с ответом.</summary>
    public bool IsNoticeVisible => !string.IsNullOrWhiteSpace(NoticeRu);

    /// <summary>
    /// Показать ответ программы на экране. null — убрать.
    /// Публичный не для красоты: так ответ проверяет самопроверка на живой Windows — она показывает
    /// карточку и убеждается, что текст дошёл до разметки, а окно осталось живым.
    /// </summary>
    public void ShowNotice(string? titleRu, string? messageRu)
    {
        NoticeTitleRu = titleRu;
        NoticeRu = messageRu;
        OnPropertyChanged(nameof(IsNoticeVisible));
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
                if (value)
                {
                    StartStatusPolling();
                }
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

    /// <summary>
    /// Адрес игрового сервера для трассы «Путь до сервера»: отдаётся только при живой сессии.
    /// Оптимизация остановлена — адрес неизвестен, и трасса честно скажет «сначала запустите игру».
    /// </summary>
    public string? LiveGameEndpoint => IsOptimizing ? Endpoint : null;

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

    /// <summary>
    /// Строка о версиях окна и службы для раздела «О программе» (C5). Обновляется, когда служба
    /// отвечает: человек должен видеть версии ДО того, как что-то пойдёт не так.
    /// </summary>
    public string VersionsNoteRu => _engine.VersionsNoteRu;

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
        ShowNotice(null, null);

        try
        {
            EngineResponse response = await _engine
                .SendAsync(EngineCommandKind.RealtimeStart)
                .ConfigureAwait(true);

            ApplyStateFromJson(response.DataJson);

            if (!response.Ok && !response.MessageRu.Contains("только замеры", StringComparison.Ordinal))
            {
                StatusRu = response.MessageRu;
                ShowNotice("Оптимизация не запустилась", response.MessageRu);
                return;
            }

            IsOptimizing = true;
            StatusRu = response.MessageRu;
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
            ShowNotice("Служба недоступна", ex.Message);
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
            ShowNotice("Служба недоступна", ex.Message);
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

    /// <summary>
    /// Редкий опрос состояния, пока оптимизация идёт (только в бою — тестам цикл не нужен).
    /// Команда status только читает: ни одного изменения системы за спиной пользователя.
    /// Останавливается сама, когда оптимизация закончилась (IsOptimizing → false).
    /// </summary>
    private void StartStatusPolling()
    {
        if (!_pollStatusWhileOptimizing)
        {
            return;
        }

        int generation = ++_pollGeneration;
        _ = PollStatusWhileOptimizingAsync(generation);
    }

    private async Task PollStatusWhileOptimizingAsync(int generation)
    {
        while (_pollGeneration == generation && IsOptimizing)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_pollGeneration != generation || !IsOptimizing)
            {
                return;
            }

            await RefreshStatusAsync().ConfigureAwait(true);
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
            ShowNotice("Починка сети", response.MessageRu);
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
            ShowNotice("Служба недоступна", ex.Message);
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

            // График сети: точки приходят той же командой status — отдельного канала и зондов нет.
            if (root.TryGetProperty("chartSamples", out System.Text.Json.JsonElement samples) &&
                samples.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                Chart.ReceiveSamples(ParseChartSamples(samples));
            }

            OnPropertyChanged(nameof(RttLineRu));
            OnPropertyChanged(nameof(VersionsNoteRu));

            // C5: версии окна и службы разошлись — говорим один раз и не молчим. Это предупреждение,
            // а не отказ: команды работают (протокол один), но сборка неполная.
            if (_engine.VersionState == EngineVersionState.AppDiffers && !_versionWarningShown)
            {
                _versionWarningShown = true;
                ShowNotice("Версии различаются", _engine.VersionsNoteRu);
            }
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

    /// <summary>
    /// Разбор точек графика из ответа status. Правило то же, что во всей программе: неизмеренное
    /// окно — точка без чисел (разрыв на графике), а не ноль. Медиана окна идёт в линию «средняя»,
    /// когда способ замера не дал среднего: это то же число, которое кнопка называет «пинг сейчас».
    /// </summary>
    private static IReadOnlyList<ChartSample> ParseChartSamples(System.Text.Json.JsonElement array)
    {
        var samples = new List<ChartSample>(array.GetArrayLength());

        foreach (System.Text.Json.JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !item.TryGetProperty("atUtc", out System.Text.Json.JsonElement atElement) ||
                atElement.ValueKind != System.Text.Json.JsonValueKind.String ||
                !DateTimeOffset.TryParse(atElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
            {
                // Точка без времени — не точка: пропустить честнее, чем рисовать «в нуле».
                continue;
            }

            bool measured = item.TryGetProperty("measured", out System.Text.Json.JsonElement measuredElement)
                && measuredElement.ValueKind == System.Text.Json.JsonValueKind.True;

            if (!measured)
            {
                samples.Add(ChartSample.Empty(at));
                continue;
            }

            double? min = ReadNumber(item, "rttMinMs");
            double? median = ReadNumber(item, "rttMedianMs");
            double? avg = ReadNumber(item, "rttAvgMs") ?? median;
            double? p95 = ReadNumber(item, "rttP95Ms");
            double? jitter = ReadNumber(item, "jitterMadMs");
            double? loss = ReadNumber(item, "lossPercent");

            samples.Add(new ChartSample(
                at,
                RttMinMs: min,
                RttAvgMs: avg,
                RttP95Ms: p95,
                JitterMadMs: jitter,
                PacketLossPercent: loss));
        }

        return samples;
    }
}
