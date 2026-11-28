using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PingForge.App.ViewModels;

namespace PingForge.App.Views;

/// <summary>Раздел «О программе»: что делает, чего не делает и где поддержать автора.</summary>
public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();

    /// <summary>
    /// «Открыть страницу релиза» (задача I): обычный браузер человека. Адрес берётся из модели —
    /// а она хранит только проверенный https://github.com/…; сама страница ничего не скачивает.
    /// Если браузер не открылся, отказ пишется в статус проверки, а не модальным окном: адрес
    /// остаётся на экране, и его можно открыть вручную.
    /// </summary>
    private void OpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }

        UpdateCheckViewModel updates = main.Updates;
        string? url = updates.ReleasePageUrl;
        if (url is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            updates.ReportOpenFailed(url);
        }
    }
}
