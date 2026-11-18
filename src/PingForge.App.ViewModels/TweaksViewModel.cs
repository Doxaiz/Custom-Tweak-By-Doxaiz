using System.Text.Json;
using PingForge.Core.Models;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Раздел «Твики»: 47 карточек с ползунками, пресеты, массовый выбор и применение.
///
/// Что здесь проверяется тестами и почему это важно:
///   • «включить все» и «включить рекомендуемое» не включают твики с вердиктом «не советуем» —
///     массовая кнопка не может протащить то, что каталог не рекомендует;
///   • применение всегда идёт с подтверждением, в котором названо число твиков;
///   • отчёт после применения честный: «применено 12 из 19, ошибок 1», а не «готово».
/// </summary>
public sealed class TweaksViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private readonly IUserDialogs _dialogs;
    private readonly List<TweakRowViewModel> _rows;
    private string _statusRu = "Выберите пресет или отметьте твики вручную. Ничего не применяется до нажатия кнопки.";
    private PresetViewModel? _selectedPreset;
    private bool _isApplying;
    private List<TweakRowViewModel>? _visibleFilter;
    private TweakOrder _order = TweakOrder.ByPurpose;
    private string _searchRu = string.Empty;
    private IReadOnlyList<TweakGroupViewModel> _groups = [];
    private IReadOnlyList<TweakRowViewModel> _visibleRows = [];

    public TweaksViewModel(
        EngineClient engine,
        IUserDialogs dialogs,
        IReadOnlyList<TweakRowViewModel> rows,
        IReadOnlyList<PresetViewModel> presets)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _rows = [.. rows ?? throw new ArgumentNullException(nameof(rows))];
        Presets = presets ?? throw new ArgumentNullException(nameof(presets));

        ApplyPresetCommand = new AsyncRelayCommand(ApplySelectedPresetAsync, () => SelectedPreset is not null);
        RollbackCommand = new AsyncRelayCommand(RollbackAsync);
        ApplySelectedCommand = new AsyncRelayCommand(ApplyCheckedAsync, () => CheckedCount > 0);
        SelectRecommendedCommand = new RelayCommand(() => SelectRecommended());
        SelectAllCommand = new RelayCommand(() => SelectAll());
        SelectNoneCommand = new RelayCommand(() => SelectNone());
        ClearFilterCommand = new RelayCommand(ClearFilter, () => IsFiltered);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => _searchRu.Length > 0);

        Orders =
        [
            new TweakOrderOptionViewModel(TweakOrder.ByPurpose, SelectOrder),
            new TweakOrderOptionViewModel(TweakOrder.RecommendedFirst, SelectOrder),
            new TweakOrderOptionViewModel(TweakOrder.Alphabetical, SelectOrder),
        ];

        foreach (TweakRowViewModel row in _rows)
        {
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CheckedCount), nameof(SelectionLineRu));
                ApplySelectedCommand.RaiseCanExecuteChanged();

                // Подписи групп считаются по ползункам: переключили твик — «включено 5» в заголовке
                // должно измениться сразу. Сами группы при этом не пересобираются, иначе открытые
                // блоки «Подробнее» схлопывались бы у человека на глазах.
                foreach (TweakGroupViewModel group in _groups)
                {
                    group.RefreshCounts();
                }
            };
        }

        Rebuild();
        Orders[0].MarkSelected(true);
    }

    public IReadOnlyList<TweakRowViewModel> Rows => _rows;

    /// <summary>
    /// Карточка «DNS — это про вход, а не про пинг» (задача D). Список резолверов — из
    /// GameRouteCatalog.FreeDnsRoutes, тот же, что меряет «Проверить сеть». Ничего не применяет:
    /// это объяснение и выбор входа, а не «ускоритель пинга».
    /// </summary>
    public DnsCardViewModel Dns { get; } = new();

    /// <summary>
    /// Карточка «Задержка ввода» (задача F): советник из PingForge.Analysis, подключённый к окну.
    /// Прикрепляется после построения: ей нужен переход к отфильтрованному списку этого же раздела.
    /// </summary>
    public InputLatencyViewModel? InputLatency { get; private set; }

    /// <summary>Экран «Что изменилось» (задача K) — появляется после применения.</summary>
    public AppliedChangesViewModel Changes { get; } = new();

    public void AttachInputLatency(InputLatencyViewModel card)
    {
        InputLatency = card ?? throw new ArgumentNullException(nameof(card));
        OnPropertyChanged(nameof(InputLatency));
    }

    /// <summary>
    /// Строки, которые видит человек, — в том порядке, в каком они стоят на экране. Это и каталог,
    /// и фильтр по кодам (задача F), и поиск; плоский список нужен тем, кто не смотрит на группы.
    /// </summary>
    public IReadOnlyList<TweakRowViewModel> VisibleRows => _visibleRows;

    /// <summary>Группы карточек на экране: «по назначению» — по подкатегориям, иначе одна группа.</summary>
    public IReadOnlyList<TweakGroupViewModel> Groups => _groups;

    /// <summary>Режимы порядка для кнопок-пилюль. Подписи и пояснения — из <see cref="TweakOrdering"/>.</summary>
    public IReadOnlyList<TweakOrderOptionViewModel> Orders { get; }

    /// <summary>Текущий порядок карточек.</summary>
    public TweakOrder Order => _order;

    /// <summary>Строка, по которой ищут: название, описание или код.</summary>
    public string SearchRu
    {
        get => _searchRu;
        set
        {
            string cleaned = value ?? string.Empty;
            if (!Set(ref _searchRu, cleaned))
            {
                return;
            }

            ClearSearchCommand.RaiseCanExecuteChanged();
            Rebuild();
        }
    }

    /// <summary>Подпись под поиском: сколько нашлось и по чему идёт поиск.</summary>
    public string SearchLineRu
    {
        get
        {
            if (_searchRu.Trim().Length == 0)
            {
                return "Поиск идёт по названию, описанию, коду и назначению — например, «DNS» или «TCP_ACK».";
            }

            if (_visibleRows.Count == 0)
            {
                return "Ничего не найдено: измените запрос. Отметки и пресеты при этом не менялись.";
            }

            return _visibleRows.Count == 1
                ? "Найден 1 твик — поиск идёт по названию, описанию, коду и назначению."
                : $"Найдено {_visibleRows.Count} из {_rows.Count} — поиск идёт по названию, описанию, коду и назначению.";
        }
    }

    /// <summary>Строка для пустого экрана: показывается только когда показывать нечего.</summary>
    public string EmptyLineRu => _visibleRows.Count > 0
        ? string.Empty
        : _searchRu.Trim().Length == 0
            ? "Список пуст: в каталоге нет ни одного твика."
            : "По этому запросу в каталоге ничего нет. Очистите поиск или измените запрос.";

    public bool HasVisibleRows => _visibleRows.Count > 0;

    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Сменить порядок карточек: тот же список, другая последовательность.</summary>
    public void SelectOrder(TweakOrder order)
    {
        if (_order == order)
        {
            return;
        }

        _order = order;
        foreach (TweakOrderOptionViewModel option in Orders)
        {
            option.MarkSelected(option.Order == order);
        }

        OnPropertyChanged(nameof(Order), nameof(OrderLineRu));
        Rebuild();
    }

    /// <summary>Строка о текущем порядке: чем он полезен, а не просто «сортировка».</summary>
    public string OrderLineRu =>
        $"{TweakOrdering.TitleRu(_order)}: {TweakOrdering.HintRu(_order)}";

    /// <summary>Сбросить поиск: снова видно всё, что не урезано фильтром по кодам.</summary>
    public void ClearSearch()
    {
        if (_searchRu.Length == 0)
        {
            return;
        }

        _searchRu = string.Empty;
        OnPropertyChanged(nameof(SearchRu));
        ClearSearchCommand.RaiseCanExecuteChanged();
        Rebuild();
    }

    /// <summary>Включён ли фильтр по кодам (из карточки задержки ввода или проверки настроек).</summary>
    public bool IsFiltered => _visibleFilter is not null;

    /// <summary>Подпись фильтра: сколько строк показано из скольких — числа считаются, а не пишутся.</summary>
    public string FilterLineRu => IsFiltered
        ? $"Показаны {VisibleRows.Count} из {_rows.Count} твиков — связанных с последней проверкой."
        : string.Empty;

    public RelayCommand ClearFilterCommand { get; }

    /// <summary>
    /// Показать только перечисленные твики — так работают кнопки «Показать связанные твики»
    /// из карточки задержки ввода и «Показать в твиках» из проверки настроек. Отбор — и ничего больше:
    /// ползунки, пресеты и применение остаются в полном списке.
    /// </summary>
    public void ShowOnlyCodes(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var wanted = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        var matched = _rows.Where(row => wanted.Contains(row.Code)).ToList();

        // Ни одного совпадения — фильтр не включается: пустой список без объяснения был бы ошибкой.
        _visibleFilter = matched.Count > 0 ? matched : null;
        Rebuild();
        OnPropertyChanged(nameof(IsFiltered), nameof(FilterLineRu));
        ClearFilterCommand.RaiseCanExecuteChanged();
        StatusRu = matched.Count == 0
            ? "Среди твиков каталога нет ни одного из перечисленных кодов — фильтр не включён, показан весь список."
            : $"Фильтр: показаны только твики из проверки ({matched.Count} из {_rows.Count}). Отметки и пресеты не менялись.";
    }

    /// <summary>Убрать фильтр: снова виден весь каталог.</summary>
    public void ClearFilter()
    {
        _visibleFilter = null;
        Rebuild();
        OnPropertyChanged(nameof(IsFiltered), nameof(FilterLineRu));
        ClearFilterCommand.RaiseCanExecuteChanged();
        StatusRu = "Фильтр снят: показан весь каталог твиков.";
    }

    /// <summary>
    /// Пересобрать экранный список: фильтр по кодам → поиск → порядок и группы. Одна точка, где
    /// решается, что видно, — иначе поиск, фильтр и порядок начинают спорить друг с другом.
    /// </summary>
    private void Rebuild()
    {
        IEnumerable<TweakRowViewModel> source = _visibleFilter ?? _rows;

        IEnumerable<TweakRowViewModel> filtered = string.IsNullOrWhiteSpace(_searchRu)
            ? source
            : source.Where(row => TweakOrdering.MatchesSearch(row, _searchRu));

        _groups = TweakOrdering.BuildGroups(filtered, _order);
        _visibleRows = _groups.SelectMany(group => group.Rows).ToList();

        OnPropertyChanged(
            nameof(Groups), nameof(VisibleRows), nameof(SearchLineRu), nameof(EmptyLineRu),
            nameof(HasVisibleRows), nameof(OrderLineRu));
    }

    /// <summary>
    /// Отметить ровно перечисленные коды — так работает «Применить рекомендованное» из проверки
    /// сети (задача G). Ровно, а не «вдобавок»: чужие отметки человека не должны уехать в применение
    /// незаметно. Само применение не запускается — его запускает обычная кнопка со всеми
    /// подтверждениями; здесь только расставляются ползунки. Возвращает, сколько кодов нашлось.
    /// </summary>
    public int SelectCodesForApply(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var wanted = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        int matched = 0;

        foreach (TweakRowViewModel row in _rows)
        {
            bool shouldBeOn = wanted.Contains(row.Code);
            if (shouldBeOn)
            {
                matched++;
            }

            row.IsOn = shouldBeOn;
        }

        StatusRu = matched == 0
            ? "Проверка сети передала коды, которых нет в каталоге, — отметки сняты, применять нечего."
            : $"Отмечено по проверке сети: {matched} твик(ов). Дальше — обычная кнопка применения с подтверждением.";

        return matched;
    }

    public IReadOnlyList<PresetViewModel> Presets { get; }

    public AsyncRelayCommand ApplyPresetCommand { get; }

    public AsyncRelayCommand ApplySelectedCommand { get; }

    /// <summary>Вернуть прежние настройки одной кнопкой.</summary>
    public AsyncRelayCommand RollbackCommand { get; }

    public RelayCommand SelectRecommendedCommand { get; }

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public PresetViewModel? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (Set(ref _selectedPreset, value))
            {
                ApplyPresetCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedPresetNoteRu));
            }
        }
    }

    /// <summary>Пояснение к выбранному пресету: что именно он делает и сколько твиков применит.</summary>
    public string SelectedPresetNoteRu => SelectedPreset is null
        ? "Пресет не выбран."
        : $"{SelectedPreset.PurposeRu}. Применится твиков: {SelectedPreset.Count}.";

    public bool IsApplying
    {
        get => _isApplying;
        private set => Set(ref _isApplying, value);
    }

    public int CheckedCount => _rows.Count(row => row.IsOn);

    /// <summary>Число включённых нельзя выдумывать: показываем ровно то, что стоит в ползунках.</summary>
    public string SelectionLineRu => $"Отмечено {CheckedCount} из {_rows.Count}";

    /// <summary>Сколько рекомендованных — считается по каталогу, а не берётся из константы.</summary>
    public int RecommendedCount => _rows.Count(row => row.IsRecommended);

    /// <summary>«Только рекомендуемое»: включает рекомендованные, выключает остальные — включая «не советуем».</summary>
    public void SelectRecommended()
    {
        foreach (TweakRowViewModel row in _rows)
        {
            row.IsOn = row.IsRecommended;
        }

        StatusRu = $"Оставлено только рекомендуемое: {CheckedCount} твиков из {_rows.Count}. " +
            "Остальные («по желанию», «осторожно», «не советуем») выключены.";
    }

    /// <summary>«Включить все»: включает всё, кроме «не советуем» — их можно включить только вручную.</summary>
    public void SelectAll()
    {
        int skipped = 0;
        foreach (TweakRowViewModel row in _rows)
        {
            if (row.IsDangerous)
            {
                row.IsOn = false;
                skipped++;
                continue;
            }

            row.IsOn = true;
        }

        StatusRu = skipped == 0
            ? $"Включено всё: {CheckedCount} твиков."
            : $"Включено {CheckedCount} твиков. Не включены {skipped} штуки с вердиктом «не советуем»: " +
              "их можно поставить только вручную и с пониманием, что они ломают.";
    }

    /// <summary>«Отключить всё».</summary>
    public void SelectNone()
    {
        foreach (TweakRowViewModel row in _rows)
        {
            row.IsOn = false;
        }

        StatusRu = "Все твики выключены. Ничего не изменится, пока не отметите нужное.";
    }

    /// <summary>Применить выбранный пресет: сначала подтверждение, потом служба, потом честный отчёт.</summary>
    public async Task ApplySelectedPresetAsync()
    {
        if (SelectedPreset is null)
        {
            StatusRu = "Пресет не выбран: нажмите на один из трёх, чтобы применить.";
            return;
        }

        PresetViewModel preset = SelectedPreset;

        bool askRestorePoint = true;
        bool confirmed = await _dialogs.ConfirmAsync(
            $"Применить пресет «{preset.TitleRu}»?",
            $"Будет изменено {preset.Count} настроек Windows из {_rows.Count} в каталоге. " +
            "Перед изменениями программа запомнит прежние значения — вернуть их можно одной кнопкой.",
            "Применить",
            "Отмена").ConfigureAwait(true);

        if (!confirmed)
        {
            StatusRu = $"Применение пресета «{preset.TitleRu}» отменено — ничего не менялось.";
            return;
        }

        askRestorePoint = await _dialogs
            .OfferRestorePointAsync(preset.Count, preset.TitleRu)
            .ConfigureAwait(true);

        IsApplying = true;
        StatusRu = $"Применяю пресет «{preset.TitleRu}»…";
        try
        {
            EngineResponse response = await _engine.SendAsync(
                EngineCommandKind.ApplyPreset,
                [("preset", preset.WireName), ("restorePoint", askRestorePoint ? "true" : "false")]).ConfigureAwait(true);

            StatusRu = response.MessageRu;
            TryUpdateChangesFromResponse(response);
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Применить то, что отмечено руками. Список идёт по кодам — так же, как в команде службы.</summary>
    public async Task ApplyCheckedAsync()
    {
        List<TweakRowViewModel> checkedRows = _rows.Where(row => row.IsOn).ToList();
        if (checkedRows.Count == 0)
        {
            StatusRu = "Ни один твик не отмечен: применять нечего.";
            return;
        }

        int dangerous = checkedRows.Count(row => row.IsDangerous);

        string warning = dangerous == 0
            ? string.Empty
            : $"\n\nСреди них {dangerous} с вердиктом «не советуем» — они помечены в списке. " +
              "Их включают только осознанно: они ломают устойчивость соединения или загрузки.";

        bool confirmed = await _dialogs.ConfirmAsync(
            $"Применить {checkedRows.Count} твиков?",
            "Перед изменениями программа запомнит прежние значения и предложит точку восстановления Windows." +
            warning,
            "Применить",
            "Отмена").ConfigureAwait(true);

        if (!confirmed)
        {
            StatusRu = "Применение отменено — ничего не менялось.";
            return;
        }

        bool askRestorePoint = await _dialogs
            .OfferRestorePointAsync(checkedRows.Count, "свой набор")
            .ConfigureAwait(true);

        IsApplying = true;
        StatusRu = $"Применяю {checkedRows.Count} твиков…";
        try
        {
            EngineResponse response = await _engine.SendAsync(
                EngineCommandKind.ApplyTweaks,
                [
                    ("tweaks", string.Join(',', checkedRows.Select(row => row.Code))),
                    ("restorePoint", askRestorePoint ? "true" : "false"),
                ]).ConfigureAwait(true);

            StatusRu = response.MessageRu;
            TryUpdateChangesFromResponse(response);
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Вернуть прежние настройки из последнего снапшота. Всегда с подтверждением.</summary>
    public async Task RollbackAsync()
    {
        bool confirmed = await _dialogs.ConfirmAsync(
            "Вернуть прежние настройки?",
            "Программа вернёт значения, которые были до последнего применения: и твики, и сетевые настройки. " +
            "Свои изменения она снимает полностью.",
            "Вернуть",
            "Отмена").ConfigureAwait(true);

        if (!confirmed)
        {
            StatusRu = "Возврат отменён — ничего не менялось.";
            return;
        }

        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.RollbackSnapshot).ConfigureAwait(true);
            StatusRu = response.MessageRu;
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
    }

    private void TryUpdateChangesFromResponse(EngineResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.DataJson))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.DataJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("snapshotId", out JsonElement sidEl) || sidEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string snapshotId = sidEl.GetString() ?? string.Empty;
            int applied = root.TryGetProperty("applied", out JsonElement aEl) && aEl.ValueKind == JsonValueKind.Number ? aEl.GetInt32() : 0;
            int noOp = root.TryGetProperty("noOp", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.Number ? nEl.GetInt32() : 0;
            int skipped = root.TryGetProperty("skipped", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt32() : 0;
            int failed = root.TryGetProperty("failed", out JsonElement fEl) && fEl.ValueKind == JsonValueKind.Number ? fEl.GetInt32() : 0;

            var rows = new List<AppliedChangeRowViewModel>();
            if (root.TryGetProperty("rows", out JsonElement rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in rowsEl.EnumerateArray())
                {
                    string code = item.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                    string title = item.TryGetProperty("titleRu", out JsonElement t2) && t2.ValueKind == JsonValueKind.String ? t2.GetString() ?? code : code;
                    string before = item.TryGetProperty("previousText", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? string.Empty : string.Empty;
                    string appliedText = item.TryGetProperty("appliedText", out JsonElement at) && at.ValueKind == JsonValueKind.String ? at.GetString() ?? string.Empty : string.Empty;
                    string verified = item.TryGetProperty("verifiedText", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
                    string outcome = item.TryGetProperty("outcome", out JsonElement o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? string.Empty : string.Empty;
                    string status = item.TryGetProperty("statusRu", out JsonElement st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? outcome : outcome;
                    bool succeeded = item.TryGetProperty("succeeded", out JsonElement su) && su.ValueKind == JsonValueKind.True;
                    bool reboot = item.TryGetProperty("rebootNeeded", out JsonElement rb) && rb.ValueKind == JsonValueKind.True;
                    rows.Add(new AppliedChangeRowViewModel(code, title, before, appliedText, verified, outcome, status, succeeded, reboot));
                }
            }

            Changes.UpdateFromBatch(snapshotId, applied, noOp, skipped, failed, rows);
            Changes.BindRevertActions(_engine);
        }
        catch (JsonException)
        {
            // битый JSON — оставляем прежний статус, экран не появляется
        }
    }
}
