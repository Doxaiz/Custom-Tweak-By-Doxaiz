using System.IO.Compression;
using PingForge.Core.Diagnostics;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Пакет для поддержки: что человек увидел на экране — то и лежит в архиве, а прежний пакет сбой не съедает.
/// </summary>
/// <remarks>
/// Пакет собирают, когда что-то уже сломалось. Раньше сборка удаляла прежний архив до создания нового:
/// не хватило места или прав — и человек остался без старого пакета и с обрезанным новым, то есть
/// без ничего. Теперь архив собирается рядом и подменяет прежний целиком.
/// </remarks>
public sealed class SupportPackageTests
{
    [Fact]
    public void Пакет_собирается_и_содержит_ровно_то_что_в_плане()
    {
        using var dir = new TempFolder();
        string logPath = Path.Combine(dir.Path, "service.log");
        File.WriteAllLines(logPath, ["строка 1", "строка 2", "строка 3"]);
        string target = Path.Combine(dir.Path, "PingForge-поддержка.zip");

        SupportPackagePlan plan = Plan(target, logPath);
        SupportPackageBuildResult result = SupportPackageBuilder.Build(plan);

        Assert.True(result.Built, result.ProblemRu);
        Assert.Equal(2, result.FilesIncluded);
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + SupportPackageBuilder.TempSuffix));

        using (ZipArchive archive = ZipFile.OpenRead(result.PathRu))
        {
            Assert.Equal(new[] { "service.log", "сводка.txt" }, archive.Entries.Select(entry => entry.FullName).ToArray());
        }

        Assert.Contains("строка 3", ReadEntry(result.PathRu, "service.log"), StringComparison.Ordinal);
        Assert.Contains("Пакет собран тестом", ReadEntry(result.PathRu, "сводка.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void Второй_пакет_под_тем_же_именем_заменяет_первый_целиком()
    {
        using var dir = new TempFolder();
        string logPath = Path.Combine(dir.Path, "service.log");
        File.WriteAllText(logPath, "старая строка");
        string target = Path.Combine(dir.Path, "Пакет.zip");

        Assert.True(SupportPackageBuilder.Build(Plan(target, logPath, summary: "первый")).Built);

        File.WriteAllText(logPath, "новая строка");
        Assert.True(SupportPackageBuilder.Build(Plan(target, logPath, summary: "второй")).Built);

        string summary = ReadEntry(target, "сводка.txt");
        Assert.Contains("второй", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("первый", summary, StringComparison.Ordinal);
        Assert.Contains("новая строка", ReadEntry(target, "service.log"), StringComparison.Ordinal);
    }

    [Fact]
    public void Сбой_сборки_не_портит_прежний_пакет()
    {
        using var dir = new TempFolder();
        string logPath = Path.Combine(dir.Path, "service.log");
        File.WriteAllText(logPath, "первая версия");
        string target = Path.Combine(dir.Path, "Пакет.zip");
        Assert.True(SupportPackageBuilder.Build(Plan(target, logPath)).Built);
        long before = new FileInfo(target).Length;
        string contentBefore = ReadEntry(target, "service.log");

        // Место временного архива заняла папка: собрать новый пакет некуда.
        Directory.CreateDirectory(target + SupportPackageBuilder.TempSuffix);

        SupportPackageBuildResult result = SupportPackageBuilder.Build(Plan(target, logPath, summary: "второй"));

        Assert.False(result.Built);
        Assert.NotEmpty(result.ProblemRu);
        Assert.Equal(before, new FileInfo(target).Length);
        Assert.Equal(contentBefore, ReadEntry(target, "service.log"));
    }

    [Fact]
    public void План_не_включает_отсутствующий_файл_и_говорит_об_этом()
    {
        using var dir = new TempFolder();
        string target = Path.Combine(dir.Path, "Пакет.zip");
        string missing = Path.Combine(dir.Path, "нет-такого.log");

        SupportPackagePlan plan = Plan(target, missing);

        SupportPackageItem item = Assert.Single(plan.Items, i => i.EntryName == "service.log");
        Assert.False(item.Included);
        Assert.Contains("нет файла", item.ReasonRu, StringComparison.Ordinal);
        Assert.True(plan.HasSomething, "сводка-текст остаётся, пакет собирать есть из чего");
    }

    [Fact]
    public void Журнал_сверх_предела_выкидывается_а_план_объясняет_почему()
    {
        using var dir = new TempFolder();
        string logPath = Path.Combine(dir.Path, "service.log");
        File.WriteAllLines(logPath, Enumerable.Range(1, 400).Select(n => $"строка {n} " + new string('ж', 100)));
        string target = Path.Combine(dir.Path, "Пакет.zip");

        SupportPackagePlan plan = Plan(target, logPath, limitBytes: 4096);

        SupportPackageItem logItem = Assert.Single(plan.Items, i => i.EntryName == "service.log");
        Assert.False(logItem.Included);
        Assert.Contains("не влез в предел", logItem.ReasonRu, StringComparison.Ordinal);
        Assert.True(plan.FitsLimit);
        Assert.True(plan.HasSomething);
    }

    [Fact]
    public void Пустой_план_не_собирается_и_называет_причину()
    {
        using var dir = new TempFolder();
        string target = Path.Combine(dir.Path, "Пакет.zip");
        SupportPackagePlan plan = SupportPackageBuilder.Plan(
            target,
            [SupportPackageEntry.Text("Сводка", "сводка.txt", "текст", () => string.Empty)]);

        SupportPackageBuildResult result = SupportPackageBuilder.Build(plan);

        Assert.False(result.Built);
        Assert.Contains("нечего положить", result.ProblemRu, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    private static SupportPackagePlan Plan(string target, string logPath, string summary = "Пакет собран тестом", long limitBytes = SupportPackageBuilder.MaxBytes) =>
        SupportPackageBuilder.Plan(
            target,
            [
                SupportPackageEntry.LogFile("Журнал службы", "service.log", "последние строки", logPath, maxLines: 500),
                SupportPackageEntry.Text("Сводка", "сводка.txt", "короткий текст", () => summary),
            ],
            limitBytes);

    /// <summary>Прочитать запись архива, закрыв его: на Windows открытый архив держит файл.</summary>
    private static string ReadEntry(string archivePath, string name)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"в архиве нет записи «{name}»");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Временная папка, которая убирает за собой всё, что в ней появилось.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pingforge-support-" + Guid.NewGuid().ToString("N"));
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
