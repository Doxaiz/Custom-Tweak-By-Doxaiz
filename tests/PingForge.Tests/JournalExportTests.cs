using System.Text;
using PingForge.App.ViewModels;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Выгрузка журнала в CSV: файл либо целый, либо его нет, и чужой файл не затирается молча.
/// </summary>
/// <remarks>
/// Выгрузку человек несёт в таблицу или отдаёт коллеге — половина CSV там бесполезна и опасна:
/// по ней не видно, что часть записей потерялась. Поэтому проверяется не только «файл появился»,
/// но и что при занятом имени прежний файл остаётся нетронутым, а недописанного хвоста не остаётся.
/// </remarks>
public sealed class JournalExportTests
{
    /// <summary>Ответы исправной службы для команд, которым тест не придумывает своих.</summary>
    private static readonly FakeEngineChannel Fallback = new();

    [Fact]
    public async Task Экспорт_CSV_пишет_файл_и_не_оставляет_временных()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "journal.csv");
        (JournalViewModel journal, _) = await LoadedJournalAsync();

        await journal.ExportCsvToAsync(path);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + AtomicFileWrite.TempSuffix));

        string csv = await File.ReadAllTextAsync(path, Encoding.UTF8);
        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // шапка + две записи

        // Выгрузка с BOM: Excel иначе покажет кириллицу мусором.
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        // В подписи — ровно то, что ушло в файл: число строк берётся из тех же строк журнала.
        Assert.Equal(2, journal.FilteredRows.Count);
        Assert.True(
            journal.StatusRu.Contains("(2 строки)", StringComparison.Ordinal),
            $"в статусе нет числа строк: «{journal.StatusRu}»");
    }

    [Fact]
    public async Task Экспорт_CSV_не_затирает_существующий_файл_молча()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "journal.csv");
        const string было = "чужая выгрузка";
        await File.WriteAllTextAsync(path, было);
        (JournalViewModel journal, _) = await LoadedJournalAsync();

        await journal.ExportCsvToAsync(path);

        Assert.Contains("Не удалось сохранить CSV", journal.StatusRu, StringComparison.Ordinal);
        Assert.Equal(было, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + AtomicFileWrite.TempSuffix));
    }

    [Fact]
    public async Task Одна_запись_подписана_как_строка_а_не_строк()
    {
        using var dir = new TempFolder();
        string path = Path.Combine(dir.Path, "journal.csv");
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.JournalTail
            ? EngineResponse.Success(
                request.Id,
                "Последняя 1 запись журнала.",
                """
                {"count":1,"records":[
                {"loggedUtc":"2026-10-16T12:10:00Z","eventKind":"TWEAK_APPLY","tweakCode":"TCP_NO_DELAY","tweakTitleRu":"Отключение алгоритма Нейгла","previousValueText":"0","appliedValueText":"1","verifiedActualText":"1","succeeded":true,"rebootNeeded":false,"explanationRu":"VerifiedEffective"}
                ]}
                """)
            : Fallback.DefaultAnswer(request));
        var journal = new JournalViewModel(new EngineClient(channel));
        await journal.RefreshCommand.ExecuteAsync();

        await journal.ExportCsvToAsync(path);

        Assert.Equal(1, journal.FilteredRows.Count);
        Assert.True(
            journal.StatusRu.Contains("(1 строка)", StringComparison.Ordinal),
            $"одна строка должна называться «1 строка», а статус: «{journal.StatusRu}»");
    }

    private static async Task<(JournalViewModel Journal, FakeEngineChannel Channel)> LoadedJournalAsync()
    {
        var channel = new FakeEngineChannel();
        var journal = new JournalViewModel(new EngineClient(channel));
        await journal.RefreshCommand.ExecuteAsync();
        return (journal, channel);
    }

    /// <summary>Временная папка, которая убирает за собой всё, что в ней появилось.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingforge-journal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Временная папка, которую не дали убрать, — не повод валить тест.
            }
        }
    }
}
