using System.IO;
using System.Text.Json;
using PingForge.Network;

namespace PingForge.App.ViewModels;

/// <summary>Экспорт/импорт/сброс настроек (задача R): один JSON с версией схемы и датой.</summary>
public sealed class SettingsExportViewModel : ObservableObject
{
    private readonly IAppSettingsStore _store;
    private readonly IUserDialogs _dialogs;
    private string _statusRu = "Экспорт — копия настроек с версией схемы. Импорт не применяет твики, только настройки.";

    public SettingsExportViewModel(IAppSettingsStore store, IUserDialogs dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        ExportCommand = new AsyncRelayCommand(() => ExportCoreAsync(null));
        ImportCommand = new AsyncRelayCommand(() => ImportCoreAsync(null));
        ResetCommand = new AsyncRelayCommand(ResetAsync);
    }

    public string StatusRu { get => _statusRu; private set => Set(ref _statusRu, value); }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    public const int SchemaVersion = 1;

    public sealed record ExportEnvelope(int SchemaVersion, DateTime ExportedUtc, string NoteRu, AppSettings Settings);

    public Task ExportToFileAsync(string path, CancellationToken ct = default) => ExportCoreAsync(path, ct);
    public Task ImportFromFileAsync(string path, CancellationToken ct = default) => ImportCoreAsync(path, ct);

    private async Task ExportCoreAsync(string? preselectedPath, CancellationToken ct = default)
    {
        string path = preselectedPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PingForge-настройки.json");
        try
        {
            var env = new ExportEnvelope(SchemaVersion, DateTime.UtcNow, "Пути к играм проверьте на новом компьютере.", _store.Load());
            string json = JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(true);
            StatusRu = $"Настройки сохранены в {path} — снапшоты и журнал не трогались.";
        }
        catch (Exception ex) { StatusRu = $"Не удалось сохранить: {ex.Message}"; }
    }

    private async Task ImportCoreAsync(string? preselectedPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(preselectedPath)) { StatusRu = "Укажите путь к файлу."; return; }
        string path = preselectedPath!;
        try
        {
            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(true);
            ExportEnvelope? env = JsonSerializer.Deserialize<ExportEnvelope>(json);
            if (env is null) { StatusRu = "Файл пуст или не JSON."; return; }
            if (env.SchemaVersion != SchemaVersion) { StatusRu = $"Версия схемы {env.SchemaVersion} не поддерживается (ожидается {SchemaVersion})."; return; }
            bool replace = await _dialogs.ConfirmAsync("Заменить все настройки?", "«Заменить всё» — текущие настройки перезапишутся. «Добавить новое» — добавим только то, чего у вас нет, существующее не трогаем.", "Заменить всё", "Добавить новое").ConfigureAwait(true);
            AppSettings current = _store.Load();
            AppSettings merged = replace ? env.Settings : Merge(current, env.Settings);
            _store.Save(merged);
            StatusRu = replace ? "Настройки заменены — твики не применялись, только файл." : "Настройки добавлены — существующее не тронуто, твики не применялись.";
        }
        catch (Exception ex) { StatusRu = $"Не удалось прочитать: {ex.Message}"; }
    }

    private async Task ResetAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Сбросить к заводским?", "Текущие настройки сохранятся рядом как backup (settings.json.bak). Снапшоты и журнал не удаляются.", "Сбросить", "Отмена").ConfigureAwait(true);
        if (!ok) { StatusRu = "Сброс отменён."; return; }
        try
        {
            string path = _store.SettingsFilePath;
            string bak = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (File.Exists(path)) File.Copy(path, bak, overwrite: false);
            _store.Save(new AppSettings());
            StatusRu = $"Сброшено к заводским. Резервная копия: {bak}. Снапшоты и журнал целы.";
        }
        catch (Exception ex) { StatusRu = $"Не удалось сбросить: {ex.Message}"; }
    }

    private static AppSettings Merge(AppSettings current, AppSettings incoming)
    {
        IReadOnlyList<CustomGameEntry> cur = current.CustomGames is null ? Array.Empty<CustomGameEntry>() : current.CustomGames;
        IReadOnlyList<CustomGameEntry> inc = incoming.CustomGames is null ? Array.Empty<CustomGameEntry>() : incoming.CustomGames;
        var mergedGames = cur.Concat(inc.Where(g => !cur.Any(c => c.ExeName.Equals(g.ExeName, StringComparison.OrdinalIgnoreCase)))).ToList();
        return current with { CustomGames = mergedGames };
    }
}
