using System.Collections.ObjectModel;
using PingForge.Network.Routes;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Строка списка узлов в блоке «Сервер маршрутизации». Узел — только настоящий: шлюз из таблицы
/// маршрутов, VPN-адаптер или адрес, который человек ввёл сам. Имя, адрес, источник и медиана
/// (если узел измерялся) — всё из данных, никаких украшательств.
/// </summary>
public sealed class RoutingNodeRowViewModel : ObservableObject
{
    private string _name;
    private bool _isDefault;
    private bool _isSecondary;

    public RoutingNodeRowViewModel(RoutingNodeEntry entry, Action makeDefault, Action makeSecondary, Action remove)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _name = entry.Name;

        // Кнопки строки делают то, что скажет родитель: выбор по умолчанию/резерва и удаление — его решения.
        MakeDefaultCommand = new RelayCommand(makeDefault);
        MakeSecondaryCommand = new RelayCommand(makeSecondary);
        RemoveCommand = new RelayCommand(remove);
    }

    public RelayCommand MakeDefaultCommand { get; }

    public RelayCommand MakeSecondaryCommand { get; }

    public RelayCommand RemoveCommand { get; }

    internal RoutingNodeEntry Entry { get; private set; }

    public string Address => Entry.Address;

    public string Name
    {
        get => _name;
        set
        {
            string label = RoutingNodeValidation.SanitizeLabel(value);
            if (label.Length == 0)
            {
                label = Entry.Address;
            }

            Entry = Entry with { Name = label };
            Set(ref _name, label);
        }
    }

    /// <summary>Откуда узел взялся: «текущий шлюз», «второй шлюз», «VPN-адаптер …», «введён вручную».</summary>
    public string SourceRu => Entry.SourceRu;

    public string MedianRu => Entry.MedianRttMs is { } median
        ? $"{median:0.#} мс"
        : "не измерялась";

    public bool IsDefault
    {
        get => _isDefault;
        set => Set(ref _isDefault, value);
    }

    public bool IsSecondary
    {
        get => _isSecondary;
        set => Set(ref _isSecondary, value);
    }

    public string RoleRu => IsDefault ? "основной" : IsSecondary ? "резерв" : string.Empty;

    public void ApplyMeasurement(double? medianMs, double? jitterMs, double? lossPercent, string? nameRu)
    {
        Entry = Entry with
        {
            Name = string.IsNullOrWhiteSpace(nameRu) ? Entry.Name : nameRu!.Trim(),
            MedianRttMs = medianMs,
            JitterMs = jitterMs,
            LossPercent = lossPercent,
            MeasuredUtc = medianMs is null ? Entry.MeasuredUtc : DateTime.UtcNow,
        };

        OnPropertyChanged(nameof(Entry), nameof(Name), nameof(MedianRu));
    }

    public RoutingNodeEntry ToEntry() => Entry;
}

/// <summary>
/// Блок «Сервер маршрутизации» в «Настройках» (задача B3).
///
/// Что здесь есть и чего нет:
///   • список узлов — только шлюзы реальных адаптеров (поиск по таблице маршрутов) и ручной ввод;
///     «серверов PingForge», VPS, платных VPN и «бесплатных ускорителей» в интерфейсе не бывает —
///     своей сети у программы нет, и врать про инфраструктуру нельзя;
///   • «Измерить все пути» — служебная команда: временные /32 на адрес сервера игры, замер, снятие.
///     Программа ничего не включает молча: постоянный маршрут поставит только живая оптимизация
///     по кнопке «Оптимизировать», и только когда есть измеренный выигрыш или выбранный узел;
///   • предупреждение про третью сторону и смену региона (пример Destiny 2) — обязательный текст,
///     решает человек;
///   • DNS сюда не переехал и не переедет: он ускоряет вход в игру, а не пинг в матче — здесь только маршруты.
///
/// Настройки пишутся в общий settings.json сразу при изменении (как свои игры из задачи A): блок
/// переживает перезапуск окна, а служба подхватывает список при следующем запуске оптимизации.
/// </summary>
public sealed class RoutingSettingsViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private readonly IAppSettingsStore _store;
    private readonly IUserDialogs _dialogs;
    private string _addAddress = string.Empty;
    private string _addLabel = string.Empty;
    private bool _autoSelectBest;
    private bool _isBusy;
    private string _statusRu;

    public RoutingSettingsViewModel(EngineClient engine, IAppSettingsStore store, IUserDialogs dialogs)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        AppSettings settings = _store.Load();
        foreach (RoutingNodeEntry entry in settings.RoutingNodes ?? [])
        {
            AddRow(entry);
        }

        _autoSelectBest = settings.RoutingAutoSelect;
        _multipathEnabled = settings.RoutingMultipathEnabled;
        if (settings.RoutingDefaultNode is { Length: > 0 } address)
        {
            foreach (RoutingNodeRowViewModel row in Rows.Where(row =>
                         row.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            {
                row.IsDefault = true;
                break;
            }
        }
        if (settings.RoutingSecondaryNode is { Length: > 0 } sec)
        {
            foreach (RoutingNodeRowViewModel row in Rows.Where(row =>
                         row.Address.Equals(sec, StringComparison.OrdinalIgnoreCase)))
            {
                row.IsSecondary = true;
                break;
            }
        }

        _statusRu = $"Файл настроек: {_store.SettingsFilePath}";

        FindGatewaysCommand = new AsyncRelayCommand(FindGatewaysAsync, () => !IsBusy);
        AddNodeCommand = new AsyncRelayCommand(() => AddNodeAsync(AddAddress, AddLabel), () => !IsBusy);
        MeasureAllCommand = new AsyncRelayCommand(MeasureAllAsync, () => !IsBusy);
        // Команды строк (по умолчанию/удалить) живут у самих строк: RelayCommand здесь без параметра,
        // как в карточках игр — так поведение строки проверяется тестом без XAML-магии.
    }

    /// <summary>Строки-узлы. Пусто — узлов нет, и «Оптимизировать» честно откажет с подсказкой здесь же.</summary>
    public ObservableCollection<RoutingNodeRowViewModel> Rows { get; } = [];

    public AsyncRelayCommand FindGatewaysCommand { get; }

    public AsyncRelayCommand AddNodeCommand { get; }

    public AsyncRelayCommand MeasureAllCommand { get; }

    public string AddAddress
    {
        get => _addAddress;
        set => Set(ref _addAddress, value);
    }

    public string AddLabel
    {
        get => _addLabel;
        set => Set(ref _addLabel, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                FindGatewaysCommand.RaiseCanExecuteChanged();
                AddNodeCommand.RaiseCanExecuteChanged();
                MeasureAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>«Выбирать лучший автоматически»: путь выбирается замером при оптимизации, а не догадкой.</summary>
    public bool AutoSelectBest
    {
        get => _autoSelectBest;
        set
        {
            if (Set(ref _autoSelectBest, value))
            {
                Persist();
                StatusRu = value
                    ? "Автовыбор включён: при нажатии «Оптимизировать» программа измерит прямой путь и путь через каждый " +
                      "узел, и переключится только если выигрыш измерен. Сама по себе она ничего не меняет."
                    : "Автовыбор выключен: используется узел, выбранный по умолчанию, — или ничего, если узел не выбран.";
            }
        }
    }

    private bool _multipathEnabled;
    public bool MultipathEnabled
    {
        get => _multipathEnabled;
        set
        {
            if (Set(ref _multipathEnabled, value))
            {
                Persist();
                StatusRu = value
                    ? "Мультипуть включён: держим основной и горячий резерв. Без своей сети дублировать пакеты нельзя — при подтверждённой просадке основного трафик уходит на второй шлюз: после трёх подряд плохих окон контроля (около 6 с при замере раз в 2 с), а не в тот же миг."
                    : "Мультипуть выключен: используется один узел. Второй шлюз остаётся в списке, но в оптимизации не участвует.";
            }
        }
    }

    public string MultipathNoteRu => "Мультипуть на своих линиях: работает только с двумя шлюзами у вас дома (Ethernet + Wi-Fi/модем). Программа меряет оба пути к серверу игры и держит лучший основным, второй — горячим резервом. Без своей сети пакеты не дублируются по интернету: переключение — сменой маршрута /32, и только после трёх подряд плохих окон контроля (около 6 с при замере раз в 2 с) и только на путь, который сам выигрывает у прямого. Честно: если второго шлюза нет — мультипуть не включается.";


    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>Честное предупреждение рядом с блоком. Текст продукта — проверяется тестом, менять его можно только вместе с тестом.</summary>
    public const string WarningRu =
        "Через чужой узел трафик идёт через третью сторону: она видит, куда и когда вы играете. Часть игр " +
        "(например Destiny 2) блокировала аккаунты за смену региона — решайте осознанно. Программа ничего " +
        "не включает молча: маршрут ставится только по кнопке «Оптимизировать», только на адрес сервера игры " +
        "(/32) и, при автовыборе, только по измеренному выигрышу. Смена DNS к этому блоку отношения не имеет: " +
        "DNS ускоряет вход в игру, а не пинг в матче.";

    /// <summary>Тот же текст для разметки: биндинг XAML видит свойства экземпляра, а не const.</summary>
    public string WarningText => WarningRu;

    public int NodeCount => Rows.Count;

    // ── команды ─────────────────────────────────────────────────────────────────────────────

    /// <summary>«Найти шлюзы»: служба смотрит таблицу маршрутов и список адаптеров. Только чтение системы.</summary>
    public async Task FindGatewaysAsync()
    {
        IsBusy = true;
        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.RouteDiscover).ConfigureAwait(true);

            if (!response.Ok)
            {
                StatusRu = $"Поиск шлюзов не получился: {response.MessageRu}";
                return;
            }

            MergeDiscovered(RoutingWireCodec.DecodeCandidates(response.DataJson));
            Persist();
            StatusRu = response.MessageRu;
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Ручной ввод: строгий IPv4, подпись необязательна, источник — «введён вручную». Мусор не сохраняется.</summary>
    public Task AddNodeAsync(string? address, string? label)
    {
        IReadOnlyList<RoutingNodeEntry> existing = Rows.Select(row => row.ToEntry()).ToList();
        if (!RoutingNodeValidation.TryCreateManual(address, label, existing, out RoutingNodeEntry? node, out string errorRu))
        {
            StatusRu = $"Узел не добавлен: {errorRu}";
            return Task.CompletedTask;
        }

        RoutingNodeRowViewModel row = AddRow(node!);

        // Первый узел логично сделать узлом по умолчанию — иначе «по умолчанию» нет, и оптимизация
        // честно откажет. Но только если по умолчанию до этого ничего не было.
        if (Rows.Count == 1)
        {
            row.IsDefault = true;
        }

        AddAddress = string.Empty;
        AddLabel = string.Empty;

        Persist();
        StatusRu = $"Узел {node!.Address} ({node.SourceRu}) добавлен. Оптимизация поведёт через него игру только " +
                   "после того, как вы нажмёте «Оптимизировать» — и только на адрес сервера игры.";
        return Task.CompletedTask;
    }

    /// <summary>Удаление узла из списка. Маршруты в системе эта кнопка не трогает: они — забота оптимизации и «Починить сеть».</summary>
    public void RemoveNode(RoutingNodeRowViewModel? row)
    {
        if (row is null)
        {
            StatusRu = "Узел не удалён: строка не выбрана.";
            return;
        }

        bool wasDefault = row.IsDefault;
        bool wasSecondary = row.IsSecondary;
        Rows.Remove(row);
        Persist();
        StatusRu = $"Узел {row.Address} убран из списка. " +
                   (wasDefault ? "Узел по умолчанию сброшен: оптимизация честно откажет, пока не выберете другой." :
                    wasSecondary ? "Резерв сброшен: мультипуть остался без второго шлюза." :
                    "Маршруты в системе не менялись — удаление из списка их не снимает.");
    }

    /// <summary>Выбор узла по умолчанию: повторное нажатие на «По умолчанию» у уже выбранного узла снимает выбор.</summary>
    public void MakeDefault(RoutingNodeRowViewModel? row)
    {
        if (row is null)
        {
            StatusRu = "Узел по умолчанию не изменён: строка не выбрана.";
            return;
        }

        bool already = row.IsDefault;
        foreach (RoutingNodeRowViewModel candidate in Rows)
        {
            candidate.IsDefault = false;
        }

        row.IsDefault = !already;
        if (row.IsDefault && row.IsSecondary) row.IsSecondary = false;
        Persist();
        StatusRu = row.IsDefault
            ? $"Узел по умолчанию: {row.Name} ({row.Address}). Оптимизация поведёт игру через него — только /32 на адрес " +
              "сервера и только по кнопке «Оптимизировать»."
            : "Узел по умолчанию снят: без автовыбора оптимизация маршрут менять не будет.";
    }

    public void MakeSecondary(RoutingNodeRowViewModel? row)
    {
        if (row is null)
        {
            StatusRu = "Резерв не изменён: строка не выбрана.";
            return;
        }
        if (row.IsDefault)
        {
            StatusRu = "Основной узел нельзя сделать резервом одновременно — выберите другой шлюз.";
            return;
        }
        bool already = row.IsSecondary;
        foreach (RoutingNodeRowViewModel candidate in Rows) candidate.IsSecondary = false;
        row.IsSecondary = !already;
        Persist();
        StatusRu = row.IsSecondary
            ? $"Резерв: {row.Name} ({row.Address}). При мультипути переключение на него — после подтверждённой просадки основного (три плохих окна контроля подряд)."
            : "Резерв снят: мультипуть будет без ручного резерва (автовыбор возьмёт второй лучший по замерам).";
    }

    /// <summary>
    /// «Измерить все пути»: служба ставит временный /32 на адрес сервера игры, меряет, снимает.
    /// До замера — подтверждение: измерение трогает таблицу маршрутов, хотя и на секунды.
    /// </summary>
    public async Task MeasureAllAsync()
    {
        if (Rows.Count == 0)
        {
            StatusRu = "Измерять нечего: список узлов пуст. Нажмите «Найти шлюзы» или добавьте адрес вручную.";
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            "Измерить пути",
            "Замер делает временный маршрут /32 только на адрес сервера запущенной игры, измеряет путь через каждый " +
            "узел и сразу снимает его. Каждый путь мерится двумя сериями вперемешку, поэтому замер идёт дольше — " +
            "зато дрейф сети не выдаётся за выигрыш узла. Постоянных изменений не остаётся. Запустить замер?",
            "Измерить",
            "Отмена").ConfigureAwait(true);

        if (!confirmed)
        {
            StatusRu = "Замер отменён — маршруты не тронуты.";
            return;
        }

        IsBusy = true;
        try
        {
            EngineResponse response = await _engine.SendAsync(EngineCommandKind.RouteMeasure).ConfigureAwait(true);
            RoutingMeasureDto? report = RoutingWireCodec.DecodeMeasure(response.DataJson);

            if (report is not null)
            {
                foreach (RoutingMeasureNodeDto node in report.Nodes)
                {
                    RoutingNodeRowViewModel? row = Rows.FirstOrDefault(candidate =>
                        candidate.Address.Equals(node.Address, StringComparison.OrdinalIgnoreCase));
                    row?.ApplyMeasurement(node.Success ? node.MedianRttMs : null, node.Success ? node.JitterMs : null,
                        node.Success ? node.LossPercent : null, node.Name);
                }

                Persist();
            }

            StatusRu = response.Ok
                ? response.MessageRu
                : $"Измерение: {response.MessageRu}";

            if (report is not null && report.WinnerAddress is { Length: > 0 } winner)
            {
                foreach (RoutingNodeRowViewModel row in Rows) row.IsDefault = false;
                foreach (RoutingNodeRowViewModel row in Rows.Where(row =>
                             row.Address.Equals(winner, StringComparison.OrdinalIgnoreCase)))
                {
                    row.IsDefault = true;
                    break;
                }

                if (report.RunnerUpAddress is { Length: > 0 } runner && MultipathEnabled)
                {
                    foreach (RoutingNodeRowViewModel row in Rows) row.IsSecondary = false;
                    foreach (RoutingNodeRowViewModel row in Rows.Where(row => row.Address.Equals(runner, StringComparison.OrdinalIgnoreCase)))
                    {
                        row.IsSecondary = true;
                        break;
                    }
                    StatusRu += $" Лучший — {winner}, резерв — {runner}: мультипуть горячий. Применять — только «Оптимизировать».";
                }
                else
                {
                    StatusRu += " Лучший помечен узлом по умолчанию — применять его или нет, решает кнопка «Оптимизировать».";
                }
                Persist();
            }
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── внутренности ─────────────────────────────────────────────────────────────────────────

    private RoutingNodeRowViewModel AddRow(RoutingNodeEntry entry)
    {
        RoutingNodeRowViewModel? bound = null;
        var row = new RoutingNodeRowViewModel(entry, () => MakeDefault(bound), () => MakeSecondary(bound), () => RemoveNode(bound));
        bound = row;
        Rows.Add(row);
        return row;
    }

    private void MergeDiscovered(IReadOnlyList<RoutingCandidateDto> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var byAddress = Rows.ToDictionary(row => row.Address, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RoutingCandidateDto candidate in candidates)
        {
            seen.Add(candidate.Address);
            if (byAddress.TryGetValue(candidate.Address, out RoutingNodeRowViewModel? existing))
            {
                // Сохранённый узел остаётся собой: его подпись и медиана дороже «свежего» имени.
                continue;
            }

            AddRow(new RoutingNodeEntry(
                candidate.Address,
                candidate.Name.Length > 0 ? candidate.Name : candidate.Address,
                RoutingNodeSourceRu.IsHonest(candidate.SourceRu) ? candidate.SourceRu : RoutingNodeSourceRu.SecondGateway));
        }

        // Системный узел, которого больше нет в таблице (адаптер выключили), из списка уходит:
        // висячий «второй шлюз» врал бы. Свои (ручные) — остаются: их человек писал сам.
        List<RoutingNodeRowViewModel> gone = Rows
            .Where(row => !seen.Contains(row.Address) && !row.ToEntry().IsManual)
            .ToList();

        foreach (RoutingNodeRowViewModel row in gone)
        {
            Rows.Remove(row);
        }
    }

    private void Persist()
    {
        try
        {
            AppSettings current = _store.Load();
            _store.Save(current with
            {
                RoutingNodes = Rows.Select(row => row.ToEntry()).ToList(),
                RoutingDefaultNode = Rows.FirstOrDefault(row => row.IsDefault)?.Address,
                RoutingAutoSelect = AutoSelectBest,
                RoutingMultipathEnabled = MultipathEnabled,
                RoutingSecondaryNode = Rows.FirstOrDefault(row => row.IsSecondary)?.Address,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusRu = $"Изменения не записались в настройки: {ex.Message}. В этом запуске блок работает, после перезапуска вернётся прежний список.";
        }
    }
}
