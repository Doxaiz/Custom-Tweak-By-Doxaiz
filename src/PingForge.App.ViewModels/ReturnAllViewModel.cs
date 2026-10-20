using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>Одна кнопка «Вернуть всё, что меняла программа» (задача L): останавливает → снимает → откатывает → проверяет.
/// Чужие маршруты/политики не трогает — этим занимается служба (repair-network / rollback только своё).</summary>
public sealed class ReturnAllViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private string _statusRu = "Можно вернуть всё одной кнопкой: программа снимет только своё.";
    private bool _isRunning;
    private IReadOnlyList<string> _lastSteps = Array.Empty<string>();

    public ReturnAllViewModel(EngineClient engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public string StatusRu { get => _statusRu; private set => Set(ref _statusRu, value); }
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public IReadOnlyList<string> LastSteps => _lastSteps;

    public AsyncRelayCommand ReturnAllCommand { get; private set; } = null!;

    public void AttachDialogs(IUserDialogs dialogs)
    {
        ReturnAllCommand = new AsyncRelayCommand(async () =>
        {
            bool ok = await dialogs.ConfirmAsync("Вернуть всё как было?", "Программа остановит живую оптимизацию, снимет свои маршруты и QoS, откатит твики последнего снапшота и проверит остатки. Чужие настройки не трогает.", "Вернуть", "Отмена").ConfigureAwait(true);
            if (!ok) { StatusRu = "Возврат отменён."; return; }
            await ExecuteAsync().ConfigureAwait(true);
        });
    }

    private async Task ExecuteAsync()
    {
        IsRunning = true;
        StatusRu = "Возвращаю всё…";
        try
        {
            EngineResponse resp = await _engine.SendAsync(EngineCommandKind.ReturnAll).ConfigureAwait(true);
            StatusRu = resp.MessageRu;
            // Парсим шаги из DataJson если есть
            try
            {
                if (!string.IsNullOrWhiteSpace(resp.DataJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(resp.DataJson);
                    if (doc.RootElement.TryGetProperty("stepsRu", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        _lastSteps = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                        OnPropertyChanged(nameof(LastSteps));
                    }
                }
            } catch { }
        }
        catch (EngineUnavailableException ex) { StatusRu = $"Не удалось, служба не отвечает: {ex.Message}"; }
        finally { IsRunning = false; }
    }
}

/// <summary>Сколько занимает база — видно человеку в «О программе» (задача L).</summary>
public sealed class StorageInfoViewModel : ObservableObject
{
    private readonly EngineClient _engine;
    private string _dbPath = "—";
    private long _dbBytes;
    private int _snapshots;
    private int _journalRows;
    private string _statusRu = "Нажмите «Обновить», чтобы увидеть размер базы.";
    private bool _isLoading;

    public StorageInfoViewModel(EngineClient engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public string DbPath { get => _dbPath; private set => Set(ref _dbPath, value); }
    public long DbBytes { get => _dbBytes; private set => Set(ref _dbBytes, value); }
    public int Snapshots { get => _snapshots; private set => Set(ref _snapshots, value); }
    public int JournalRows { get => _journalRows; private set => Set(ref _journalRows, value); }
    public string StatusRu { get => _statusRu; private set => Set(ref _statusRu, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public string SizeRu => $"{_dbBytes / 1024} КБ";
    public string SummaryRu => $"Путь: {DbPath} — {SizeRu}, снапшотов: {Snapshots}, записей: {JournalRows}.";

    public AsyncRelayCommand RefreshCommand { get; private set; } = null!;
    public AsyncRelayCommand CleanupCommand { get; private set; } = null!;

    public void AttachDialogs(IUserDialogs dialogs)
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CleanupCommand = new AsyncRelayCommand(async () =>
        {
            bool ok = await dialogs.ConfirmAsync("Почистить старые снапшоты?", "Будут удалены снапшоты старше 30 дней, останется последние 10. Снапшоты с незакрытыми операциями не удаляются никогда.", "Почистить", "Отмена").ConfigureAwait(true);
            if (!ok) return;
            await CleanupAsync().ConfigureAwait(true);
        });
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            EngineResponse resp = await _engine.SendAsync(EngineCommandKind.StorageInfo).ConfigureAwait(true);
            StatusRu = resp.MessageRu;
            if (!string.IsNullOrWhiteSpace(resp.DataJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(resp.DataJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("dbPath", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String) DbPath = p.GetString() ?? DbPath;
                    if (root.TryGetProperty("dbBytes", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number) DbBytes = b.GetInt64();
                    if (root.TryGetProperty("snapshots", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number) Snapshots = s.GetInt32();
                    if (root.TryGetProperty("journalRows", out var j) && j.ValueKind == System.Text.Json.JsonValueKind.Number) JournalRows = j.GetInt32();
                    OnPropertyChanged(nameof(SizeRu), nameof(SummaryRu));
                } catch { }
            }
        }
        catch (EngineUnavailableException ex) { StatusRu = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task CleanupAsync()
    {
        IsLoading = true;
        try
        {
            EngineResponse resp = await _engine.SendAsync(EngineCommandKind.CleanupSnapshots, [("keep","10"),("olderThanDays","30")]).ConfigureAwait(true);
            StatusRu = resp.MessageRu;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (EngineUnavailableException ex) { StatusRu = ex.Message; }
        finally { IsLoading = false; }
    }
}
