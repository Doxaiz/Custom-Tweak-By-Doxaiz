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

/// <summary>Строка → видимость: пусто → Collapsed, иначе Visible. Для примечаний об обрезке.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Пилюля масштаба: «отмечена ли эта ступень». Три кнопки 100/125/150 % выглядели одинаково, и
/// какой масштаб действует сейчас, приходилось искать в подписи. Теперь это переключатели:
/// число масштаба превращается в отметку, а отметка — обратно в число (двусторонняя привязка).
/// </summary>
public sealed class ScaleOptionConverter : IValueConverter
{
    /// <summary>Совпадение с точностью до шага: 1.25 и 1.2500000001 — это один масштаб.</summary>
    private const double Tolerance = 0.001;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double scale && OptionOf(parameter) is double option && Math.Abs(scale - option) < Tolerance;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && OptionOf(parameter) is double option ? option : Binding.DoNothing;

    private static double? OptionOf(object? parameter)
    {
        if (parameter is double direct)
        {
            return direct;
        }

        return parameter is string text &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }
}
