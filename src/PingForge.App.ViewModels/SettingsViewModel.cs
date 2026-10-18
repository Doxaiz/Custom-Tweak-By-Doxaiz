using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Раздел «Настройки»: профиль потребления, путь к файлу настроек и поведение окна.
///
/// По прямому требованию конфиг живёт здесь, в «Настройках», а не отдельным блоком в боковой
/// колонке: пользователь должен видеть, где лежит файл, и не искать его по диску.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private readonly IAppSettingsStore _store;
    private readonly IUserDialogs _dialogs;
    private PerfProfile _profile;
    private bool _offerRestorePoint;
    private bool _closeToTray;
    private string _statusRu;

    public SettingsViewModel(EngineClient engine, IAppSettingsStore store, IUserDialogs dialogs, RoutingSettingsViewModel? routing = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(dialogs));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        AppSettings settings = _store.Load();
        _profile = EngineNames.TryParseProfile(settings.PerfProfile, out PerfProfile parsed) ? parsed : PerfProfile.Balanced;
        _offerRestorePoint = settings.OfferRestorePoint;
        _closeToTray = settings.CloseToTray;
        _statusRu = $"Файл настроек: {_store.SettingsFilePath}";

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync);
        ReopenSetupCommand = new RelayCommand(() => Setup?.Reopen());

        // Блок «Сервер маршрутизации» (задача B) живёт здесь, в «Настройках»: узлы, поиск шлюзов,
        // измерение путей и выбор узла по умолчанию. Двойник передаётся в тестах — в бою свой.
        Routing = routing ?? new RoutingSettingsViewModel(engine, store, dialogs);
    }

    /// <summary>Узлы маршрутизации: список, поиск шлюзов, замер путей. Ничего не меняет сама по себе.</summary>
    public RoutingSettingsViewModel Routing { get; }

    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// «Пройти мастер заново». Мастер показывается один раз, но выбор мог быть случайным —
    /// поэтому вернуться к нему можно в любой момент, а не переустановкой программы.
    /// </summary>
    public RelayCommand ReopenSetupCommand { get; }

    /// <summary>Мастер первого запуска. Появляется здесь, потому что окно решает, когда его показать.</summary>
    public OnboardingViewModel? Setup { get; private set; }

    public void AttachSetup(OnboardingViewModel setup) => Setup = setup;

    public AsyncRelayCommand ApplyProfileCommand { get; }

    /// <summary>Где лежит файл настроек — показывается прямо в окне настроек.</summary>
    public string SettingsFilePath => _store.SettingsFilePath;

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>Профиль потребления: экономно / обычно / максимум реакции.</summary>
    public PerfProfile Profile
    {
        get => _profile;
        set
        {
            if (Set(ref _profile, value))
            {
                OnPropertyChanged(nameof(ProfileNoteRu), nameof(ProfileWire));
            }
        }
    }

    public string ProfileWire => EngineNames.ProfileWireName(Profile);

    /// <summary>
    /// Что профиль означает на практике. Считается теми же правилами, что и в движке
    /// (<see cref="ResourceBudgetOptions"/>), поэтому подпись не может разойтись с настоящими интервалами.
    /// </summary>
    public string ProfileNoteRu
    {
        get
        {
            var budget = new ResourceBudgetOptions(Profile);
            double interval = budget.EffectiveMonitorInterval(TimeSpan.FromSeconds(2)).TotalSeconds;
            int perMinute = budget.MeasurementsPerMinute(TimeSpan.FromSeconds(2), 1);

            return $"Контроль каждые {interval:0.#} с, до {perMinute} проб в минуту. " +
                (Profile == PerfProfile.Economy
                    ? "На батарее оптимизация встаёт на паузу — для слабых ноутбуков."
                    : Profile == PerfProfile.MaxPerformance
                        ? "Самый частый контроль: заметнее нагрузка, нужен для турнирных матчей."
                        : "Обычный режим: пауза только когда игра свёрнута.");
        }
    }

    /// <summary>Предлагать точку восстановления Windows перед изменениями.</summary>
    public bool OfferRestorePoint
    {
        get => _offerRestorePoint;
        set => Set(ref _offerRestorePoint, value);
    }

    /// <summary>Закрывать окно в трей, а не выходить.</summary>
    public bool CloseToTray
    {
        get => _closeToTray;
        set => Set(ref _closeToTray, value);
    }

    /// <summary>Сохранить настройки в файл и честно сказать, сохранилось ли.</summary>
    public async Task SaveAsync()
    {
        try
        {
            // Читаем файл заново: в нём могли появиться поля, которые меняет не эта страница
            // (пройденный мастер первого запуска, запомненные пути к играм). «Собрать с нуля»
            // означало бы молча стереть их при первом же сохранении настроек.
            AppSettings settings = _store.Load() with
            {
                PerfProfile = EngineNames.ProfileWireName(Profile),
                OfferRestorePoint = OfferRestorePoint,
                StartMinimized = false,
                CloseToTray = CloseToTray,
                JournalRowsShown = 20,
            };

            _store.Save(settings);
            AppSettings actual = _store.Load();

            StatusRu = actual == settings
                ? $"Настройки сохранены: {_store.SettingsFilePath}"
                : $"Настройки не сохранились: в файле осталось прежнее. Проверьте права на папку {Path.GetDirectoryName(_store.SettingsFilePath)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusRu = $"Файл настроек не записался: {ex.Message}. Изменения останутся только до перезапуска.";
        }
    }

    /// <summary>Отправить профиль потребления службе — именно она считает интервалы замеров.</summary>
    public async Task ApplyProfileAsync()
    {
        try
        {
            EngineResponse response = await _engine.SendAsync(
                EngineCommandKind.PerfProfile,
                [("profile", ProfileWire)]).ConfigureAwait(true);

            StatusRu = response.MessageRu;
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
    }
}
