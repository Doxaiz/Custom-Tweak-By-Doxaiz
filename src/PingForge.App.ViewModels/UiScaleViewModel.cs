using PingForge.Core.Ui;

namespace PingForge.App.ViewModels;

/// <summary>
/// Масштаб интерфейса 100/125/150. Окно масштабируется раскладкой
/// (<c>LayoutTransform</c> в <c>MainWindow.xaml</c>), а не растяжением картинки: текст остаётся
/// резким, карточки тянутся, горизонтальной прокрутки нет. Минимальный размер окна при 100 % —
/// 860×560 точек; при другом масштабе он растёт вместе с содержимым (это делает код окна).
/// </summary>
public sealed class UiScaleViewModel : ObservableObject
{
    /// <summary>Минимальный размер окна при 100 % — тот, при котором всё читается.</summary>
    public const double MinWidthAt100 = 860;

    /// <summary>Минимальная высота окна при 100 %.</summary>
    public const double MinHeightAt100 = 560;

    private double _scale = 1.0; // 1.0 = 100 %

    private string _themeTextColor = PaletteNote.TextColor;
    private string _themeMutedColor = PaletteNote.MutedColor;
    private string _themeBgColor = PaletteNote.BgColor;

    public double Scale
    {
        get => _scale;
        set
        {
            if (Set(ref _scale, Math.Clamp(value, 1.0, 1.5)))
            {
                OnPropertyChanged(nameof(ScaleLabelRu));
            }
        }
    }

    public IReadOnlyList<double> AllowedScales { get; } = new[] { 1.0, 1.25, 1.5 };

    /// <summary>
    /// Что масштаб делает на самом деле. Числа минимального окна берутся из констант, а не
    /// пишутся руками: раньше здесь стояло 800×500 при настоящем минимуме 860×560.
    /// </summary>
    public string ScaleLabelRu =>
        $"{_scale * 100:0}% — раскладка пересобирается, текст остаётся резким, карточки тянутся. " +
        $"Окно не меньше {MinWidthAt100 * _scale:0}×{MinHeightAt100 * _scale:0} точек. " +
        "Масштаб запоминается в настройках.";

    /// <summary>
    /// Контраст палитры: считается по цветам, которые передало окно (<see cref="ApplyTheme"/>),
    /// а не печатается по памяти. Раньше здесь стояли готовые «17,1:1» и «7,5:1»; тест сверял их
    /// с темой, но правка палитры означала ручную правку текста, а вывод об AA и AAA был отдельным
    /// утверждением. Теперь текст собирается из чисел, и вывод следует из них.
    /// </summary>
    public string WcagNoteRu => PaletteNote.SentenceRu(_themeTextColor, _themeMutedColor, _themeBgColor);

    /// <summary>
    /// Цвета настоящей темы для подписи о контрасте. Их передаёт окно из ресурсов <c>App.xaml</c>:
    /// модели представления палитру не читают, но и не выдумывают — до передачи действуют значения
    /// по умолчанию из <see cref="PaletteNote"/>, и тест сверяет их с темой.
    /// </summary>
    /// <param name="textColor">Цвет основного текста.</param>
    /// <param name="mutedColor">Цвет подписей.</param>
    /// <param name="bgColor">Цвет фона.</param>
    public void ApplyTheme(string textColor, string mutedColor, string bgColor)
    {
        _themeTextColor = textColor;
        _themeMutedColor = mutedColor;
        _themeBgColor = bgColor;
        OnPropertyChanged(nameof(WcagNoteRu));
    }
}
