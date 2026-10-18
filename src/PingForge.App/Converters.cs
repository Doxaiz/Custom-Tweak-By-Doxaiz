using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PingForge.App;

/// <summary>
/// Строка «x,y x,y …» → коллекция точек Polyline. Модель графика не знает про WPF и отдаёт точки
/// строкой в пикселях канвы: так геометрия считается и проверяется тестами, а разметка только рисует.
/// </summary>
public sealed class ChartPointsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string text && text.Length > 0
            ? PointCollection.Parse(text)
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("График рисуется в одну сторону: из модели в линии.");
}

/// <summary>Обратная видимость: false → Visible, true → Collapsed. Для «пустых состояний» карточек.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
