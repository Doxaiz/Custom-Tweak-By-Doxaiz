using System.Windows;
using System.Windows.Controls;
using PingForge.App.ViewModels;

namespace PingForge.App.Views;

/// <summary>
/// Раздел «Твики». Из кода здесь только одно действие: нажатие на пресет переносит выбор
/// в модель представления. Всё остальное — привязки, то есть текст и числа берутся из каталога.
/// </summary>
public partial class TweaksView : UserControl
{
    public TweaksView() => InitializeComponent();

    private void PresetChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: PresetViewModel preset } && DataContext is TweaksViewModel viewModel)
        {
            viewModel.SelectedPreset = preset;
        }
    }
}
