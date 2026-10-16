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
    }

    private void NavChanged(object sender, RoutedEventArgs e)
    {
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
