using System.IO;
using System.Security.Principal;
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

    /// <summary>Режим самопроверки: проходит весь путь старта без показа окна и человека.</summary>
    public static bool SelfCheckMode { get; private set; }

    /// <summary>Режим без диалогов: ни одного окна-сообщения, всё в журнал.</summary>
    public static bool QuietMode { get; private set; }

    /// <summary>Каталог читается из той же сборки, что применяет служба: описания и вердикты совпадают.</summary>
    public TweakCatalog Catalog { get; private set; } = null!;

    public EngineClient Engine { get; private set; } = null!;

    public MainViewModel MainScreen { get; private set; } = null!;

    public TweaksViewModel Tweaks { get; private set; } = null!;

    public JournalViewModel Journal { get; private set; } = null!;

    public SettingsViewModel Settings { get; private set; } = null!;

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

        StartupLog.WriteHeader(
            GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            SelfCheckMode,
            QuietMode,
            isAdministrator: IsAdministrator());

        // Перехватчики ставятся до любой работы: иначе ошибка на старте уносит процесс молча.
        CrashGuard.Install(this);

        try
        {
            Build();
            StartupLog.Note("Сборка приложения закончена");
        }
        catch (Exception ex)
        {
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
        StartupLog.Step("Собираю каталог твиков");
        Catalog = new TweakCatalog(new TweakPlatformPorts());

        StartupLog.Step("Читаю настройки");
        var dialogs = new WpfDialogs();
        var store = new JsonFileSettingsStore(DefaultSettingsPath());
        AppSettings settings = store.Load();

        // Если файл настроек повреждён — говорим об этом сразу, а не делаем вид, что так и было.
        if (store.LastLoadProblemRu is { } problem)
        {
            Notify("Custom Tweak By Doxaiz — настройки", problem, MessageBoxImage.Warning);
        }

        StartupLog.Step("Собираю модели представления");
        Engine = new EngineClient(new NamedPipeEngineChannel());

        MainScreen = new MainViewModel(Engine, dialogs);
        Tweaks = new TweaksViewModel(
            Engine,
            dialogs,
            TweakRowFactory.BuildAll(Catalog),
            TweakRowFactory.BuildPresets(Catalog));
        Journal = new JournalViewModel(Engine, settings.JournalRowsShown);
        Settings = new SettingsViewModel(Engine, store, dialogs);

        StartupLog.Step("Строю окно");
        _window = new MainWindow(this);

        if (SelfCheckMode)
        {
            // Окно строим, но не показываем: разметка уже разобрана. Трей — тоже строим,
            // иначе самопроверка не проверила бы его вовсе.
            _tray = BuildTray();
            return;
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

        _tray = BuildTray();
    }

    /// <summary>
    /// Значок в трее. Его отсутствие — не повод не запускаться: программа и без трея делает всё,
    /// что обещает. Поэтому здесь перехват, а не падение всего запуска.
    /// </summary>
    private TrayPresence? BuildTray()
    {
        try
        {
            StartupLog.Step("Ставлю значок в трее");
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
