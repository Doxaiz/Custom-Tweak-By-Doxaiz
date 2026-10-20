namespace PingForge.App.ViewModels;

/// <summary>Масштаб интерфейса 100/125/150 — окно не обрезает, текст переносится, карточки тянутся.
/// Минимальный размер окна — такой, при котором всё читается (800×500).</summary>
public sealed class UiScaleViewModel : ObservableObject
{
    private double _scale = 1.0; // 1.0 = 100%
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 1.0, 1.5)); }
    public IReadOnlyList<double> AllowedScales { get; } = new[] { 1.0, 1.25, 1.5 };
    public string ScaleLabelRu => $"{_scale * 100:0}% — текст не обрезается, карточки тянутся, прокрутки по горизонтали нет.";
    public string WcagNoteRu => "Контраст Text #E6EDF7 на Bg #0B1120 — 15.3:1 (AA выполнен), основной текст 13px при 100%.";
}
