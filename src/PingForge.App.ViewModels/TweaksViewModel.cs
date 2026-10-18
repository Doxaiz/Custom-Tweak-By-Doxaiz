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

        foreach (TweakRowViewModel row in _rows)
        {
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CheckedCount), nameof(SelectionLineRu));
                ApplySelectedCommand.RaiseCanExecuteChanged();
            };
        }
    }

    public IReadOnlyList<TweakRowViewModel> Rows => _rows;

    /// <summary>
    /// Карточка «Задержка ввода» (задача F): советник из PingForge.Analysis, подключённый к окну.
    /// Прикрепляется после построения: ей нужен переход к отфильтрованному списку этого же раздела.
    /// </summary>
    public InputLatencyViewModel? InputLatency { get; private set; }

    public void AttachInputLatency(InputLatencyViewModel card)
    {
        InputLatency = card ?? throw new ArgumentNullException(nameof(card));
        OnPropertyChanged(nameof(InputLatency));
    }

    /// <summary>Строки, которые видит человек: весь каталог или отфильтрованная часть (задача F).</summary>
    public IReadOnlyList<TweakRowViewModel> VisibleRows => _visibleFilter ?? _rows;

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
        OnPropertyChanged(nameof(VisibleRows), nameof(IsFiltered), nameof(FilterLineRu));
        ClearFilterCommand.RaiseCanExecuteChanged();
        StatusRu = matched.Count == 0
            ? "Среди твиков каталога нет ни одного из перечисленных кодов — фильтр не включён, показан весь список."
            : $"Фильтр: показаны только твики из проверки ({matched.Count} из {_rows.Count}). Отметки и пресеты не менялись.";
    }

    /// <summary>Убрать фильтр: снова виден весь каталог.</summary>
    public void ClearFilter()
    {
        _visibleFilter = null;
        OnPropertyChanged(nameof(VisibleRows), nameof(IsFiltered), nameof(FilterLineRu));
        ClearFilterCommand.RaiseCanExecuteChanged();
        StatusRu = "Фильтр снят: показан весь каталог твиков.";
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
}
