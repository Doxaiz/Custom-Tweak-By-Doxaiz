using System.Windows;
using System.Windows.Controls;
using PingForge.App.ViewModels;
using PingForge.Orchestration.Realtime;

namespace PingForge.App.Views;

/// <summary>
/// Раздел «Настройки». Переключатели профиля ставятся по сохранённому значению и пишут обратно
/// в модель представления — так профиль меняется ровно тогда, когда его выбрал пользователь.
/// </summary>
public partial class SettingsView : UserControl
{
    private bool _initializing;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncProfileButtons();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void SyncProfileButtons()
    {
        if (ViewModel is null)
        {
            return;
        }

        _initializing = true;
        switch (ViewModel.Profile)
        {
            case PerfProfile.Economy:
                ProfileEconomy.IsChecked = true;
                break;
            case PerfProfile.MaxPerformance:
                ProfileMax.IsChecked = true;
                break;
            default:
                ProfileBalanced.IsChecked = true;
                break;
        }

        _initializing = false;
    }

    private void ProfileEconomyChecked(object sender, RoutedEventArgs e) => SetProfile(PerfProfile.Economy);

    private void ProfileBalancedChecked(object sender, RoutedEventArgs e) => SetProfile(PerfProfile.Balanced);

    private void ProfileMaxChecked(object sender, RoutedEventArgs e) => SetProfile(PerfProfile.MaxPerformance);

    private void SetProfile(PerfProfile profile)
    {
        if (_initializing || ViewModel is null)
        {
            return;
        }

        ViewModel.Profile = profile;
    }
}
