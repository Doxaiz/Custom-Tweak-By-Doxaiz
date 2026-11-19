using System.Globalization;
using System.Text.Json;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Одна сессия оптимизации в «Журнале» (задача J): когда, игра и сервер, «до → после»,
/// действия, пометка откатa и честный статус. Все числа nullable: «не измерено» — это прочерк
/// и честный текст, а не ноль и не выдуманное улучшение.
/// </summary>
public sealed record SessionHistoryRow(
    string SessionId,
    DateTime StartedUtc,
    DateTime? StoppedUtc,
    string GameTitleRu,
    string Endpoint,
    bool HasGame,
    bool IsMeasurable,
    double? BaselineRttMs,
    double? FinalRttMs,
    double? ImprovementMs,
    IReadOnlyList<string> Actions,
    string RollbackStateRu,
    bool IsCompleted,
    string StatusRu,
    string Outcome,
    string MethodRu = "",
    bool MethodsDiffer = false)
{
    public bool HasValidTime => StartedUtc != DateTime.MinValue;

    public string WhenRu => HasValidTime
        ? StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)
        : "время неизвестно";

    public string TitleRu
    {
        get
        {
            string game = string.IsNullOrWhiteSpace(GameTitleRu) ? "игра не названа" : GameTitleRu;
            string endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "адрес сервера не определён" : Endpoint;
            return $"{WhenRu} — {game} — {endpoint}";
        }
    }

    /// <summary>«до → после» и разница. Незавершённая сессия не получает улучшения.</summary>
    public string ResultRu
    {
        get
        {
            // Способ замера — рядом с числами: «до» и «после» могли быть сняты разными способами,
            // и человек должен видеть, чем именно мерили. Пустая подпись не добавляется.
            string method = string.IsNullOrWhiteSpace(MethodRu) ? string.Empty : " — " + MethodRu;

            if (!HasGame)
            {
                return "«до» не измерено: игра не найдена — итог неизвестен";
            }

            if (BaselineRttMs is not { } before)
            {
                return IsCompleted ? "«до» не измерено — сравнивать нечего" : "сессия не завершена, итог неизвестен";
            }

            if (FinalRttMs is not { } after)
            {
                return $"«до» {before:0.#} мс, «после» не измерено — сессия не завершена, итог неизвестен" + method;
            }

            double difference = ImprovementMs ?? before - after;
            string line = $"было {before:0.#} мс → стало {after:0.#} мс";

            // Пара снята разными способами: разница может быть свойством способа, а не действий.
            string caveat = MethodsDiffer
                ? "; разница может быть от способа замера, а не от действий"
                : string.Empty;

            if (difference > 0.5)
            {
                return line + $", лучше на {difference:0.#} мс" + method + caveat;
            }

            if (difference < -0.5)
            {
                return line + $", хуже на {-difference:0.#} мс" + method + caveat;
            }

            return line + ", заметного выигрыша нет" + method + caveat;
        }
    }

    public string ActionsRu => Actions.Count == 0
        ? "Действий не было."
        : "Действия: " + string.Join(", ", Actions) + ".";

    public string RollbackRu => "Откат: " + (string.IsNullOrWhiteSpace(RollbackStateRu)
        ? "неизвестно (сессия без итога)"
        : RollbackStateRu.ToLowerInvariant());

    public string StatusLineRu => string.IsNullOrWhiteSpace(StatusRu)
        ? (IsCompleted ? "Сессия завершена." : "Сессия не завершена, итог неизвестен.")
        : StatusRu;
}

/// <summary>
/// Итог сравнения двух выбранных сессий: можно ли сравнивать, что именно совпало или разошлось
/// и — если нельзя — по какой причине. Причина отдельным полем, потому что подпись под списком
/// называет её одной строкой, а не общим «сравнение невозможно».
/// </summary>
public sealed record SessionComparisonResult(bool Comparable, IReadOnlyList<string> Lines, string ReasonRu = "");

/// <summary>
/// Сравнение двух сессий (задача J). Правило честности: сессии к разным серверам не сравниваются —
/// разница в задержке тогда говорит о сервере, а не о действиях программы. Сессия без измеренного
/// «после» не получает выдуманного результата: сравнение результата честно невозможно.
///
/// Ещё два случая, где честный ответ — отказ:
///   • та же самая сессия дважды. Разница с собой всегда ноль, и «результат совпал в пределах 0,5 мс»
///     выглядело бы измерением, хотя это совпадение записи с собой. Строки перечитываются из базы,
///     поэтому сравниваются номера записей, а не ссылки на объекты;
///   • адрес сервера не определён. «Неизвестен» и «тот же самый» — разные вещи: две сессии с пустым
///     адресом раньше считались сессиями к одному серверу именно потому, что обе пустые.
/// </summary>
public static class SessionComparison
{
    /// <summary>Причина отказа: выбрана одна и та же сессия.</summary>
    public const string SameSessionReason = "одна и та же сессия";

    /// <summary>Причина отказа: у сессии нет адреса сервера.</summary>
    public const string UnknownEndpointReason = "адрес сервера не определён";

    /// <summary>Причина отказа: сессии к разным серверам.</summary>
    public const string DifferentServersReason = "разные серверы";

    public static SessionComparisonResult Compare(SessionHistoryRow first, SessionHistoryRow second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (SameSession(first, second))
        {
            return new SessionComparisonResult(false,
            [
                "Это одна и та же сессия: сравнивать её с собой нечего. Совпадёт всё, и «разница 0 мс» тут — это совпадение записи с собой, а не измерение.",
                $"{first.WhenRu} — {(string.IsNullOrWhiteSpace(first.Endpoint) ? "адрес сервера не определён" : first.Endpoint)}.",
                "Выберите две разные сессии к одному и тому же серверу."
            ], SameSessionReason);
        }

        if (string.IsNullOrWhiteSpace(first.Endpoint) || string.IsNullOrWhiteSpace(second.Endpoint))
        {
            return new SessionComparisonResult(false,
            [
                "Адрес сервера не определён хотя бы у одной сессии — значит, неизвестно и то, к одному ли серверу они были. Сравнивать вслепую нельзя: разница в задержке может оказаться разницей серверов.",
                $"Первая сессия: {(string.IsNullOrWhiteSpace(first.Endpoint) ? "адрес неизвестен" : first.Endpoint)}. " +
                $"Вторая сессия: {(string.IsNullOrWhiteSpace(second.Endpoint) ? "адрес неизвестен" : second.Endpoint)}.",
                "Выберите две сессии с известным адресом к одному и тому же серверу."
            ], UnknownEndpointReason);
        }

        if (!string.Equals(first.Endpoint, second.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            return new SessionComparisonResult(false,
            [
                "Сравнивать сессии к разным серверам нельзя: задержка до разных серверов зависит от сервера, а не от действий программы.",
                $"Первая сессия: {first.Endpoint}. Вторая сессия: {second.Endpoint}.",
                "Выберите две сессии к одному и тому же серверу."
            ], DifferentServersReason);
        }

        var lines = new List<string>();

        var common = first.Actions
            .Intersect(second.Actions, StringComparer.OrdinalIgnoreCase)
            .OrderBy(title => title, StringComparer.CurrentCulture)
            .ToList();
        var onlyFirst = first.Actions.Except(second.Actions, StringComparer.OrdinalIgnoreCase).ToList();
        var onlySecond = second.Actions.Except(first.Actions, StringComparer.OrdinalIgnoreCase).ToList();

        lines.Add(common.Count == 0
            ? "Общих действий нет."
            : "Совпали действия: " + string.Join(", ", common) + ".");

        if (onlyFirst.Count > 0)
        {
            lines.Add("Только в первой сессии: " + string.Join(", ", onlyFirst) + ".");
        }

        if (onlySecond.Count > 0)
        {
            lines.Add("Только во второй сессии: " + string.Join(", ", onlySecond) + ".");
        }

        // Разные игры при одном сервере сравнивать можно, но одинаковый сервер — ещё не одинаковые
        // условия: об этом сказано прямо, чтобы разница не выглядела заслугой программы.
        if (!string.Equals(first.GameTitleRu, second.GameTitleRu, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Сессии к разным играм ({(string.IsNullOrWhiteSpace(first.GameTitleRu) ? "игра не названа" : first.GameTitleRu)} " +
                $"и {(string.IsNullOrWhiteSpace(second.GameTitleRu) ? "игра не названа" : second.GameTitleRu)}): " +
                "одинаковый сервер — ещё не одинаковые условия, и разница может быть разницей игр.");
        }

        // Полного «до → после» нет хотя бы у одной сессии — числа не сравниваются. Проверяются все
        // три числа, а не только «после»: строка без измеренного «до» не получает выдуманной разницы.
        if (first.BaselineRttMs is null || second.BaselineRttMs is null
            || first.FinalRttMs is null || second.FinalRttMs is null
            || first.ImprovementMs is null || second.ImprovementMs is null)
        {
            lines.Add("У одной из сессий нет полного «до → после» — результат не сравнивается: «не измерено» не становится нулём.");
            lines.Add($"Первая: {first.ResultRu}. Вторая: {second.ResultRu}.");
            return new SessionComparisonResult(true, lines);
        }

        lines.Add($"Первая: {first.ResultRu}. Вторая: {second.ResultRu}.");

        double delta = first.ImprovementMs.Value - second.ImprovementMs.Value;
        lines.Add(Math.Abs(delta) <= 0.5
            ? "Результат совпал в пределах 0,5 мс."
            : $"Результат разошёлся на {Math.Abs(delta):0.#} мс: {(delta > 0 ? "в первой сессии лучше" : "во второй сессии лучше")}.");

        return new SessionComparisonResult(true, lines);
    }

    /// <summary>
    /// Одна и та же запись. Номер сессии важнее ссылки: строки перечитываются из базы, и две
    /// загрузки одной сессии — это два разных объекта с одним номером.
    /// </summary>
    private static bool SameSession(SessionHistoryRow first, SessionHistoryRow second)
    {
        if (!string.IsNullOrWhiteSpace(first.SessionId) || !string.IsNullOrWhiteSpace(second.SessionId))
        {
            return string.Equals(first.SessionId, second.SessionId, StringComparison.Ordinal);
        }

        return ReferenceEquals(first, second);
    }
}

/// <summary>Точка дневного графика: средний RTT «до» по сессиям к ОДНОМУ серверу за день.</summary>
public sealed record DailyRttPoint(DateTime Day, int SessionCount, double AverageRttMs, double BarWidthPx)
{
    public string DayRu => Day.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

    public string LineRu => $"{DayRu}: {AverageRttMs:0.#} мс — сессий: {SessionCount}";

    public string ValueRu => $"{AverageRttMs:0.#} мс — сессий: {SessionCount}";
}

/// <summary>
/// График по дням (задача J). Разные серверы не усредняются и не сравниваются: в выборку попадают
/// только сессии с тем же адресом сервера, что выбран человеком. Не измеренное не превращается в ноль.
/// </summary>
public static class DailyRttHistory
{
    /// <summary>Ширина самой длинной полосы на канве графика, точки.</summary>
    public const double MaxBarWidthPx = 220;

    public static IReadOnlyList<DailyRttPoint> Build(IReadOnlyList<SessionHistoryRow> sessions, string? endpoint)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return [];
        }

        var perDay = sessions
            .Where(session => string.Equals(session.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            .Where(session => session.BaselineRttMs is not null && session.IsMeasurable && session.HasValidTime)
            .GroupBy(session => session.StartedUtc.ToLocalTime().Date)
            .Select(group => (
                Day: group.Key,
                Count: group.Count(),
                Average: Math.Round(group.Average(session => session.BaselineRttMs!.Value), 1)))
            .OrderBy(point => point.Day)
            .ToList();

        if (perDay.Count == 0)
        {
            return [];
        }

        double maximum = perDay.Max(point => point.Average);

        return perDay
            .Select(point => new DailyRttPoint(
                point.Day,
                point.Count,
                point.Average,
                maximum <= 0 ? 0 : Math.Round(point.Average / maximum * MaxBarWidthPx, 1)))
            .ToList();
    }
}

/// <summary>
/// Вкладка «Сессии» в разделе «Журнал» (задача J): список сессий «до → после», сравнение двух
/// выбранных и график среднего RTT по дням только для одного сервера. Сама ничего не меняет;
/// очистка истории — по подтверждению и отдельной командой службы, журнал изменений не трогается.
/// </summary>
public sealed class SessionHistoryViewModel : ObservableObject
{
    private const string IdleRu =
        "История сессий ещё не читалась: нажмите «Обновить сессии» — записи придут из базы службы.";

    private const string EmptyRu =
        "История сессий пуста: живая оптимизация ещё не запускалась. После первого запуска — даже неудачного — " +
        "здесь появится запись с честным статусом.";

    private readonly EngineClient _engine;
    private readonly IUserDialogs? _dialogs;
    private readonly int _rowsToAsk;

    private IReadOnlyList<SessionHistoryRow> _rows = [];
    private IReadOnlyList<string> _endpoints = [];
    private IReadOnlyList<DailyRttPoint> _daily = [];
    private IReadOnlyList<string> _comparisonRu = [];
    private SessionHistoryRow? _selectedFirst;
    private SessionHistoryRow? _selectedSecond;
    private string? _selectedEndpoint;
    private string _statusRu = IdleRu;
    private string _summaryRu = string.Empty;
    private string _comparisonCaptionRu = "Выберите две сессии к одному серверу и нажмите «Сравнить».";
    private string _dailyCaptionRu = "График по дням появится, когда будет хотя бы одна измеренная сессия.";
    private bool _isLoading;
    private bool _loaded;

    public SessionHistoryViewModel(EngineClient engine, IUserDialogs? dialogs, int rowsToAsk = 200)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs;
        _rowsToAsk = Math.Clamp(rowsToAsk, 1, 500);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CompareCommand = new RelayCommand(Compare, () => SelectedFirst is not null && SelectedSecond is not null);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => _loaded && !IsLoading);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CompareCommand { get; }

    public AsyncRelayCommand ClearCommand { get; }

    public IReadOnlyList<SessionHistoryRow> Rows
    {
        get => _rows;
        private set
        {
            if (Set(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows), nameof(IsEmpty));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    /// <summary>Пусто после успешного чтения — это «сессий ещё не было», а не «не прочитали».</summary>
    public bool IsEmpty => _loaded && Rows.Count == 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                ClearCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public string SummaryRu
    {
        get => _summaryRu;
        private set => Set(ref _summaryRu, value);
    }

    public string EmptyRuText => EmptyRu;

    public SessionHistoryRow? SelectedFirst
    {
        get => _selectedFirst;
        set
        {
            if (Set(ref _selectedFirst, value))
            {
                CompareCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ComparisonHintRu), nameof(ComparisonReady));
            }
        }
    }

    public SessionHistoryRow? SelectedSecond
    {
        get => _selectedSecond;
        set
        {
            if (Set(ref _selectedSecond, value))
            {
                CompareCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ComparisonHintRu), nameof(ComparisonReady));
            }
        }
    }

    public bool ComparisonReady => SelectedFirst is not null && SelectedSecond is not null;

    public string ComparisonHintRu => ComparisonReady
        ? "Сравнение покажет совпавшие действия и то, где результат разошёлся."
        : "Выберите вторую сессию — сравнивать одну с самой собой нечего.";

    public IReadOnlyList<string> ComparisonRu
    {
        get => _comparisonRu;
        private set => Set(ref _comparisonRu, value);
    }

    public string ComparisonCaptionRu
    {
        get => _comparisonCaptionRu;
        private set => Set(ref _comparisonCaptionRu, value);
    }

    public IReadOnlyList<string> Endpoints
    {
        get => _endpoints;
        private set => Set(ref _endpoints, value);
    }

    /// <summary>Сервер для графика. Пусто — график не строится: усреднять разные серверы нельзя.</summary>
    public string? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (Set(ref _selectedEndpoint, value))
            {
                RecomputeDaily();
            }
        }
    }

    public IReadOnlyList<DailyRttPoint> Daily
    {
        get => _daily;
        private set
        {
            if (Set(ref _daily, value))
            {
                OnPropertyChanged(nameof(HasDaily));
            }
        }
    }

    public bool HasDaily => Daily.Count > 0;

    public string DailyCaptionRu
    {
        get => _dailyCaptionRu;
        private set => Set(ref _dailyCaptionRu, value);
    }

    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            EngineResponse response = await _engine.SendAsync(
                EngineCommandKind.Sessions,
                [("limit", _rowsToAsk.ToString(CultureInfo.InvariantCulture))]).ConfigureAwait(true);

            if (!response.Ok)
            {
                StatusRu = response.MessageRu;
                _loaded = false;
                Rows = [];
                Endpoints = [];
                Daily = [];
                SummaryRu = string.Empty;
                return;
            }

            _loaded = true;
            Rows = Parse(response.DataJson);
            UpdateSelectionAndEndpoints();
            StatusRu = response.MessageRu;
            SummaryRu = BuildSummary();
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (EngineUnavailableException ex)
        {
            _loaded = false;
            StatusRu = ex.Message;
            SummaryRu = "Историю сессий прочитать не удалось: «пусто» сейчас означало бы «не знаем», а не «сессий не было».";
        }
        finally
        {
            IsLoading = false;
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Сравнить две выбранные сессии. Разные серверы — честный отказ, без цифр.</summary>
    public void Compare()
    {
        if (SelectedFirst is null || SelectedSecond is null)
        {
            ComparisonRu = ["Выберите две сессии: сравнивать нужно именно их, а не «что-нибудь»."];
            ComparisonCaptionRu = "Сравнение невозможно";
            return;
        }

        SessionComparisonResult result = SessionComparison.Compare(SelectedFirst, SelectedSecond);
        ComparisonRu = result.Lines;
        ComparisonCaptionRu = result.Comparable
            ? "Сравнение сессий"
            : $"Сравнение невозможно: {result.ReasonRu}";
    }

    /// <summary>
    /// Очистка истории — только по подтверждению. Журнал изменений системы остаётся: это разные вещи.
    /// </summary>
    public async Task ClearAsync()
    {
        if (_dialogs is null)
        {
            StatusRu = "Очистка истории требует подтверждения окна — в этой сборке окна нет. История не тронута.";
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            "Очистить историю сессий",
            "Будут удалены записи о сессиях оптимизации: «до/после», действия и пометки откатa. " +
            "Журнал изменений системы не тронется — «забыть замеры» не значит «забыть, что программа меняла». " +
            "Удалённые записи вернуть нельзя. Продолжить?",
            "Очистить",
            "Отмена").ConfigureAwait(true);

        if (!confirmed)
        {
            StatusRu = "Очистка отменена — история сессий не тронута.";
            return;
        }

        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.SessionsClear).ConfigureAwait(true);
            StatusRu = response.MessageRu;
            if (response.Ok)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
    }

    private void UpdateSelectionAndEndpoints()
    {
        var endpoints = Rows
            .Select(row => row.Endpoint)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(endpoint => endpoint, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Endpoints = endpoints;
        SelectedEndpoint = endpoints.FirstOrDefault();

        SelectedFirst = Rows.Count > 0 ? Rows[0] : null;
        SelectedSecond = Rows.Count > 1 ? Rows[1] : null;

        ComparisonRu = [];
        ComparisonCaptionRu = Rows.Count > 1
            ? "Выберите две сессии к одному серверу и нажмите «Сравнить»."
            : "Для сравнения нужно хотя бы две сессии.";
        RecomputeDaily();
    }

    private void RecomputeDaily()
    {
        Daily = DailyRttHistory.Build(Rows, SelectedEndpoint);

        if (string.IsNullOrWhiteSpace(SelectedEndpoint))
        {
            DailyCaptionRu = "График по дням появится, когда будет хотя бы одна измеренная сессия.";
            return;
        }

        DailyCaptionRu = Daily.Count == 0
            ? $"По серверу {SelectedEndpoint} измеренных сессий пока нет: график не из чего строить, и нулём это не считается."
            : $"Средний RTT «до» по дням. Только сервер {SelectedEndpoint}: сессии к другим серверам не усредняются.";
    }

    private string BuildSummary()
    {
        int completed = Rows.Count(row => row.IsCompleted);
        int unfinished = Rows.Count - completed;
        return $"Сессий: {Rows.Count}, завершено: {completed}, без итога: {unfinished}.";
    }

    private static IReadOnlyList<SessionHistoryRow> Parse(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(dataJson);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("records", out JsonElement records)
                || records.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var rows = new List<SessionHistoryRow>();
            foreach (JsonElement item in records.EnumerateArray())
            {
                var actions = new List<string>();
                if (item.TryGetProperty("actions", out JsonElement actionArray)
                    && actionArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement action in actionArray.EnumerateArray())
                    {
                        if (action.ValueKind == JsonValueKind.String && action.GetString() is { Length: > 0 } title)
                        {
                            actions.Add(title);
                        }
                    }
                }

                rows.Add(new SessionHistoryRow(
                    ReadText(item, "sessionId"),
                    ReadDate(item, "startedUtc"),
                    ReadNullableDate(item, "stoppedUtc"),
                    ReadText(item, "gameTitleRu"),
                    ReadText(item, "endpoint"),
                    ReadBool(item, "hasGame"),
                    ReadBool(item, "isMeasurable"),
                    ReadNumber(item, "baselineRttMs"),
                    ReadNumber(item, "finalRttMs"),
                    ReadNumber(item, "improvementMs"),
                    actions,
                    ReadText(item, "rollbackStateRu"),
                    ReadBool(item, "isCompleted"),
                    ReadText(item, "statusRu"),
                    ReadText(item, "outcome"),
                    ReadText(item, "methodRu"),
                    ReadBool(item, "methodsDiffer")));
            }

            return rows;
        }
    }

    private static string ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
            ? number
            : null;

    private static DateTime ReadDate(JsonElement element, string name) =>
        ReadNullableDate(element, name) ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    private static DateTime? ReadNullableDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTime(out DateTime parsed)
            ? parsed
            : null;
}
