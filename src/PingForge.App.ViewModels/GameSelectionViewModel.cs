using PingForge.Network;
namespace PingForge.App.ViewModels;

/// <summary>Несколько игр одновременно: список найденных, выбор «оптимизировать эту» (задача S).</summary>
public sealed class GameSelectionViewModel : ObservableObject
{
    private IReadOnlyList<DetectedGameFlow> _found = Array.Empty<DetectedGameFlow>();
    private string? _selectedProcess;
    private string _statusRu = "Игр не найдено — запустите игру и нажмите «Искать».";

    public IReadOnlyList<DetectedGameFlow> Found => _found;
    public string? SelectedProcess { get => _selectedProcess; set => Set(ref _selectedProcess, value); }
    public string StatusRu { get => _statusRu; private set => Set(ref _statusRu, value); }

    public bool HasMultiple => _found.Count > 1;
    public bool HasAny => _found.Count > 0;

    public void UpdateFound(IReadOnlyList<DetectedGameFlow> flows, string? preferred = null)
    {
        _found = flows;
        if (flows.Count == 0) { StatusRu = "Игр не найдено — запустите игру и нажмите «Искать». Проверьте, что путь к игре указан в разделе Игры."; SelectedProcess = null; }
        else if (flows.Count == 1) { SelectedProcess = flows[0].Flow.ProcessName; StatusRu = $"Найдена одна игра: {flows[0].Flow.ProcessName} — выбираю её."; }
        else
        {
            if (!string.IsNullOrWhiteSpace(preferred) && flows.Any(f => f.Flow.ProcessName.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
                SelectedProcess = preferred;
            else SelectedProcess = flows[0].Flow.ProcessName;
            StatusRu = $"Найдено {flows.Count} игр — выберите «оптимизировать эту» для нужной.";
        }
        OnPropertyChanged(nameof(Found), nameof(HasMultiple), nameof(HasAny));
    }

    public string SwitchMessageRu(string from, string to) => $"Переключаюсь с {from} на {to}: снимаю изменения прежней и начинаю заново.";
}
