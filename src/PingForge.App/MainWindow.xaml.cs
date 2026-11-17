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

        // Масштаб интерфейса: раскладка тянется LayoutTransform'ом из разметки, а минимальный размер
        // окна обязан расти вместе с ней — иначе при 150 % окно «не пускает» содержимое и правый
        // край обрезается. При 100 % это ровно 860×560, прежние значения.
        ApplyUiScale();
        if (DataContext is ShellViewModel shell)
        {
            shell.Settings.UiScale.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UiScaleViewModel.Scale))
                {
                    ApplyUiScale();
                    FitToScreen();
                }
            };
        }

        Closing += (_, e) => _app.OnWindowClosing(e);

        // Первый пункт меню отмечен прямо в разметке (IsChecked="True"), поэтому событие
        // «выбран раздел» приходит ещё во время разбора XAML — когда страниц (Page*) ещё нет.
        // Именно на этом окно падало с NullReferenceException: на Linux это не видно, а на
        // Windows процесс умирал молча, без окна и без единого слова. Применяем раздел после
        // загрузки окна, а до тех пор обработчик просто ничего не делает.
        Loaded += (_, _) =>
        {
            FitToScreen();
            ApplySection();
        };
    }

    /// <summary>
    /// Минимальный размер окна под текущий масштаб. Ширина и высота содержимого при 150 %
    /// в полтора раза больше, поэтому и минимум окна в полтора раза больше: иначе окно можно
    /// ужать так, что разделы и кнопки окажутся за краем.
    /// </summary>
    private void ApplyUiScale()
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        double scale = shell.Settings.UiScale.Scale;
        Rect work = SystemParameters.WorkArea;

        // На маленьком экране 150 % не должны выталкивать окно за край: минимум растёт вместе
        // с содержимым, но не больше рабочей области. Внутри есть прокрутка, поэтому ужать окно
        // до экрана безопаснее, чем потерять правый и нижний край.
        MinWidth = Math.Min(
            UiScaleViewModel.MinWidthAt100 * scale,
            Math.Max(UiScaleViewModel.MinWidthAt100, work.Width - 16));
        MinHeight = Math.Min(
            UiScaleViewModel.MinHeightAt100 * scale,
            Math.Max(UiScaleViewModel.MinHeightAt100, work.Height - 16));

        Width = Math.Max(Width, MinWidth);
        Height = Math.Max(Height, MinHeight);
    }

    /// <summary>
    /// Окно не должно быть больше экрана: на 1024×768 (и на многих ноутбуках 1366×768) окно
    /// 1180×820 вылезало за края — часть разделов и кнопок оказывалась вне видимой области.
    /// Само по себе это не мешает работать, но выглядит как «программа сломана».
    /// </summary>
    private void FitToScreen()
    {
        Rect work = SystemParameters.WorkArea;

        if (Width > work.Width)
        {
            Width = Math.Max(MinWidth, work.Width - 16);
        }

        if (Height > work.Height)
        {
            Height = Math.Max(MinHeight, work.Height - 16);
        }

        if (Left < work.Left)
        {
            Left = work.Left;
        }

        if (Top < work.Top)
        {
            Top = work.Top;
        }

        if (Left + Width > work.Right)
        {
            Left = Math.Max(work.Left, work.Right - Width);
        }

        if (Top + Height > work.Bottom)
        {
            Top = Math.Max(work.Top, work.Bottom - Height);
        }
    }

    private void NavChanged(object sender, RoutedEventArgs e) => ApplySection();

    /// <summary>
    /// Задача F: перейти в раздел «Твики» из кода — так работает кнопка «Показать в твиках»
    /// блока проверки настроек. Отмечаем пункт меню: <see cref="NavChanged"/> сам применит раздел,
    /// то есть путь ровно тот же, что и клик человека по меню.
    /// </summary>
    public void ShowTweaksSection() => NavTweaks.IsChecked = true;

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
        PageGames.Visibility = NavGames.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// Все разделы окна — для самопроверки: ей нужно показать и проверить каждый, даже свёрнутый.
    /// </summary>
    internal IReadOnlyList<UIElement> SectionsForCheck =>
        [PageOptimize, PageGames, PageTweaks, PageJournal, PageSettings, PageAbout];

    /// <summary>Готовы ли поля окна. Недосозданные поля — не ошибка: значит разметка ещё разбирается.</summary>
    private bool IsSectionReady() =>
        PageOptimize is not null && PageGames is not null && PageTweaks is not null &&
        PageJournal is not null && PageSettings is not null && PageAbout is not null &&
        NavOptimize is not null && NavGames is not null && NavTweaks is not null &&
        NavJournal is not null && NavSettings is not null && NavAbout is not null;

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
        Games = app.Games;
        Setup = app.Setup;
        Tweaks = app.Tweaks;
        Journal = app.Journal;
        Settings = app.Settings;

        GameLineRu = $"Твиков в каталоге: {app.Tweaks.Rows.Count} · версия {GetType().Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
    }

    public MainViewModel MainScreen { get; }

    /// <summary>Раздел «Игры»: открыть игру и начать оптимизацию.</summary>
    public GameLauncherViewModel Games { get; }

    /// <summary>Мастер первого запуска: показывается поверх окна, пока его не пройдут или не отложат.</summary>
    public OnboardingViewModel Setup { get; }

    public TweaksViewModel Tweaks { get; }

    public JournalViewModel Journal { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Строка в шапке: число твиков берётся из каталога, а не пишется руками.</summary>
    public string GameLineRu { get; }
}
