using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PingForge.App.ViewModels;

namespace PingForge.App;

/// <summary>
/// Окно программы: шапка, список разделов, содержимое выбранного раздела.
///
/// Переключение разделов сделано на переключателях (как и в витрине): выбранный пункт виден,
/// содержимое меняется мгновенно, и никаких обработчиков, которые могут «не сработать».
/// </summary>
public partial class MainWindow : Window
{
    private readonly App _app;

    public MainWindow(App app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        InitializeComponent();

        DataContext = new ShellViewModel(app);

        Closing += (_, e) => _app.OnWindowClosing(e);

        // Первый пункт меню отмечен прямо в разметке (IsChecked="True"), поэтому событие
        // «выбран раздел» приходит ещё во время разбора XAML — когда страниц (Page*) ещё нет.
        // Именно на этом окно падало с NullReferenceException: на Linux это не видно, а на
        // Windows процесс умирал молча, без окна и без единого слова. Применяем раздел после
        // загрузки окна, а до тех пор обработчик просто ничего не делает.
        Loaded += (_, _) => ApplySection();
    }

    private void NavChanged(object sender, RoutedEventArgs e) => ApplySection();

    /// <summary>
    /// Показать выбранный раздел. Вызывается и обработчиком меню, и после загрузки окна:
    /// одна и та же логика, один и тот же результат — независимо от того, что произошло раньше.
    /// </summary>
    private void ApplySection()
    {
        if (!IsSectionReady())
        {
            return;   // разбор разметки ещё идёт — применим на Loaded
        }

        PageOptimize.Visibility = NavOptimize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTweaks.Visibility = NavTweaks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageJournal.Visibility = NavJournal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // Журнал читается при входе в раздел, а не по таймеру: незачем дёргать службу без надобности.
        if (NavJournal.IsChecked == true && !_app.Journal.IsLoaded)
        {
            _ = _app.Journal.RefreshAsync();
        }
    }

    /// <summary>Готовы ли поля окна. Недосозданные поля — не ошибка: значит разметка ещё разбирается.</summary>
    private bool IsSectionReady() =>
        PageOptimize is not null && PageTweaks is not null && PageJournal is not null &&
        PageSettings is not null && PageAbout is not null &&
        NavOptimize is not null && NavTweaks is not null && NavJournal is not null &&
        NavSettings is not null && NavAbout is not null;

    private void SupportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://dalink.to/doxaiz") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось открыть ссылку: {ex.Message}\n\nАдрес: https://dalink.to/doxaiz",
                "Custom Tweak By Doxaiz — поддержка автора",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

/// <summary>
/// Модель окна: то, что нужно шапке и подвалу. Отдельно от разделов, чтобы окно не знало лишнего.
/// </summary>
public sealed class ShellViewModel
{
    public ShellViewModel(App app)
    {
        MainScreen = app.MainScreen;
        Tweaks = app.Tweaks;
        Journal = app.Journal;
        Settings = app.Settings;

        GameLineRu = $"Твиков в каталоге: {app.Tweaks.Rows.Count} · версия {GetType().Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
    }

    public MainViewModel MainScreen { get; }

    public TweaksViewModel Tweaks { get; }

    public JournalViewModel Journal { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Строка в шапке: число твиков берётся из каталога, а не пишется руками.</summary>
    public string GameLineRu { get; }
}
