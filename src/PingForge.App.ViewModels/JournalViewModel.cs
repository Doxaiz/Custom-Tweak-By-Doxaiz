using System.Text.Json;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Раздел «Журнал»: что программа меняла и что из этого вышло.
///
/// Правило честности: пустой журнал — это «программа ещё ничего не меняла», а не пустая таблица.
/// Сломанный ответ службы не превращается в «записей нет» — иначе пользователь решит, что всё чисто.
/// </summary>
public sealed class JournalViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private readonly List<JournalRowViewModel> _rows = [];
    private string _statusRu = "Журнал ещё не читали.";
    private bool _isLoading;

    /// <summary>
    /// Читали ли журнал успешно хоть раз. Без этого признака «нет связи» выглядело бы как «записей нет» —
    /// а это разные вещи: в первом случае мы ничего не знаем, во втором знаем, что программа ничего не меняла.
    /// </summary>
    private bool _everLoaded;

    /// <summary>Пытались ли читать журнал вообще. Отличает «ещё не читали» от «прочитать не смогли».</summary>
    private bool _loadAttempted;

    public JournalViewModel(EngineClient engine, int rowsToAsk = 20, IUserDialogs? dialogs = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        RowsToAsk = Math.Clamp(rowsToAsk, 1, EngineDispatcherLimits.MaxJournalLimit);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        // Задача J: вторая вкладка «Сессии» — история замеров «до/после», сравнение и график по дням.
        Sessions = new SessionHistoryViewModel(engine, dialogs);
    }

    /// <summary>
    /// Вкладка «Сессии» (задача J): история оптимизаций в той же базе, что журнал изменений.
    /// Читается отдельной командой службы и не смешивается с «что менялось в системе».
    /// </summary>
    public SessionHistoryViewModel Sessions { get; }

    /// <summary>Сколько записей просить у службы.</summary>
    public int RowsToAsk { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<JournalRowViewModel> Rows => _rows;

    public bool IsEmpty => _rows.Count == 0;

    /// <summary>Журнал прочитан успешно: только тогда «пусто» означает «программа ничего не меняла».</summary>
    public bool IsLoaded => _everLoaded;

    /// <summary>Чтение не удалось: таблица пустая не потому, что изменений не было, а потому что мы не смогли их прочитать.</summary>
    public bool IsUnavailable => !_everLoaded && _loadAttempted;

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    /// <summary>Сколько изменений не подтвердилось — это то, что пользователь ищет в журнале первым делом.</summary>
    public int FailedCount => _rows.Count(row => !row.Succeeded);

    public int RebootPendingCount => _rows.Count(row => row.Succeeded && row.RebootNeeded);

    public string SummaryRu
    {
        get
        {
            if (_rows.Count > 0)
            {
                string failed = FailedCount == 0 ? "все записи успешные" : $"не получилось: {FailedCount}";
                return $"Записей: {_rows.Count}, {failed}." +
                    (RebootPendingCount == 0 ? string.Empty : $" Ждут перезагрузки: {RebootPendingCount}.");
            }

            if (!_everLoaded)
            {
                return _loadAttempted
                    ? "Журнал не прочитан: связи со службой нет, поэтому сказать «изменений не было» нельзя."
                    : "Журнал ещё не читали.";
            }

            return "Программа ещё ничего не меняла.";
        }
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        _loadAttempted = true;
        try
        {
            EngineResponse response = await _engine.SendAsync(
                EngineCommandKind.JournalTail,
                [("limit", RowsToAsk.ToString())]).ConfigureAwait(true);

            if (!response.Ok)
            {
                StatusRu = response.MessageRu;
                OnPropertyChanged(nameof(SummaryRu), nameof(IsUnavailable), nameof(IsLoaded));
                return;
            }

            _everLoaded = true;
            _rows.Clear();
            _rows.AddRange(Parse(response.DataJson));
            StatusRu = response.MessageRu;
            OnPropertyChanged(nameof(Rows), nameof(IsEmpty), nameof(SummaryRu), nameof(FailedCount),
                nameof(RebootPendingCount), nameof(IsUnavailable), nameof(IsLoaded));
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
            OnPropertyChanged(nameof(SummaryRu), nameof(IsUnavailable), nameof(IsLoaded));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IEnumerable<JournalRowViewModel> Parse(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(dataJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("records", out JsonElement records) ||
                records.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement item in records.EnumerateArray())
            {
                yield return new JournalRowViewModel(
                    ReadDate(item, "loggedUtc"),
                    ReadText(item, "tweakTitleRu"),
                    ReadText(item, "tweakCode"),
                    ReadText(item, "previousValueText"),
                    ReadText(item, "appliedValueText"),
                    ReadText(item, "verifiedActualText"),
                    ReadBool(item, "succeeded"),
                    ReadBool(item, "rebootNeeded"),
                    ReadText(item, "explanationRu"));
            }
        }
    }

    private static string ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static DateTime ReadDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTime(out DateTime parsed)
            ? parsed
            : DateTime.MinValue;
}

/// <summary>Пределы, которые служба объявляет в протоколе: окно не должно просить больше, чем служба отдаёт.</summary>
public static class EngineDispatcherLimits
{
    public const int MaxJournalLimit = 500;
}
