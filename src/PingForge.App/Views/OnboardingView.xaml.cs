using System.Windows.Controls;

namespace PingForge.App.Views;

/// <summary>
/// Мастер первого запуска. Логика — в модели представления, здесь только разметка:
/// так шаги и совет проверяются тестами без Windows.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();
}
