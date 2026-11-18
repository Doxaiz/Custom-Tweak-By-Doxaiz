using System.IO.Compression;
using System.Text;

namespace PingForge.Core.Diagnostics;

/// <summary>Содержимое одного пункта пакета.</summary>
public sealed record SupportPackageContent(string Text, bool Exists, string MissingRu, int TotalLines);

/// <summary>
/// Описание пункта плана: что читаем, как называем в архиве, лимит строк для логов.
/// </summary>
public sealed class SupportPackageEntry
{
    public string NameRu { get; }
    public string EntryName { get; }
    public string DescriptionRu { get; }
    public int MaxLines { get; }
    private readonly Func<string> _readText;
    private readonly string? _filePath;

    private SupportPackageEntry(string nameRu, string entryName, string descriptionRu, int maxLines, Func<string> readText, string? filePath)
    {
        NameRu = nameRu;
        EntryName = entryName;
        DescriptionRu = descriptionRu;
        MaxLines = maxLines;
        _readText = readText;
        _filePath = filePath;
    }

    public static SupportPackageEntry Text(string nameRu, string entryName, string descriptionRu, Func<string> readText) =>
        new(nameRu, entryName, descriptionRu, maxLines: 0, readText, filePath: null);

    public static SupportPackageEntry LogFile(string nameRu, string entryName, string descriptionRu, string filePath, int maxLines = 500) =>
        new(nameRu, entryName, descriptionRu, maxLines,
            readText: () => ReadLogFile(filePath, maxLines),
            filePath: filePath);

    public SupportPackageContent Read()
    {
        try
        {
            // Для файлов проверяем существование: отсутствие — не ошибка, а «нет файла».
            if (_filePath is not null)
            {
                if (!File.Exists(_filePath))
                {
                    return new SupportPackageContent(string.Empty, Exists: false, MissingRu: "нет файла", TotalLines: 0);
                }

                // Дополнительная проверка на существование через _readText уже учтёт обрезку.
            }

            string text = _readText();
            if (text is null)
            {
                return new SupportPackageContent(string.Empty, Exists: false, MissingRu: "нет файла", TotalLines: 0);
            }

            int totalLines = 0;
            if (_filePath is not null)
            {
                try
                {
                    totalLines = File.ReadAllLines(_filePath).Length;
                }
                catch
                {
                    totalLines = text.Split('\n').Length;
                }
            }
            else
            {
                totalLines = text.Split('\n').Length;
            }

            return new SupportPackageContent(text, Exists: true, MissingRu: string.Empty, TotalLines: totalLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SupportPackageContent(string.Empty, Exists: false, MissingRu: $"{ex.GetType().Name}: {ex.Message}", TotalLines: 0);
        }
    }

    private static string ReadLogFile(string path, int maxLines)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("файл не найден", path);
        }

        string[] all = File.ReadAllLines(path);
        if (maxLines <= 0 || all.Length <= maxLines)
        {
            return string.Join(Environment.NewLine, all);
        }

        List<string> tail = CrashReportText.TakeTail(all, maxLines);
        return string.Join(Environment.NewLine, tail);
    }
}

/// <summary>Пункт плана для экрана предпросмотра.</summary>
public sealed record SupportPackageItem(
    string NameRu,
    string EntryName,
    string DescriptionRu,
    long SizeBytes,
    bool Included,
    string ReasonRu,
    string TruncatedNoteRu)
{
    public string SizeRu => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024.0:0.#} КБ"
        : $"{SizeBytes / (1024.0 * 1024.0):0.##} МБ";
}

/// <summary>Результат сборки пакета.</summary>
public sealed record SupportPackageBuildResult(bool Built, string PathRu, int FilesIncluded, string ProblemRu)
{
    public static SupportPackageBuildResult Ok(string path, int files) => new(true, path, files, string.Empty);
    public static SupportPackageBuildResult Failed(string problem) => new(false, string.Empty, 0, problem);
}

/// <summary>План пакета: что войдёт, что не войдёт и почему.</summary>
public sealed class SupportPackagePlan
{
    public const long MaxBytes = 20L * 1024 * 1024;

    public string TargetPathRu { get; }
    public IReadOnlyList<SupportPackageItem> Items { get; }
    public long LimitBytes { get; }
    public long TotalBytes { get; }
    public bool HasSomething => Items.Any(item => item.Included);
    public bool FitsLimit => TotalBytes <= LimitBytes;

    public string SummaryRu
    {
        get
        {
            int count = Items.Count(item => item.Included);
            string size = TotalBytes < 1024 * 1024
                ? $"{TotalBytes / 1024.0:0.#} КБ"
                : $"{TotalBytes / (1024.0 * 1024.0):0.##} МБ";

            return count == 0
                ? "В пакет нечего положить: ни журналов, ни отчёта."
                : $"В пакет войдут файлов: {count}, общий размер около {size}.";
        }
    }

    public string WarningRu =>
        "Внутри будут адреса игровых серверов и пути к файлам на этом компьютере. " +
        "Отправляйте пакет только тому, кому доверяете: сам он никуда не уходит.";

    internal IReadOnlyList<SupportPackageEntry> IncludedEntries { get; }
    internal IReadOnlyList<SupportPackageContent> IncludedContents { get; }

    internal SupportPackagePlan(
        string targetPathRu,
        IReadOnlyList<SupportPackageItem> items,
        long limitBytes,
        IReadOnlyList<SupportPackageEntry> includedEntries,
        IReadOnlyList<SupportPackageContent> includedContents,
        long totalBytes)
    {
        TargetPathRu = targetPathRu;
        Items = items;
        LimitBytes = limitBytes;
        IncludedEntries = includedEntries;
        IncludedContents = includedContents;
        TotalBytes = totalBytes;
    }
}

/// <summary>Построение плана и сборка архива.</summary>
public static class SupportPackageBuilder
{
    public const long MaxBytes = SupportPackagePlan.MaxBytes;

    /// <summary>Хвост временного архива: он лежит рядом с пакетом и не остаётся после сбоя.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Составить план: прочитать все источники и решить, что войдёт. Ничего не пишет на диск.
    /// </summary>
    public static SupportPackagePlan Plan(
        string targetPathRu,
        IEnumerable<SupportPackageEntry> entries,
        long limitBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (limitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitBytes), limitBytes, "Предел размера должен быть больше нуля.");
        }

        var includedEntries = new List<SupportPackageEntry>();
        var includedContents = new List<SupportPackageContent>();
        var items = new List<SupportPackageItem>();
        long total = 0;

        // Сначала — готовый текст: без него пакет бессмысленен, и он не выкидывается.
        foreach (SupportPackageEntry entry in entries)
        {
            SupportPackageContent content = entry.Read();

            if (!content.Exists)
            {
                items.Add(new SupportPackageItem(
                    entry.NameRu,
                    entry.EntryName,
                    entry.DescriptionRu,
                    0,
                    Included: false,
                    ReasonRu: $"нет файла: {content.MissingRu}",
                    TruncatedNoteRu: string.Empty));
                continue;
            }

            if (content.Text.Length == 0)
            {
                items.Add(new SupportPackageItem(
                    entry.NameRu,
                    entry.EntryName,
                    entry.DescriptionRu,
                    0,
                    Included: false,
                    ReasonRu: "файл пуст",
                    TruncatedNoteRu: string.Empty));
                continue;
            }

            long size = Encoding.UTF8.GetByteCount(content.Text);
            bool truncated = entry.MaxLines > 0 && content.TotalLines > entry.MaxLines;

            includedEntries.Add(entry);
            includedContents.Add(content);
            total += size;

            items.Add(new SupportPackageItem(
                entry.NameRu,
                entry.EntryName,
                entry.DescriptionRu,
                size,
                Included: true,
                ReasonRu: string.Empty,
                TruncatedNoteRu: truncated
                    ? $"показаны последние {entry.MaxLines} строк из {content.TotalLines}"
                    : string.Empty));
        }

        // Затем — предел. Выкидываем журналы (у них есть MaxLines > 0), от большого к малому.
        while (total > limitBytes)
        {
            int biggest = -1;
            for (int index = 0; index < includedEntries.Count; index++)
            {
                if (includedEntries[index].MaxLines <= 0)
                {
                    continue;
                }

                if (biggest < 0 || includedContents[index].Text.Length > includedContents[biggest].Text.Length)
                {
                    biggest = index;
                }
            }

            if (biggest < 0)
            {
                // Остался только готовый текст, и он сам больше предела: архива не будет.
                break;
            }

            SupportPackageEntry dropped = includedEntries[biggest];
            long droppedSize = Encoding.UTF8.GetByteCount(includedContents[biggest].Text);

            total -= droppedSize;
            includedEntries.RemoveAt(biggest);
            includedContents.RemoveAt(biggest);

            int itemIndex = items.FindIndex(item =>
                item.Included && string.Equals(item.EntryName, dropped.EntryName, StringComparison.Ordinal));
            if (itemIndex >= 0)
            {
                SupportPackageItem item = items[itemIndex];
                items[itemIndex] = item with
                {
                    Included = false,
                    ReasonRu = $"не влез в предел {limitBytes / (1024 * 1024)} МБ",
                    SizeBytes = 0,
                };
            }
        }

        return new SupportPackagePlan(targetPathRu, items, limitBytes, includedEntries, includedContents, total);
    }

    /// <summary>
    /// Собрать архив по готовому плану. По плану, а не по источникам заново: то, что человек видел
    /// на экране, — ровно то, что окажется в архиве.
    /// </summary>
    public static SupportPackageBuildResult Build(SupportPackagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.HasSomething)
        {
            return SupportPackageBuildResult.Failed("в пакет нечего положить: нет ни одного файла");
        }

        if (!plan.FitsLimit)
        {
            return SupportPackageBuildResult.Failed(
                $"пакет не влезает в предел {plan.LimitBytes / (1024 * 1024)} МБ даже без журналов");
        }

        if (string.IsNullOrWhiteSpace(plan.TargetPathRu))
        {
            return SupportPackageBuildResult.Failed("не задано имя файла пакета");
        }

        try
        {
            string? folder = Path.GetDirectoryName(plan.TargetPathRu);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // Собираем во временный файл рядом и подменяем прежний архив одним движением.
            // Раньше прежний пакет удалялся до сборки: сбой посреди работы (нет места, нет прав)
            // оставлял человека без старого пакета и с обрезанным новым.
            string temp = plan.TargetPathRu + TempSuffix;
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            try
            {
                using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    for (int index = 0; index < plan.IncludedEntries.Count; index++)
                    {
                        SupportPackageEntry entry = plan.IncludedEntries[index];
                        SupportPackageContent content = plan.IncludedContents[index];

                        ZipArchiveEntry zipEntry = archive.CreateEntry(entry.EntryName, CompressionLevel.Optimal);
                        using var stream = zipEntry.Open();
                        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        writer.Write(content.Text);
                    }
                }

                // overwrite: true — второй пакет под тем же именем заменяет первый, но только целиком.
                File.Move(temp, plan.TargetPathRu, overwrite: true);
            }
            catch
            {
                TryDeleteTemp(temp);
                throw;
            }

            return SupportPackageBuildResult.Ok(plan.TargetPathRu, plan.IncludedEntries.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return SupportPackageBuildResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Убрать недописанный архив. Молча: он никому не нужен, а прежний пакет цел.</summary>
    private static void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не убралось — беды нет: имя с хвостом .tmp ни на что не влияет.
        }
    }
}
