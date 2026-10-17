using System.Windows;

namespace PingForge.App;

/// <summary>
/// Заставка на время старта. Показывается только в обычном запуске: самопроверка и тихий режим
/// окон не показывают, иначе сборочная машина ждала бы их закрытия.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    /// <summary>Что сейчас происходит — теми же словами, что пишутся в журнал запуска.</summary>
    public void Stage(string text)
    {
        StageText.Text = text;
        // Принудительная отрисовка: следующий шаг сборки займёт главный поток, и без этого
        // надпись сменилась бы только после него — то есть уже никогда.
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }
}
