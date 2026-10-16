using System.IO;
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
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private TrayPresence? _tray;

    /// <summary>Каталог читается из той же сборки, что применяет служба: описания и вердикты совпадают.</summary>
    public TweakCatalog Catalog { get; private set; } = null!;

    public EngineClient Engine { get; private set; } = null!;

    public MainViewModel MainScreen { get; private set; } = null!;

    public TweaksViewModel Tweaks { get; private set; } = null!;

    public JournalViewModel Journal { get; private set; } = null!;

    public SettingsViewModel Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Портов платформы нет намеренно: интерфейсу нужны описания твиков, а не живая система.
        // Всё, что трогает систему, делает служба — она под LocalSystem.
        Catalog = new TweakCatalog(new TweakPlatformPorts());

        var dialogs = new WpfDialogs();
        var store = new JsonFileSettingsStore(DefaultSettingsPath());
        AppSettings settings = store.Load();

        // Если файл настроек повреждён — говорим об этом сразу, а не делаем вид, что так и было.
        if (store.LastLoadProblemRu is { } problem)
        {
            MessageBox.Show(problem, "Custom Tweak By Doxaiz — настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Engine = new EngineClient(new NamedPipeEngineChannel());

        MainScreen = new MainViewModel(Engine, dialogs);
        Tweaks = new TweaksViewModel(
            Engine,
            dialogs,
            TweakRowFactory.BuildAll(Catalog),
            TweakRowFactory.BuildPresets(Catalog));
        Journal = new JournalViewModel(Engine, settings.JournalRowsShown);
        Settings = new SettingsViewModel(Engine, store, dialogs);

        _window = new MainWindow(this);
        _tray = new TrayPresence(this, _window);

        if (settings.StartMinimized)
        {
            _tray.ShowBalloon("Программа запущена и ждёт", "Оптимизация не начата: нажмите «Оптимизировать соединение» в окне или в меню значка.");
        }
        else
        {
            _window.Show();
        }
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
        _tray?.Dispose();
        Shutdown();
    }
}
