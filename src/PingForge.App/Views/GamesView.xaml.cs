using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PingForge.App.ViewModels;

namespace PingForge.App.Views;

/// <summary>Раздел «Игры»: выбрать игру и открыть её — оптимизация начнётся следом.</summary>
public partial class GamesView : UserControl
{
    public GamesView() => InitializeComponent();

    private void RemoveCustomClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameCardViewModel card }
            && DataContext is GameLauncherViewModel launcher)
        {
            launcher.RemoveCustomGame(card);
        }
    }

    private void RenameCustomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameCardViewModel card }
            || DataContext is not GameLauncherViewModel launcher)
        {
            return;
        }

        // Маленький диалог ввода названия: без него переименование своей игры было бы
        // обещано в модели, но недоступно человеку в окне.
        // Имя для экранного диктора: у поля ввода его нет из содержимого, и без него диктор
        // скажет просто «поле редактирования».
        var box = new TextBox { Text = card.TitleRu, MinWidth = 260, Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetName(box, "Новое название своей игры");
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Новое название своей игры:", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dialog = new Window
        {
            Title = "Переименовать игру",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
        };

        var ok = new Button { Content = "Сохранить", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        box.Focus();
        box.SelectAll();
        if (dialog.ShowDialog() == true)
        {
            launcher.RenameCustomGame(card, box.Text);
        }
    }
}
