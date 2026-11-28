using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Строка экрана «Что изменилось» (задача K): что было → стало по каждому твику.
/// </summary>
public sealed class AppliedChangeRowViewModel : ObservableObject
{
    private string _statusRu;
    private bool _isReverting;
    private bool _hasReverted;

    public AppliedChangeRowViewModel(
        string code,
        string titleRu,
        string beforeText,
        string afterText,
        string verifiedText,
        string outcome,
        string statusRu,
        bool succeeded,
        bool rebootNeeded)
    {
        Code = code;
        TitleRu = titleRu;
        BeforeText = string.IsNullOrWhiteSpace(beforeText) ? "не удалось прочитать" : beforeText;
        AfterText = string.IsNullOrWhiteSpace(afterText) ? "не удалось прочитать" : afterText;
        VerifiedText = string.IsNullOrWhiteSpace(verifiedText) ? "не удалось прочитать" : verifiedText;
        Outcome = outcome;
        _statusRu = statusRu;
        Succeeded = succeeded;
        RebootNeeded = rebootNeeded;
        RevertCommand = new AsyncRelayCommand(RevertAsync);
    }

    public string Code { get; }
    public string TitleRu { get; }
    public string BeforeText { get; }
    public string AfterText { get; }
    public string VerifiedText { get; }
    public string Outcome { get; }
    public bool Succeeded { get; }
    public bool RebootNeeded { get; }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public bool IsReverting
    {
        get => _isReverting;
        private set => Set(ref _isReverting, value);
    }

    public bool HasReverted
    {
        get => _hasReverted;
        private set => Set(ref _hasReverted, value);
    }

    /// <summary>Кнопка «Вернуть только это» — доступна пока не откатили.</summary>
    public AsyncRelayCommand RevertCommand { get; }

    /// <summary>Действие отката устанавливается извне (нужен EngineClient + snapshot).</summary>
    public Func<Task<EngineResponse>>? RevertAction { get; set; }

    private async Task RevertAsync()
    {
        if (RevertAction is null || HasReverted || IsReverting)
        {
            return;
        }

        IsReverting = true;
        try
        {
            EngineResponse response = await RevertAction().ConfigureAwait(true);
            StatusRu = response.MessageRu;
            if (response.Ok)
            {
                HasReverted = true;
            }
        }
        catch (EngineUnavailableException ex)
        {
            StatusRu = ex.Message;
        }
        finally
        {
            IsReverting = false;
        }
    }

    /// <summary>Строка «было → стало» — не показывается, если значение не прочитано (заменяем на «не удалось прочитать», а не прочерк).</summary>
    public string ChangeLineRu => $"{BeforeText} → {VerifiedText}";

    public bool HasReadableValues => BeforeText != "не удалось прочитать" && VerifiedText != "не удалось прочитать";
}

/// <summary>
/// Экран «Что изменилось» — открывается после применения пресета или выбранных твиков (задача K).
/// </summary>
public sealed class AppliedChangesViewModel : ObservableObject
{
    private readonly List<AppliedChangeRowViewModel> _rows = [];
    private string _summaryRu = string.Empty;
    private string _snapshotId = string.Empty;
    private bool _isVisible;

    public IReadOnlyList<AppliedChangeRowViewModel> Rows => _rows;

    public string SummaryRu
    {
        get => _summaryRu;
        private set => Set(ref _summaryRu, value);
    }

    public string SnapshotId
    {
        get => _snapshotId;
        private set => Set(ref _snapshotId, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    public bool IsEmpty => _rows.Count == 0;

    /// <summary>Обновить экран после применения — данные приходят из ответа службы (BatchData.Rows).</summary>
    public void UpdateFromBatch(string snapshotId, int applied, int noOp, int skipped, int failed, IReadOnlyList<AppliedChangeRowViewModel> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        SnapshotId = snapshotId;
        SummaryRu = $"Изменено {applied}, уже было как надо {noOp}, пропущено {skipped}, ошибок {failed}.";
        IsVisible = true;
        OnPropertyChanged(nameof(Rows), nameof(IsEmpty), nameof(IsVisible));
    }

    public void Clear()
    {
        _rows.Clear();
        SnapshotId = string.Empty;
        SummaryRu = string.Empty;
        IsVisible = false;
        OnPropertyChanged(nameof(Rows), nameof(IsEmpty), nameof(IsVisible));
    }

    /// <summary>Привязать действия «Вернуть только это» к EngineClient (нужен snapshotId + code).</summary>
    public void BindRevertActions(EngineClient engine)
    {
        foreach (AppliedChangeRowViewModel row in _rows)
        {
            string code = row.Code;
            string snapshot = SnapshotId;
            row.RevertAction = () => engine.SendAsync(
                EngineCommandKind.RollbackSingleTweak,
                [("tweak", code), ("snapshot", snapshot)]);
        }
    }
}
