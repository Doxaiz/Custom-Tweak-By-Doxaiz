using PingForge.Core.Diagnostics;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Запись отчёта о падении: файл либо целый, либо его нет.
/// </summary>
/// <remarks>
/// Отчёт пишется в самый неудачный момент — когда программа уже сломалась. Половина отчёта хуже
/// отсутствия: человек отдаёт его как «вот что случилось», а в нём обрезана ошибка. Поэтому
/// проверяется, что запись идёт через временный файл, хвост не остаётся, а два падения в одну
/// миллисекунду не съедают отчёты друг друга.
/// </remarks>
public sealed class CrashReportWriteTests
{
    private static readonly DateTime Moment = new(2026, 11, 18, 12, 34, 56, 789, DateTimeKind.Local);

    [Fact]
    public void Отчёт_пишется_целиком_и_без_временного_хвоста()
    {
        using var dir = new TempFolder();

        CrashReportWriteResult result = CrashReportWriter.Write(
            dir.Path,
            "Проверка записи отчёта",
            new InvalidOperationException("служба не ответила"),
            new CrashReportContext(AppVersionRu: "1.0.0", WindowsRu: "Windows 11"),
            logLines: ["строка журнала"],
            momentLocal: Moment);

        Assert.True(result.Written, result.ProblemRu);
        Assert.True(File.Exists(result.PathRu));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + CrashReportWriter.TempSuffix));

        byte[] bytes = File.ReadAllBytes(result.PathRu);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        string text = File.ReadAllText(result.PathRu);
        Assert.Contains("отчёт об ошибке", text, StringComparison.Ordinal);
        Assert.Contains("служба не ответила", text, StringComparison.Ordinal);
        Assert.Contains("Проверка записи отчёта", text, StringComparison.Ordinal);

        // Программа обещает, что отчёт никуда не уходит — обещание написано в самом файле.
        Assert.Contains("никуда его не отправляет", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Два_падения_в_одну_миллисекунду_не_съедают_отчёт_друг_друга()
    {
        using var dir = new TempFolder();
        var context = new CrashReportContext(AppVersionRu: "1.0.0");

        CrashReportWriteResult first = CrashReportWriter.Write(dir.Path, "первое падение", new Exception("раз"), context, momentLocal: Moment);
        CrashReportWriteResult second = CrashReportWriter.Write(dir.Path, "второе падение", new Exception("два"), context, momentLocal: Moment);

        Assert.True(first.Written, first.ProblemRu);
        Assert.False(second.Written);
        Assert.Contains("IOException", second.ProblemRu, StringComparison.Ordinal);

        // Первый отчёт цел, временного хвоста от второго не осталось.
        Assert.Contains("первое падение", File.ReadAllText(first.PathRu), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + CrashReportWriter.TempSuffix));
        Assert.Single(Directory.GetFiles(dir.Path, "crash-*.txt"));
    }

    [Fact]
    public void Некуда_писать_отчёт_возвращает_причину_а_не_падает()
    {
        using var dir = new TempFolder();
        string fileInsteadOfFolder = Path.Combine(dir.Path, "это-файл-а-не-папка");
        File.WriteAllText(fileInsteadOfFolder, "занято");

        CrashReportWriteResult result = CrashReportWriter.Write(
            Path.Combine(fileInsteadOfFolder, "crash"),
            "проверка отказа",
            new Exception("раз"),
            new CrashReportContext());

        Assert.False(result.Written);
        Assert.NotEmpty(result.ProblemRu);
        Assert.Empty(result.PathRu);
    }

    [Fact]
    public void Пустой_каталог_называется_причиной()
    {
        CrashReportWriteResult result = CrashReportWriter.Write(
            "   ",
            "проверка отказа",
            new Exception("раз"),
            new CrashReportContext());

        Assert.False(result.Written);
        Assert.Contains("каталог", result.ProblemRu, StringComparison.Ordinal);
    }

    /// <summary>Временная папка, которая убирает за собой всё, что в ней появилось.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingforge-crash-" + Guid.NewGuid().ToString("N"));
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
