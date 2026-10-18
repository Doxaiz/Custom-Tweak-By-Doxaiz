using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using PingForge.App.ViewModels;
using PingForge.Tweaks;

namespace PingForge.App;

/// <summary>
/// Сборка приложения. Здесь и только здесь видно, из чего оно состоит: канал к службе, модели
/// представления, окно, значок в трее. Никакой логики — она в моделях, поэтому и проверяется тестами.
///
/// При запуске программа ничего не делает: канал открывается по первому запросу, оптимизация
/// не стартует, твики не применяются. Единственная фоновая работа — редкий опрос состояния.
///
/// Запуск теперь ещё и рассказывает о себе: каждая ступень пишется в журнал (см. <see cref="StartupLog"/>),
/// а любая необработанная ошибка попадает и в журнал, и в окно. Раньше сбой на старте выглядел
/// как «нажал — и ничего не произошло», и это была не жалоба пользователя, а дырка в программе.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private TrayPresence? _tray;
    private SplashWindow? _splash;
    private SingleInstance? _instance;
    private DateTime _splashShownUtc;

    /// <summary>Режим самопроверки: проходит весь путь старта без показа окна и человека.</summary>
    public static bool SelfCheckMode { get; private set; }

    /// <summary>Режим без диалогов: ни одного окна-сообщения, всё в журнал.</summary>
    public static bool QuietMode { get; private set; }

    /// <summary>Каталог читается из той же сборки, что применяет служба: описания и вердикты совпадают.</summary>
    public TweakCatalog Catalog { get; private set; } = null!;

    /// <summary>
    /// Порты платформы Windows для анализа (задача F): советники задержки ввода и проверки настроек
    /// читают реальные значения — реестр, PnP-устройства, свойства сетевых карт. Создаются здесь,
    /// а не внутри моделей: их состав видно в одном месте, и их можно освободить при выходе.
    /// </summary>
    private PingForge.Tweaks.Windows.WindowsTweakPlatform? _analysisPlatform;

    public EngineClient Engine { get; private set; } = null!;

    public MainViewModel MainScreen { get; private set; } = null!;

    public TweaksViewModel Tweaks { get; private set; } = null!;

    public JournalViewModel Journal { get; private set; } = null!;

    public SettingsViewModel Settings { get; private set; } = null!;

    /// <summary>Раздел «Игры»: запуск игры и сразу оптимизация под неё.</summary>
    public GameLauncherViewModel Games { get; private set; } = null!;

    /// <summary>Мастер первого запуска: три вопроса и совет — как в BoosterX, но без изменений без согласия.</summary>
    public OnboardingViewModel Setup { get; private set; } = null!;

    /// <summary>Окно — для самопроверки: ей нужно построить разметку, но не показывать её человеку.</summary>
    internal MainWindow? Shell => _window;

    /// <summary>Значок в трее — для самопроверки.</summary>
    internal TrayPresence? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SelfCheckMode = HasArgument(e.Args, "--self-check");
        QuietMode = HasArgument(e.Args, "--quiet");
        CrashGuard.Silent = SelfCheckMode || QuietMode;

        if (SelfCheckMode)
        {
            StartupLog.EnableConsoleOutput();
            SelfCheck.ArmWatchdog(120);
        }
        else if (!QuietMode)
        {
            // Один экземпляр: повторный запуск поднимает уже открытое окно, а не открывает второе.
            _instance = new SingleInstance();
            if (!_instance.IsFirst)
            {
                _instance.SignalExisting();
                Shutdown(0);
                return;
            }
        }

        StartupLog.WriteHeader(
            GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            SelfCheckMode,
            QuietMode,
            isAdministrator: IsAdministrator());

        // Перехватчики ставятся до любой работы: иначе ошибка на старте уносит процесс молча.
        CrashGuard.Install(this);

        ShowSplash();

        try
        {
            Build();
            StartupLog.Note("Сборка приложения закончена");
        }
        catch (Exception ex)
        {
            HideSplash();
            StartupLog.Problem("Программа не смогла запуститься", ex);
            CrashGuard.Report("Программа не смогла запуститься", ex);
            Environment.Exit(30);
            return;
        }

        if (SelfCheckMode)
        {
            Environment.Exit(SelfCheck.Run(this));
        }
    }

    /// <summary>
    /// Собственно сборка: каталог, настройки, модели, окно, трей. Окно показывается сразу после
    /// создания — раньше трея: если трей почему-то не заведётся, человек всё равно увидит программу.
    /// </summary>
    private void Build()
    {
        Step("Собираю каталог твиков");
        // Задача F: каталог получает настоящие порты Windows. Витрина (строки, пресеты) от этого
        // не меняется — она читает только описания. Зато советники анализа теперь снимают
        // фактические значения, а не отвечают «не поддерживается».
        var analysisPlatform = new PingForge.Tweaks.Windows.WindowsTweakPlatform();
        _analysisPlatform = analysisPlatform;
        Catalog = new TweakCatalog(analysisPlatform.ToCatalogPorts());

        Step("Читаю настройки");
        var dialogs = new WpfDialogs();
        var store = new JsonFileSettingsStore(DefaultSettingsPath());
        AppSettings settings = store.Load();

        // Если файл настроек повреждён — говорим об этом сразу, а не делаем вид, что так и было.
        if (store.LastLoadProblemRu is { } problem)
        {
            Notify("Custom Tweak By Doxaiz — настройки", problem, MessageBoxImage.Warning);
        }

        Step("Собираю модели представления");
        Engine = new EngineClient(new NamedPipeEngineChannel());

        // pollStatusWhileOptimizing: пока оптимизация идёт, окно редко (раз в 3 с) спрашивает status —
        // команда только читает. Так график сети живёт без собственных зондов: точки приходят
        // из тех же замеров, что уже делает оптимизация.
        MainScreen = new MainViewModel(Engine, dialogs, runsAsAdministrator: IsAdministrator(), pollStatusWhileOptimizing: true);
        Tweaks = new TweaksViewModel(
            Engine,
            dialogs,
            TweakRowFactory.BuildAll(Catalog),
            TweakRowFactory.BuildPresets(Catalog));

        // Карточка «Задержка ввода» (задача F): советник из PingForge.Analysis, подключённый к окну.
        Tweaks.AttachInputLatency(new InputLatencyViewModel(
            AnalysisWiring.BuildInputLatency(analysisPlatform, Catalog),
            Tweaks.ShowOnlyCodes));

        Journal = new JournalViewModel(Engine, settings.JournalRowsShown);

        // Блок «Проверка настроек» (задача F): разбор конфликтов и советы по твикам, только чтение.
        // Кнопка «Показать в твиках» фильтрует список и переключает раздел — тот же путь, что клик по меню.
        Settings = new SettingsViewModel(
            Engine,
            store,
            dialogs,
            check: new SettingsCheckViewModel(
                AnalysisWiring.BuildSettingsCheck(analysisPlatform, Catalog, () => MainScreen.Endpoint),
                ShowInTweaks));
        Games = new GameLauncherViewModel(
            new WindowsGameLocator(),
            new ProcessGameStarter(),
            new OpenFileExecutablePicker(),
            store,
            MainScreen);
        Setup = new OnboardingViewModel(store, Tweaks, Settings);
        Settings.AttachSetup(Setup);

        Step("Строю окно");
        _window = new MainWindow(this);

        if (SelfCheckMode)
        {
            // Окно строим, но не показываем: разметка уже разобрана. Трей — тоже строим,
            // иначе самопроверка не проверила бы его вовсе.
            _tray = BuildTray();
            return;
        }

        if (Setup.IsVisible)
        {
            StartupLog.Note("Первый запуск: показываю мастер настройки");
        }

        if (settings.StartMinimized)
        {
            StartupLog.Note("Настройка «запускать свёрнутым»: окно не показываю, будет шарик у значка");
            _tray = BuildTray();
            _tray?.ShowBalloon(
                "Программа запущена и ждёт",
                "Оптимизация не начата: нажмите «Оптимизировать соединение» в окне или в меню значка.");
            return;
        }

        _window.Show();
        HideSplash();

        _tray = BuildTray();
        StartListeningForSecondLaunch();
    }

    /// <summary>
    /// Ступень сборки: одна и та же строка идёт и в журнал запуска, и на заставку.
    /// Так человек видит, что программа не «зависла», а работает.
    /// </summary>
    private void Step(string text)
    {
        StartupLog.Step(text);
        _splash?.Stage(text);
    }

    /// <summary>Заставка. Её отсутствие не должно мешать запуску — поэтому всё в перехвате.</summary>
    private void ShowSplash()
    {
        if (SelfCheckMode || QuietMode)
        {
            return;
        }

        try
        {
            _splash = new SplashWindow();
            _splash.Show();
            _splashShownUtc = DateTime.UtcNow;
            StartupLog.Note("Показана заставка: программа открывается");
        }
        catch (Exception ex)
        {
            _splash = null;
            StartupLog.Problem("Заставка не показалась — работаю без неё", ex);
        }
    }

    /// <summary>
    /// Убрать заставку. Не раньше, чем через 700 мс: мгновенно мигнувшая заставка выглядит как сбой,
    /// а не как «программа открывается».
    /// </summary>
    private void HideSplash()
    {
        if (_splash is null)
        {
            return;
        }

        try
        {
            TimeSpan shown = DateTime.UtcNow - _splashShownUtc;
            if (shown < TimeSpan.FromMilliseconds(700))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(700) - shown);
            }

            _splash.Close();
        }
        catch (Exception ex)
        {
            StartupLog.Problem("Заставку не удалось закрыть", ex);
        }
        finally
        {
            _splash = null;
        }
    }

    /// <summary>Второй запуск поднимает это окно — так и должно быть, а не «открылось второе».</summary>
    private void StartListeningForSecondLaunch()
    {
        if (_instance is null || _window is null)
        {
            return;
        }

        _instance.ActivateRequested += () => Dispatcher.Invoke(() =>
        {
            try
            {
                _window.Show();
                if (_window.WindowState == WindowState.Minimized)
                {
                    _window.WindowState = WindowState.Normal;
                }

                _window.Activate();
                _window.Topmost = true;
                _window.Topmost = false;
                StartupLog.Note("Повторный запуск: поднял уже открытое окно");
            }
            catch (InvalidOperationException ex)
            {
                StartupLog.Problem("Не удалось поднять окно по повторному запуску", ex);
            }
        });

        _instance.Listen();
    }

    /// <summary>
    /// Значок в трее. Его отсутствие — не повод не запускаться: программа и без трея делает всё,
    /// что обещает. Поэтому здесь перехват, а не падение всего запуска.
    /// </summary>
    private TrayPresence? BuildTray()
    {
        try
        {
            Step("Ставлю значок в трее");
            return new TrayPresence(this, _window!);
        }
        catch (Exception ex)
        {
            StartupLog.Problem("Значок в трее не завёлся", ex);
            if (!CrashGuard.Silent)
            {
                MessageBox.Show(
                    $"Значок в трее не поставился: {ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                    "Программа работает и без него — окно открывается как обычно.",
                    "Custom Tweak By Doxaiz — значок в трее",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return null;
        }
    }

    /// <summary>
    /// Сообщение человеку. В режимах самопроверки и «без диалогов» окна не показываются: сборочная
    /// машина их не закроет, и проверка повисла бы на первом же предупреждении.
    /// </summary>
    private static void Notify(string titleRu, string messageRu, MessageBoxImage icon)
    {
        StartupLog.Warn($"{titleRu}: {messageRu}");

        if (CrashGuard.Silent)
        {
            return;
        }

        MessageBox.Show(messageRu, titleRu, MessageBoxButton.OK, icon);
    }

    /// <summary>Закрытие окна: либо выход, либо жизнь в трее — как выбрано в настройках.</summary>
    public void OnWindowClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Settings.CloseToTray)
        {
            e.Cancel = true;
            _window?.Hide();
            _tray?.ShowBalloon("Программа осталась в трее", "Оптимизация продолжает работать. Закрыть совсем — правой кнопкой по значку → «Выход».");
            return;
        }

        ExitApplication();
    }

    /// <summary>Путь к файлу настроек: рядом с базой службы, чтобы всё имущество программы лежало вместе.</summary>
    public static string DefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PingForge",
        "settings.json");

    public void ExitApplication()
    {
        StartupLog.Note("Выход по команде пользователя");
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// Задача F: кнопка «Показать в твиках» из блока проверки настроек. Сначала фильтр списка
    /// по кодам, затем переход в раздел «Твики» — тем же путём, что и клик по меню.
    /// </summary>
    private void ShowInTweaks(IReadOnlyList<string> codes)
    {
        Tweaks.ShowOnlyCodes(codes);
        Shell?.ShowTweaksSection();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _analysisPlatform?.Dispose();
        base.OnExit(e);
    }

    private static bool HasArgument(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
