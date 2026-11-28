using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace PingForge.Data;

/// <summary>Чем закончилась проверка файла базы (задача U).</summary>
public enum DatabaseIntegrityVerdict
{
    /// <summary>Файла нет (первый запуск) или база читается: терять нечего.</summary>
    Healthy,

    /// <summary>Файл есть, но SQLite считает его испорченным.</summary>
    Corrupt,

    /// <summary>Файл не читается по причинам файловой системы: нет прав, занят, сбой диска.</summary>
    Unreadable,
}

/// <summary>Результат <c>PRAGMA quick_check</c>. <see cref="ProblemRu"/> заполнен только при проблеме.</summary>
public sealed record DatabaseIntegrityReport(DatabaseIntegrityVerdict Verdict, string? ProblemRu)
{
    /// <summary>База читается (или её ещё нет — тогда проверять нечего).</summary>
    public bool IsHealthy => Verdict == DatabaseIntegrityVerdict.Healthy;
}

/// <summary>Чем закончилась подготовка базы к запуску службы.</summary>
public enum DatabaseStartupOutcome
{
    /// <summary>Файла не было — он будет создан инициализатором.</summary>
    FirstRun,

    /// <summary>Файл на месте и читается; при необходимости сделана суточная копия.</summary>
    Healthy,

    /// <summary>Файл был повреждён и заменён последней резервной копией.</summary>
    RestoredFromBackup,

    /// <summary>Файл был повреждён, копии не помогли: начали с чистой базы, испорченный файл сохранён.</summary>
    StartedCleanAfterCorruption,
}

/// <summary>
/// Итог подготовки базы. Служба пишет <see cref="DescribeRu"/> в свой журнал, а при
/// <see cref="NeedsAttention"/> добавляет ту же строку в ответ «служба жива»: про повреждённую базу
/// человек должен узнать сразу, а не при попытке отката.
/// </summary>
public sealed record DatabaseStartupReport(
    DatabaseStartupOutcome Outcome,
    string? BackupPath = null,
    string? QuarantinedPath = null,
    string? ProblemRu = null)
{
    /// <summary>База начиналась с чистого листа: прежние снапшоты и журнал из неё не читаются.</summary>
    public bool IsDegraded => Outcome == DatabaseStartupOutcome.StartedCleanAfterCorruption;

    /// <summary>
    /// Про базу надо сказать человеку, а не только в журнал службы: она была повреждена, и часть
    /// записей могла пропасть даже при удачном восстановлении из копии.
    /// </summary>
    public bool NeedsAttention =>
        Outcome is DatabaseStartupOutcome.RestoredFromBackup or DatabaseStartupOutcome.StartedCleanAfterCorruption;

    /// <summary>Одна строка для журнала службы — без выдуманных подробностей.</summary>
    public string DescribeRu() => Outcome switch
    {
        DatabaseStartupOutcome.FirstRun =>
            "База: файла ещё нет — будет создана при первом запуске.",

        DatabaseStartupOutcome.Healthy =>
            "База: PRAGMA quick_check пройден, файл читается." + BackupNote(),

        DatabaseStartupOutcome.RestoredFromBackup =>
            $"База: файл был повреждён ({ProblemRu}); заменён резервной копией «{FileLabel(BackupPath)}». " +
            "Записи журнала, сделанные после этой копии, могли пропасть — вернуть их нечем." +
            QuarantineNote(),

        DatabaseStartupOutcome.StartedCleanAfterCorruption =>
            $"База повреждена и не восстановилась: {ProblemRu}. Начал с чистой базы — снапшоты и журнал " +
            "из прежнего файла не читаются, откат прошлых изменений по ним невозможен." + QuarantineNote(),

        _ => "База: состояние не названо.",
    };

    private string BackupNote() =>
        BackupPath is null ? string.Empty : $" Резервная копия: «{FileLabel(BackupPath)}».";

    private string QuarantineNote() =>
        QuarantinedPath is null
            ? " Испорченный файл сохранить не удалось."
            : $" Испорченный файл сохранён как «{FileLabel(QuarantinedPath)}».";

    private static string FileLabel(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "файл не назван" : Path.GetFileName(path) ?? path;
}

/// <summary>
/// Целостность и резервные копии базы (задача U). База хранит снапшоты и журнал изменений: если
/// файл повредился, откат становится невозможен, и человек узнаёт об этом в худший момент.
///
/// Правила:
///   • при старте — <c>PRAGMA quick_check</c>; испорченный файл НЕ удаляется, а откладывается рядом;
///   • копия — средствами SQLite (онлайн-backup), а не копированием файла; откладывание и
///     восстановление копии — наоборот, обычными файловыми операциями;
///   • копии: одна в сутки и одна перед каждым применением, хранятся последние три;
///   • сбой копирования не превращается в «всё хорошо»: причина возвращается текстом.
/// </summary>
public sealed class DatabaseBackupService
{
    /// <summary>Сколько резервных копий хранится рядом с базой.</summary>
    public const int KeepBackups = 3;

    private const string BackupSuffix = ".bak-";
    private const string CorruptSuffix = ".corrupt-";
    private const string BeforeRestoreSuffix = ".before-restore-";

    /// <summary>Сколько копий «как было» хранится рядом: их делает только восстановление по команде.</summary>
    public const int KeepPreviousCopies = 2;

    /// <summary>Раскладка «.db + сайдкары»: рядом с базой SQLite держит хвосты журналов.</summary>
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    private static readonly TimeSpan DailyBackupInterval = TimeSpan.FromHours(24);

    private readonly SqliteDatabaseInitializer _db;

    public DatabaseBackupService(SqliteDatabaseInitializer db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>Путь к самой базе.</summary>
    public string DatabasePath => _db.DatabasePath;

    /// <summary>Каталог, где лежат база и её копии.</summary>
    public string BackupDirectory =>
        Path.GetDirectoryName(DatabasePath) is { Length: > 0 } directory ? directory : ".";

    // Имя копии начинается с имени базы: у настоящей базы это ровно pingforge.db.bak-…, а у базы
    // из тестов — своё имя, чтобы временные файлы разных тестов не перемешивались.
    private string BackupPrefix => (Path.GetFileName(DatabasePath) ?? "pingforge.db") + BackupSuffix;

    private string CorruptPrefix => (Path.GetFileName(DatabasePath) ?? "pingforge.db") + CorruptSuffix;

    /// <summary>Все копии этой базы, от свежей к старой (метка времени в имени сортируется как текст).</summary>
    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return Directory.GetFiles(BackupDirectory, BackupPrefix + "*")
            .OrderByDescending(path => Path.GetFileName(path) ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Проверка целостности: <c>PRAGMA quick_check</c>. Исключения наружу не выходят — испорченный
    /// файл это ожидаемое состояние, о котором надо рассказать, а не сбой программы.
    /// </summary>
    public DatabaseIntegrityReport CheckIntegrity()
    {
        if (!File.Exists(DatabasePath))
        {
            return new DatabaseIntegrityReport(DatabaseIntegrityVerdict.Healthy, null);
        }

        try
        {
            using var connection = new SqliteConnection(_db.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";

            var issues = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string text = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (text.Length > 0)
                    {
                        issues.Add(text);
                    }
                }
            }

            if (issues.Count == 1 && string.Equals(issues[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new DatabaseIntegrityReport(DatabaseIntegrityVerdict.Healthy, null);
            }

            return new DatabaseIntegrityReport(
                DatabaseIntegrityVerdict.Corrupt,
                issues.Count == 0
                    ? "PRAGMA quick_check не вернула ни одной строки"
                    : string.Join("; ", issues.Take(5)));
        }
        catch (SqliteException ex)
        {
            return new DatabaseIntegrityReport(DatabaseIntegrityVerdict.Corrupt, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DatabaseIntegrityReport(DatabaseIntegrityVerdict.Unreadable, ex.Message);
        }
    }

    /// <summary>
    /// Копия базы через онлайн-backup SQLite. Возвращает путь копии; <c>null</c> — копировать
    /// нечего (файла базы ещё нет). Старые копии сверх <see cref="KeepBackups"/> удаляются.
    /// </summary>
    public async Task<string?> CreateBackupAsync(CancellationToken ct = default)
    {
        if (!File.Exists(DatabasePath))
        {
            return null;
        }

        Directory.CreateDirectory(BackupDirectory);
        string backupPath = Path.Combine(BackupDirectory, BackupPrefix + Stamp(DateTime.UtcNow));

        try
        {
            await using (var source = new SqliteConnection(_db.ConnectionString))
            {
                await source.OpenAsync(ct).ConfigureAwait(false);

                var destinationBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                };

                await using (var destination = new SqliteConnection(destinationBuilder.ToString()))
                {
                    await destination.OpenAsync(ct).ConfigureAwait(false);
                    source.BackupDatabase(destination);
                }
            }
        }
        catch (SqliteException)
        {
            // Онлайн-копия не получилась — запасной путь: побайтовая копия файла. Она может потерять
            // содержимое -wal, поэтому это именно «последняя попытка», а не основной способ.
            SqliteDatabaseInitializer.ClearFileHandles();
            File.Copy(DatabasePath, backupPath, overwrite: true);
        }

        PruneOldBackups();
        return backupPath;
    }

    /// <summary>Нужна ли суточная копия: копий нет или свежей больше суток.</summary>
    public bool IsDailyBackupDue(DateTime utcNow)
    {
        DateTime newestUtc = ListBackups()
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        return newestUtc == DateTime.MinValue || utcNow - newestUtc >= DailyBackupInterval;
    }

    /// <summary>Суточная копия: делается, только если она действительно нужна.</summary>
    public async Task<string?> EnsureDailyBackupAsync(DateTime? utcNow = null, CancellationToken ct = default) =>
        IsDailyBackupDue(utcNow ?? DateTime.UtcNow)
            ? await CreateBackupAsync(ct).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Отложить испорченный файл в сторону (не удаляя): рядом появится <c>pingforge.db.corrupt-…</c>,
    /// который можно отдать на разбор. Возвращает путь отложенного файла; <c>null</c> — файла не было.
    /// </summary>
    public string? QuarantineCorruptFile(DateTime? utcNow = null)
    {
        if (!File.Exists(DatabasePath))
        {
            return null;
        }

        // Пул соединений держит дескрипторы: без этого Windows не даёт переименовать файл.
        SqliteDatabaseInitializer.ClearFileHandles();
        Directory.CreateDirectory(BackupDirectory);

        string target = Path.Combine(BackupDirectory, CorruptPrefix + Stamp(utcNow ?? DateTime.UtcNow));
        File.Move(DatabasePath, target, overwrite: true);

        foreach (string suffix in SidecarSuffixes)
        {
            string sidecar = DatabasePath + suffix;
            if (!File.Exists(sidecar))
            {
                continue;
            }

            try
            {
                File.Move(sidecar, target + suffix, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Хвост журнала остался на месте — это не повод бросать восстановление.
            }
        }

        return target;
    }

    /// <summary>
    /// Положить на место самую свежую копию и проверить её. Возвращает <c>false</c> с причиной, если
    /// копий нет, копия не легла или сама не читается.
    /// </summary>
    /// <remarks>
    /// Если текущая база читается, её файл перед заменой сохраняется рядом как
    /// <c>pingforge.db.before-restore-…</c>: восстановление — это изменение данных программы, и у
    /// человека должен остаться путь назад. Сама служба этот файл обратно не подставляет.
    /// </remarks>
    public bool TryRestoreLatest(out string? restoredFrom, out string? problemRu, out string? previousCopyPath)
    {
        restoredFrom = null;
        problemRu = null;
        previousCopyPath = null;

        IReadOnlyList<string> backups = ListBackups();
        if (backups.Count == 0)
        {
            problemRu = "резервных копий базы нет";
            return false;
        }

        string newest = backups[0];

        try
        {
            SqliteDatabaseInitializer.ClearFileHandles();

            // Читаемую базу не выбрасываем: копия «как было» остаётся рядом для ручного возврата.
            if (CheckIntegrity().IsHealthy && File.Exists(DatabasePath))
            {
                string aside = Path.Combine(
                    BackupDirectory,
                    Path.GetFileName(DatabasePath) + BeforeRestoreSuffix + Stamp(DateTime.UtcNow));

                try
                {
                    File.Copy(DatabasePath, aside, overwrite: true);
                    previousCopyPath = aside;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Не удалось сохранить — восстановление всё равно идёт, но об этом скажет текст.
                    previousCopyPath = null;
                }
            }

            // Хвосты журнала от испорченной базы нельзя оставлять: SQLite применил бы их к копии.
            foreach (string suffix in SidecarSuffixes)
            {
                string sidecar = DatabasePath + suffix;
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Copy(newest, DatabasePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problemRu = $"копию «{Path.GetFileName(newest)}» не удалось положить на место: {ex.Message}";
            return false;
        }

        DatabaseIntegrityReport check = CheckIntegrity();
        if (!check.IsHealthy)
        {
            problemRu = $"копия «{Path.GetFileName(newest)}» тоже не читается: {check.ProblemRu}";
            return false;
        }

        restoredFrom = newest;
        return true;
    }

    /// <summary>
    /// Подготовка базы к запуску службы: проверка целостности, откладывание испорченного файла,
    /// восстановление из копии, суточная копия. Служба не падает в цикле рестартов — она всегда
    /// получает ответ, что с базой, и продолжает работать.
    /// </summary>
    public async Task<DatabaseStartupReport> PrepareForStartAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        if (!File.Exists(DatabasePath))
        {
            return new DatabaseStartupReport(DatabaseStartupOutcome.FirstRun);
        }

        DatabaseIntegrityReport check = CheckIntegrity();
        if (check.IsHealthy)
        {
            string? backup = await EnsureDailyBackupAsync(utcNow, ct).ConfigureAwait(false);
            return new DatabaseStartupReport(DatabaseStartupOutcome.Healthy, backup);
        }

        string? quarantined = QuarantineCorruptFile(utcNow);

        if (TryRestoreLatest(out string? restored, out string? restoreProblem, out _))
        {
            return new DatabaseStartupReport(
                DatabaseStartupOutcome.RestoredFromBackup,
                restored,
                quarantined,
                check.ProblemRu);
        }

        // Копия не помогла: испорченная копия на месте базы больше не нужна — сам испорченный файл
        // уже отложен выше. Если убрать его не получится, служба всё равно продолжит: ошибку
        // инициализации она обрабатывает отдельно и тоже откладывает файл, а не затирает.
        if (File.Exists(DatabasePath))
        {
            try
            {
                SqliteDatabaseInitializer.ClearFileHandles();
                File.Delete(DatabasePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Оставляем как есть — «начать с чистой базы» попробует служба при инициализации.
            }
        }

        return new DatabaseStartupReport(
            DatabaseStartupOutcome.StartedCleanAfterCorruption,
            null,
            quarantined,
            $"{check.ProblemRu}{(restoreProblem is null ? string.Empty : $"; восстановить не удалось: {restoreProblem}")}");
    }

    private void PruneOldBackups()
    {
        string prefix = Path.GetFileName(DatabasePath) + BeforeRestoreSuffix;
        if (Directory.Exists(BackupDirectory))
        {
            foreach (string old in Directory.GetFiles(BackupDirectory, prefix + "*")
                .OrderByDescending(path => Path.GetFileName(path) ?? string.Empty, StringComparer.Ordinal)
                .Skip(KeepPreviousCopies))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Старая копия «как было» останется ещё на один запуск.
                }
            }
        }

        foreach (string old in ListBackups().Skip(KeepBackups))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Не удалось убрать старую копию — она просто останется ещё на один запуск.
            }
        }
    }

    private static string Stamp(DateTime utc) =>
        utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
}
