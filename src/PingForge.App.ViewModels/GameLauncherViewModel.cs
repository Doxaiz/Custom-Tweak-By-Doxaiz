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
    private string _statusRu;
    private string? _executablePath;
    private bool _isRunning;
    private bool _isBusy;

    public GameCardViewModel(
        KnownGame game,
        string? executablePath,
        Func<GameCardViewModel, Task> open,
        Func<GameCardViewModel, Task> choose)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _choose = choose ?? throw new ArgumentNullException(nameof(choose));
        _executablePath = executablePath;

        OpenCommand = new AsyncRelayCommand(() => _open(this), () => !IsBusy);
        ChoosePathCommand = new AsyncRelayCommand(() => _choose(this), () => !IsBusy);

        _statusRu = executablePath is null
            ? "Не найдена на диске — укажите .exe, и программа запомнит путь."
            : $"Найдена: {executablePath}";
    }

    public KnownGame Game { get; }

    public string TitleRu => Game.TitleRu;

    public string ProcessName => Game.ProcessName;

    public string AntiCheatRu => $"{Game.AntiCheatName}: {AnticheatText(Game.AntiCheat)}";

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
    private readonly List<GameCardViewModel> _cards;
    private string _statusRu = "Список игр готов. Нажмите «Открыть» — программа запустит игру и предложит оптимизацию.";

    public GameLauncherViewModel(
        IGameExecutableProbe probe,
        IGameStarter starter,
        IExecutablePicker picker,
        IAppSettingsStore store,
        MainViewModel main,
        IReadOnlyList<KnownGame>? games = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _main = main ?? throw new ArgumentNullException(nameof(main));

        IReadOnlyList<KnownGame> catalog = games ?? GameProcessCatalog.DefaultGames;
        IReadOnlyDictionary<string, string> remembered = store.Load().GamePaths
            ?? new Dictionary<string, string>();

        _cards = catalog
            .Select(game => new GameCardViewModel(
                game,
                remembered.TryGetValue(game.ProcessName, out string? path) && File.Exists(path) ? path : null,
                OpenAsync,
                ChoosePathAsync))
            .ToList();

        FindGamesCommand = new AsyncRelayCommand(FindInstalledAsync);
    }

    public IReadOnlyList<GameCardViewModel> Cards => _cards;

    public AsyncRelayCommand FindGamesCommand { get; }

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
        IReadOnlyCollection<string> names = _cards.Select(card => card.ProcessName).ToArray();

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
