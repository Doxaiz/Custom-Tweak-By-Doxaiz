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
            }
            catch (System.Text.Json.JsonException ex)
            {
                // Сам возврат состоялся, но список шагов пришёл нечитаемым. Раньше это глоталось,
                // и человек видел «готово» без шагов, считая, что шагов не было.
                StatusRu = $"{StatusRu} Список шагов прочитать не удалось: {ex.Message}".Trim();
            }
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
    private bool _healthy = true;
    private string? _integrityProblemRu;
    private int _backupsCount;
    private string? _latestBackupName;
    private int _corruptFiles;

    public StorageInfoViewModel(EngineClient engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public string DbPath { get => _dbPath; private set => Set(ref _dbPath, value); }
    public long DbBytes { get => _dbBytes; private set => Set(ref _dbBytes, value); }
    public int Snapshots { get => _snapshots; private set => Set(ref _snapshots, value); }
    public int JournalRows { get => _journalRows; private set => Set(ref _journalRows, value); }
    public string StatusRu { get => _statusRu; private set => Set(ref _statusRu, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public string SizeRu => $"{_dbBytes / 1024} КБ";
    public string SummaryRu => $"Путь: {DbPath} — {SizeRu}, снапшотов: {Snapshots}, записей: {JournalRows}.";

    /// <summary>Проходит ли база проверку целостности: то, что раньше выяснялось только при откате.</summary>
    public string HealthRu => _healthy
        ? "Состояние базы: проверка целостности пройдена."
        : $"Состояние базы: проверка целостности не пройдена — {_integrityProblemRu ?? "причина не прочитана"}.";

    /// <summary>Сколько резервных копий рядом и какая свежая: из неё и восстанавливаем.</summary>
    public string BackupsRu => _backupsCount == 0
        ? "Резервных копий нет: восстанавливать не из чего."
        : $"Резервных копий: {_backupsCount}, свежая — «{_latestBackupName}».";

    /// <summary>Отложенные испорченные файлы не удаляются — о них надо сказать прямо.</summary>
    public string CorruptRu => _corruptFiles == 0
        ? string.Empty
        : $"Отложенных испорченных файлов: {_corruptFiles} — они сохранены рядом для разбора.";

    public bool CanRestore => _backupsCount > 0;

    public AsyncRelayCommand RefreshCommand { get; private set; } = null!;
    public AsyncRelayCommand CleanupCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreCommand { get; private set; } = null!;

    public void AttachDialogs(IUserDialogs dialogs)
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CleanupCommand = new AsyncRelayCommand(async () =>
        {
            bool ok = await dialogs.ConfirmAsync("Почистить старые снапшоты?", "Будут удалены снапшоты старше 30 дней, останется последние 10. Снапшоты с незакрытыми операциями не удаляются никогда.", "Почистить", "Отмена").ConfigureAwait(true);
            if (!ok) return;
            await CleanupAsync().ConfigureAwait(true);
        });

        RestoreCommand = new AsyncRelayCommand(async () =>
        {
            bool ok = await dialogs.ConfirmAsync(
                "Восстановить базу из последней копии?",
                "Файл базы будет заменён резервной копией. Журнал и снапшоты, сделанные после этой копии, " +
                "пропадут — откатить изменения по ним будет нечем. Если база в порядке, это не нужно.",
                "Восстановить",
                "Отмена").ConfigureAwait(true);
            if (!ok) return;
            await RestoreAsync().ConfigureAwait(true);
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
                    if (root.TryGetProperty("healthy", out var h) && (h.ValueKind == System.Text.Json.JsonValueKind.True || h.ValueKind == System.Text.Json.JsonValueKind.False)) _healthy = h.GetBoolean();
                    if (root.TryGetProperty("integrityProblemRu", out var ip)) _integrityProblemRu = ip.ValueKind == System.Text.Json.JsonValueKind.String ? ip.GetString() : null;
                    if (root.TryGetProperty("backups", out var bc) && bc.ValueKind == System.Text.Json.JsonValueKind.Number) _backupsCount = bc.GetInt32();
                    if (root.TryGetProperty("latestBackupName", out var lb)) _latestBackupName = lb.ValueKind == System.Text.Json.JsonValueKind.String ? lb.GetString() : null;
                    if (root.TryGetProperty("corruptFiles", out var cf) && cf.ValueKind == System.Text.Json.JsonValueKind.Number) _corruptFiles = cf.GetInt32();
                    OnPropertyChanged(nameof(SizeRu), nameof(SummaryRu), nameof(HealthRu), nameof(BackupsRu), nameof(CorruptRu), nameof(CanRestore));
                }
                catch (System.Text.Json.JsonException ex)
                {
                    // Числа о базе пришли нечитаемыми: показывать прежние как свежие нельзя.
                    StatusRu = $"{StatusRu} Данные о базе прочитать не удалось: {ex.Message}".Trim();
                }
            }
        }
        catch (EngineUnavailableException ex) { StatusRu = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task RestoreAsync()
    {
        IsLoading = true;
        try
        {
            EngineResponse resp = await _engine.SendAsync(EngineCommandKind.DbRestore, [("confirm", "yes")]).ConfigureAwait(true);
            string answer = resp.MessageRu;

            // Обновление перезапишет StatusRu своим «база прочитана», поэтому итог восстановления
            // ставим после него: человек должен увидеть, что именно произошло.
            await RefreshAsync().ConfigureAwait(true);
            StatusRu = answer;
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
