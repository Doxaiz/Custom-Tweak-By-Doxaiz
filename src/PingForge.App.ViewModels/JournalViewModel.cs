using System.Text;
using System.Text.Json;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Раздел «Журнал»: что программа меняла и что из этого вышло (задача K — фильтры, поиск, CSV, пагинация).
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
    private bool _everLoaded;
    private bool _loadAttempted;
    private string _searchText = string.Empty;
    private JournalKindFilter _kindFilter = JournalKindFilter.All;
    private JournalStatusFilter _statusFilter = JournalStatusFilter.All;
    private JournalDateFilter _dateFilter = JournalDateFilter.All;
    private int _pageSize = 20;
    private int _displayCount = 20;

    public JournalViewModel(EngineClient engine, int rowsToAsk = 20, IUserDialogs? dialogs = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        RowsToAsk = Math.Clamp(rowsToAsk, 1, EngineDispatcherLimits.MaxJournalLimit);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ShowMoreCommand = new RelayCommand(ShowMore, () => HasMore);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => HasActiveFilters);
        Sessions = new SessionHistoryViewModel(engine, dialogs);
    }

    public SessionHistoryViewModel Sessions { get; }
    public int RowsToAsk { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ShowMoreCommand { get; }
    public AsyncRelayCommand ExportCsvCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    public IReadOnlyList<JournalRowViewModel> Rows => _rows;

    public bool IsEmpty => _rows.Count == 0;
    public bool IsLoaded => _everLoaded;
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

    // ---- Задача K: фильтры / поиск / пагинация ----

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                ResetPagination();
                OnPropertyChanged(nameof(FilteredRows), nameof(DisplayedRows), nameof(HasMore), nameof(IsFiltered), nameof(IsFilteredEmpty), nameof(FilterHintRu));
                ClearFiltersCommand.RaiseCanExecuteChanged();
                ShowMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public JournalKindFilter KindFilter
    {
        get => _kindFilter;
        set
        {
            if (Set(ref _kindFilter, value))
            {
                ResetPagination();
                OnPropertyChanged(nameof(FilteredRows), nameof(DisplayedRows), nameof(HasMore), nameof(IsFiltered), nameof(IsFilteredEmpty), nameof(FilterHintRu));
                ClearFiltersCommand.RaiseCanExecuteChanged();
                ShowMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public JournalStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (Set(ref _statusFilter, value))
            {
                ResetPagination();
                OnPropertyChanged(nameof(FilteredRows), nameof(DisplayedRows), nameof(HasMore), nameof(IsFiltered), nameof(IsFilteredEmpty), nameof(FilterHintRu));
                ClearFiltersCommand.RaiseCanExecuteChanged();
                ShowMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public JournalDateFilter DateFilter
    {
        get => _dateFilter;
        set
        {
            if (Set(ref _dateFilter, value))
            {
                ResetPagination();
                OnPropertyChanged(nameof(FilteredRows), nameof(DisplayedRows), nameof(HasMore), nameof(IsFiltered), nameof(IsFilteredEmpty), nameof(FilterHintRu));
                ClearFiltersCommand.RaiseCanExecuteChanged();
                ShowMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Фильтр активен — хотя бы один отличается от «всё» или есть поисковый запрос.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(_searchText)
        || _kindFilter != JournalKindFilter.All
        || _statusFilter != JournalStatusFilter.All
        || _dateFilter != JournalDateFilter.All;

    public bool HasActiveFilters => IsFiltered;

    /// <summary>Размер страницы для «показать ещё»: по умолчанию 20 в UI, но журнал помнит до 200/500.</summary>
    public int PageSize
    {
        get => _pageSize;
        set => Set(ref _pageSize, Math.Clamp(value, 1, 100));
    }

    public IReadOnlyList<JournalRowViewModel> FilteredRows => _rows.Where(MatchesFilters).ToList();

    public IReadOnlyList<JournalRowViewModel> DisplayedRows => FilteredRows.Take(_displayCount).ToList();

    public bool HasMore => FilteredRows.Count > _displayCount;

    public bool IsFilteredEmpty => _rows.Count > 0 && FilteredRows.Count == 0;

    public string FilterHintRu => IsFilteredEmpty
        ? "Ничего не нашлось по этим фильтрам. Попробуйте расширить условия или очистить поиск."
        : string.Empty;

    /// <summary>Подпись над таблицей с учётом пагинации: «Показано 20 из 200».</summary>
    public string PaginationLineRu
    {
        get
        {
            int total = FilteredRows.Count;
            int shown = Math.Min(_displayCount, total);
            if (total == 0) return string.Empty;
            if (total <= _displayCount) return $"Показано {total} из {total}.";
            return $"Показано {shown} из {total} — нажмите «Показать ещё».";
        }
    }

    /// <summary>Сбросить фильтры к «всё».</summary>
    public void ClearFilters()
    {
        _searchText = string.Empty;
        _kindFilter = JournalKindFilter.All;
        _statusFilter = JournalStatusFilter.All;
        _dateFilter = JournalDateFilter.All;
        ResetPagination();
        OnPropertyChanged(nameof(SearchText), nameof(KindFilter), nameof(StatusFilter), nameof(DateFilter),
            nameof(FilteredRows), nameof(DisplayedRows), nameof(HasMore), nameof(IsFiltered), nameof(IsFilteredEmpty), nameof(FilterHintRu), nameof(PaginationLineRu));
        ClearFiltersCommand.RaiseCanExecuteChanged();
        ShowMoreCommand.RaiseCanExecuteChanged();
    }

    public void ShowMore()
    {
        _displayCount = Math.Min(FilteredRows.Count, _displayCount + PageSize);
        OnPropertyChanged(nameof(DisplayedRows), nameof(HasMore), nameof(PaginationLineRu));
        ShowMoreCommand.RaiseCanExecuteChanged();
    }

    private void ResetPagination()
    {
        _displayCount = PageSize;
        OnPropertyChanged(nameof(DisplayedRows), nameof(HasMore), nameof(PaginationLineRu));
    }

    private bool MatchesFilters(JournalRowViewModel row) =>
        JournalFilters.MatchesKind(row, _kindFilter) &&
        JournalFilters.MatchesStatus(row, _statusFilter) &&
        JournalFilters.MatchesDate(row, _dateFilter) &&
        JournalFilters.MatchesSearch(row, _searchText);

    // ---- CSV ----

    /// <summary>
    /// Собрать CSV из отфильтрованных строк. Экранирование и защита от формул — в
    /// <see cref="JournalCsv"/>: там это проверяется тестами, включая поля из реестра, которые
    /// начинаются с «=» и в таблице стали бы формулой.
    /// </summary>
    public string BuildCsv() => JournalCsv.Build(FilteredRows);

    /// <summary>Экспорт CSV в файл: по умолчанию — в папку документов; путь возвращает в статусе.</summary>
    public async Task ExportCsvAsync()
    {
        string fileName = $"journal-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.GetTempPath();
        }

        await ExportCsvToAsync(Path.Combine(dir, fileName)).ConfigureAwait(true);
    }

    /// <summary>
    /// Экспорт в названный файл — тот же путь, что у кнопки, только имя задано заранее: так его
    /// проверяют тесты. Файл пишется целиком во временный рядом и подменяет целевой одним движением
    /// (<see cref="AtomicFileWrite"/>), а существующий не затирается молча: два экспорта с одним
    /// именем не должны съедать друг друга.
    /// </summary>
    public async Task ExportCsvToAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string csv = BuildCsv();
        try
        {
            await AtomicFileWrite.WriteAllTextAsync(path, csv, Encoding.UTF8, overwrite: false).ConfigureAwait(true);

            // Число читается один раз: цифра и слово не могут разойтись.
            int rows = FilteredRows.Count;
            StatusRu = $"CSV сохранён: {path} ({rows} {TweakOrdering.Plural(rows, "строка", "строки", "строк")}). " +
                "Значения, которые таблица приняла бы за формулу, помечены апострофом — это текст, а не вычисление.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusRu = $"Не удалось сохранить CSV: {ex.Message}";
        }
    }

    // ---- Существующее: загрузка ----

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
            ResetPagination();
            StatusRu = response.MessageRu;
            OnPropertyChanged(nameof(Rows), nameof(FilteredRows), nameof(DisplayedRows), nameof(HasMore), nameof(IsFilteredEmpty), nameof(PaginationLineRu), nameof(IsEmpty), nameof(SummaryRu), nameof(FailedCount),
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

public static class EngineDispatcherLimits
{
    public const int MaxJournalLimit = 500;
}
