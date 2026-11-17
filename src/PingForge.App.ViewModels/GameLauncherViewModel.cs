using PingForge.Core.Models;
using PingForge.Network;

namespace PingForge.App.ViewModels;

/// <summary>Где искать установленную игру. Настоящий поиск — в слое Windows, двойник — в тестах.</summary>
public interface IGameExecutableProbe
{
    /// <summary>Найти файлы игр по именам процессов. Чего не нашли — того нет в словаре.</summary>
    IReadOnlyDictionary<string, string> FindInstalled(IReadOnlyCollection<string> processNames);
}

/// <summary>Запустить игру и проверить, запущена ли она. Отдельный порт, чтобы это можно было проверить тестом.</summary>
public interface IGameStarter
{
    bool TryStart(string executablePath, out string errorRu);

    bool IsRunning(string processName);
}

/// <summary>Выбор .exe руками — для игр, которых нет в известных папках (или в нестандартной установке).</summary>
public interface IExecutablePicker
{
    string? Pick(string titleRu);
}

/// <summary>
/// Одна игра в списке запуска.
///
/// Честность важнее красоты: если игра не найдена, карточка так и говорит и предлагает указать путь.
/// Выдуманных «найдено» здесь быть не может — путь либо существует на диске, либо его нет.
/// </summary>
public sealed class GameCardViewModel : ObservableObject
{
    private readonly Func<GameCardViewModel, Task> _open;
    private readonly Func<GameCardViewModel, Task> _choose;
    private readonly GameRegionsViewModel? _regions;
    private string _statusRu;
    private string? _executablePath;
    private bool _isRunning;
    private bool _isBusy;

    public GameCardViewModel(
        KnownGame game,
        string? executablePath,
        Func<GameCardViewModel, Task> open,
        Func<GameCardViewModel, Task> choose,
        GameRegionsViewModel? regions = null)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _choose = choose ?? throw new ArgumentNullException(nameof(choose));
        _regions = regions;
        _executablePath = executablePath;

        OpenCommand = new AsyncRelayCommand(() => _open(this), () => !IsBusy);
        ChoosePathCommand = new AsyncRelayCommand(() => _choose(this), () => !IsBusy);

        _statusRu = executablePath is null
            ? "Не найдена на диске — укажите .exe, и программа запомнит путь."
            : $"Найдена: {executablePath}";
    }

    public KnownGame Game { get; private set; }

    /// <summary>Своя игра человека: её можно удалить и переименовать. Встроенные — нельзя.</summary>
    public bool IsCustom => Game.IsCustom;

    public string TitleRu => Game.TitleRu;

    internal void Rename(string titleRu)
    {
        Game = Game with { TitleRu = titleRu };
        OnPropertyChanged(nameof(TitleRu), nameof(Game), nameof(IsCustom));
    }

    public string ProcessName => Game.ProcessName;

    public string AntiCheatRu => $"{Game.AntiCheatName}: {AnticheatText(Game.AntiCheat)}";

    /// <summary>
    /// Задача D: где серверы этой игры. Данные — из GameRouteCatalog, для игры вне справочника —
    /// честное «неизвестно». «Пинга до региона» здесь нет: измеряется только выданный сервер.
    /// </summary>
    public string RegionsHeadingRu => GameRegionsViewModel.HeadingRu;

    public string RegionsLineRu => _regions?.RegionsLineRu(ProcessName) ?? GameRegionsViewModel.UnknownRegionsRu;

    public string PortsLineRu => _regions?.PortsLineRu(ProcessName) ?? GameRegionsViewModel.UnpublishedPortsRu;

    /// <summary>Что игра значит для сетевого слоя: маршрут и QoS разрешены всем, фильтр — нет.</summary>
    public string NoteRu => string.IsNullOrWhiteSpace(Game.NoteRu)
        ? "Маршрут и приоритет пакетов применяются; в память игры программа не лезет."
        : Game.NoteRu;

    public string? ExecutablePath
    {
        get => _executablePath;
        private set
        {
            if (Set(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(PathRu), nameof(HasPath));
            }
        }
    }

    public bool HasPath => !string.IsNullOrWhiteSpace(ExecutablePath);

    public string PathRu => ExecutablePath ?? "путь не указан";

    public string StatusRu
    {
        get => _statusRu;
        set => Set(ref _statusRu, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(OpenButtonRu));
            }
        }
    }

    public string OpenButtonRu => IsRunning ? "Открыть ещё раз" : "Открыть";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
                ChoosePathCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand ChoosePathCommand { get; }

    internal void SetPath(string? path)
    {
        ExecutablePath = path;
        if (path is not null)
        {
            StatusRu = $"Найдена: {path}";
        }
    }

    private static string AnticheatText(AntiCheatStatus status) => status switch
    {
        AntiCheatStatus.VerifiedSafe => "маршрут и QoS — проверено",
        AntiCheatStatus.KnownKernelConflict => "только маршрут и QoS, без фильтра пакетов",
        _ => "маршрут и QoS — можно, совместимость не проверялась",
    };
}

/// <summary>
/// Раздел «Игры»: тот самый «открыть игру», ради которого такие программы и ставят.
///
/// Как это работает и чего здесь нарочно нет:
///   • список — из каталога известных игр, а не «всё, что найдено на диске»: случайный .exe мы игрой не называем;
///   • путь берётся только с диска (поиск по библиотекам Steam и обычным папкам) или указывается человеком;
///   • «Открыть» сначала запускает игру, потом просит движок начать оптимизацию — и честно
///     показывает, что ответил движок, даже если это «не подключён»;
///   • если игра не найдена, программа не делает вид, что запустила: предлагает указать путь.
/// </summary>
public sealed class GameLauncherViewModel : ObservableObject
{
    private readonly IGameExecutableProbe _probe;
    private readonly IGameStarter _starter;
    private readonly IExecutablePicker _picker;
    private readonly IAppSettingsStore _store;
    private readonly MainViewModel _main;
    private readonly System.Collections.ObjectModel.ObservableCollection<GameCardViewModel> _cards;
    private string _statusRu = "Список игр готов. Нажмите «Открыть» — программа запустит игру и предложит оптимизацию.";

    public GameLauncherViewModel(
        IGameExecutableProbe probe,
        IGameStarter starter,
        IExecutablePicker picker,
        IAppSettingsStore store,
        MainViewModel main,
        IReadOnlyList<KnownGame>? games = null,
        GameRegionsViewModel? regions = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _main = main ?? throw new ArgumentNullException(nameof(main));
        Regions = regions ?? new GameRegionsViewModel();

        AppSettings loaded = store.Load();
        IReadOnlyList<KnownGame> catalog = games ?? GameProcessCatalog.DefaultGames;

        // Свои игры человека лежат в тех же настройках (задача A). Встроенный список ими не
        // заменяется: свои добавляются после встроенных и помечены IsCustom.
        if (games is null && loaded.CustomGames is { Count: > 0 })
        {
            catalog = GameProcessCatalog.WithCustomGames(loaded.CustomGames).All;
        }

        IReadOnlyDictionary<string, string> remembered = loaded.GamePaths
            ?? new Dictionary<string, string>();

        _cards = new System.Collections.ObjectModel.ObservableCollection<GameCardViewModel>(catalog
            .Select(game => new GameCardViewModel(
                game,
                remembered.TryGetValue(game.ProcessName, out string? path) && File.Exists(path) ? path : null,
                OpenAsync,
                ChoosePathAsync,
                Regions)));

        FindGamesCommand = new AsyncRelayCommand(FindInstalledAsync);
        AddGameCommand = new AsyncRelayCommand(AddGameFromPickerAsync);
    }

    public IReadOnlyList<GameCardViewModel> Cards => _cards;

    /// <summary>Справочник регионов и портов (задача D): один на окно, данные из GameRouteCatalog.</summary>
    public GameRegionsViewModel Regions { get; }

    public AsyncRelayCommand FindGamesCommand { get; }

    /// <summary>«Добавить игру»: выбрать .exe, которого нет в списке известных.</summary>
    public AsyncRelayCommand AddGameCommand { get; }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>Сколько игр нашлось на диске — по этому числу видно, что поиск вообще работал.</summary>
    public string SummaryRu
    {
        get
        {
            int found = _cards.Count(card => card.HasPath);
            return found == 0
                ? $"На диске не найдено ни одной из {_cards.Count} известных игр. Это не ошибка: игру можно указать вручную."
                : $"Найдено игр на диске: {found} из {_cards.Count}.";
        }
    }

    /// <summary>Искать установленные игры. Медленно (диск), поэтому отдельной задачей и с понятным статусом.</summary>
    public async Task FindInstalledAsync()
    {
        StatusRu = "Ищу игры в библиотеках Steam и обычных папках…";

        // Общий процесс (javaw.exe) искать на диске бессмысленно: найденный чужой javaw.exe — не игра,
        // и кнопка «Открыть» запустила бы его без аргументов. Такую игру человек открывает лаунчером сам.
        IReadOnlyCollection<string> names = _cards
            .Where(card => !card.Game.RequiresPortMatch)
            .Select(card => card.ProcessName)
            .ToArray();

        try
        {
            IReadOnlyDictionary<string, string> found = await Task
                .Run(() => _probe.FindInstalled(names))
                .ConfigureAwait(true);

            foreach (GameCardViewModel card in _cards)
            {
                if (found.TryGetValue(card.ProcessName, out string? path))
                {
                    card.SetPath(path);
                }
            }

            StatusRu = SummaryRu;
            OnPropertyChanged(nameof(SummaryRu));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusRu = $"Поиск игр не получился: {ex.Message}. Укажите .exe вручную — это работает всегда.";
        }
    }

    /// <summary>«Открыть»: запустить игру и попросить движок начать оптимизацию.</summary>
    public async Task OpenAsync(GameCardViewModel card)
    {
        if (!card.HasPath)
        {
            await ChoosePathAsync(card).ConfigureAwait(true);
            if (!card.HasPath)
            {
                card.StatusRu = "Без пути запускать нечего: нажмите «Указать .exe» и выберите файл игры.";
                return;
            }
        }

        card.IsBusy = true;
        try
        {
            if (!_starter.TryStart(card.ExecutablePath!, out string error))
            {
                card.StatusRu = $"Запустить не удалось: {error}";
                StatusRu = card.StatusRu;
                return;
            }

            card.IsRunning = true;
            card.StatusRu = $"{card.TitleRu} запущена. Прошу движок начать оптимизацию…";
            StatusRu = card.StatusRu;

            // Оптимизация — после запуска: до этого движку просто нечего искать в таблице процессов.
            await _main.StartAsync().ConfigureAwait(true);

            card.StatusRu = $"{card.TitleRu} запущена. Оптимизация: {_main.StatusRu}";
            StatusRu = card.StatusRu;
        }
        finally
        {
            card.IsBusy = false;
        }
    }

    /// <summary>Указать .exe вручную и запомнить путь — при следующем запуске искать не придётся.</summary>
    public async Task ChoosePathAsync(GameCardViewModel card)
    {
        string? path = _picker.Pick(card.TitleRu);
        if (string.IsNullOrWhiteSpace(path))
        {
            card.StatusRu = "Путь не выбран — игра осталась ненайденной.";
            return;
        }

        card.SetPath(path);
        StatusRu = SavePath(card) ?? card.StatusRu;
        OnPropertyChanged(nameof(SummaryRu));
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Выбрать .exe через диалог и добавить своей игрой; название — по имени файла, переименовать можно в карточке.</summary>
    public async Task AddGameFromPickerAsync()
    {
        string? path = _picker.Pick("Выберите .exe своей игры");
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusRu = "Игра не добавлена: файл не выбран.";
            return;
        }

        string title = Path.GetFileNameWithoutExtension(path.Trim());
        await AddCustomGameAsync(path, title).ConfigureAwait(true);
    }

    /// <summary>
    /// Добавить свою игру. Валидация — общая с каталогом: файл существует, это .exe, название
    /// не пустое, дублей и служебных процессов нет. Отказ пишется в статус дословно.
    /// </summary>
    public Task AddCustomGameAsync(string? executablePath, string? titleRu)
    {
        var existing = _cards.Select(card => card.ProcessName).ToArray();
        if (!CustomGameValidation.TryCreate(executablePath, titleRu, File.Exists, existing, out CustomGameEntry? entry, out string errorRu))
        {
            StatusRu = $"Игра не добавлена: {errorRu}";
            return Task.CompletedTask;
        }

        KnownGame game = CustomGameValidation.ToKnownGame(entry!);
        var card = new GameCardViewModel(game, executablePath!.Trim(), OpenAsync, ChoosePathAsync);
        _cards.Add(card);
        SaveCustomGames(extraPath: (game.ProcessName, executablePath.Trim()));
        StatusRu = $"Своя игра «{game.TitleRu}» добавлена. Анти-чит у неё неизвестен — программа этого не скрывает. " +
            "Служба подхватит её при следующем запуске службы.";
        OnPropertyChanged(nameof(SummaryRu));
        return Task.CompletedTask;
    }

    /// <summary>Удалить свою игру. Встроенную удалить нельзя — и это не кнопка «не работает», а отказ с причиной.</summary>
    public bool RemoveCustomGame(GameCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.IsCustom)
        {
            StatusRu = $"«{card.TitleRu}» — встроенная игра из списка известных: её удалить нельзя. Свои игры удаляются из их карточек.";
            return false;
        }

        _cards.Remove(card);
        SaveCustomGames();
        StatusRu = $"Своя игра «{card.TitleRu}» удалена. Теперь этот процесс снова «кандидат, а не игра» — " +
            "оптимизация для него не запустится, пока игру не добавят снова.";
        OnPropertyChanged(nameof(SummaryRu));
        return true;
    }

    /// <summary>Переименовать свою игру. Встроенные названия не меняются.</summary>
    public bool RenameCustomGame(GameCardViewModel card, string? newTitleRu)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.IsCustom)
        {
            StatusRu = $"«{card.TitleRu}» — встроенная игра: её название не меняется. Переименовываются только свои игры.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(newTitleRu))
        {
            StatusRu = "Название не изменено: новое название пустое.";
            return false;
        }

        card.Rename(newTitleRu.Trim());
        SaveCustomGames();
        StatusRu = $"Своя игра переименована в «{card.TitleRu}».";
        return true;
    }

    private void SaveCustomGames((string ProcessName, string Path)? extraPath = null)
    {
        try
        {
            AppSettings current = _store.Load();
            var paths = current.GamePaths is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(current.GamePaths);
            if (extraPath is { } extra)
            {
                paths[extra.ProcessName] = extra.Path;
            }

            var custom = _cards
                .Where(card => card.IsCustom)
                .Select(card => new CustomGameEntry(card.ProcessName, card.TitleRu))
                .ToList();

            // Пути удалённых своих игр не держим: удалённая игра не должна воскресать по пути.
            var customNames = new HashSet<string>(custom.Select(game => game.ProcessName), StringComparer.OrdinalIgnoreCase);
            foreach (string stale in paths.Keys.Where(name =>
                !GameProcessCatalog.DefaultGames.Any(game => string.Equals(game.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                && !customNames.Contains(name)).ToList())
            {
                paths.Remove(stale);
            }

            _store.Save(current with { CustomGames = custom, GamePaths = paths });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusRu = $"Изменение списка игр не записалось в настройки: {ex.Message}. В этом запуске список работает, после перезапуска окна вернётся прежний.";
        }
    }

    private string? SavePath(GameCardViewModel card)
    {
        try
        {
            AppSettings current = _store.Load();
            var paths = current.GamePaths is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(current.GamePaths);
            paths[card.ProcessName] = card.ExecutablePath ?? string.Empty;

            _store.Save(current with { GamePaths = paths });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Путь выбран, но в настройки не записался: {ex.Message}. В этом запуске он работает.";
        }
    }
}
