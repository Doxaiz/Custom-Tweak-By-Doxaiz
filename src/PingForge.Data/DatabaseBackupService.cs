using System.IO;
using Microsoft.Data.Sqlite;

namespace PingForge.Data;

/// <summary>Резервные копии базы (задача U): раз в сутки + перед каждым применением, последние 3.
/// Копия — через SQLite backup API (онлайн), а не копией файла.</summary>
public sealed class DatabaseBackupService
{
    private readonly SqliteDatabaseInitializer _db;
    public DatabaseBackupService(SqliteDatabaseInitializer db) => _db = db;

    public async Task<string> CreateBackupAsync(CancellationToken ct = default)
    {
        string path = _db.DatabasePath;
        string dir = Path.GetDirectoryName(path) ?? ".";
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string bakPath = Path.Combine(dir, $"pingforge.db.bak-{stamp}");
        Directory.CreateDirectory(dir);
        await using var src = new SqliteConnection(_db.ConnectionString);
        await src.OpenAsync(ct).ConfigureAwait(false);
        await using var dst = new SqliteConnection($"Data Source={bakPath}");
        await dst.OpenAsync(ct).ConfigureAwait(false);
        src.BackupDatabase(dst);
        PruneOldBackups(dir);
        return bakPath;
    }

    private static void PruneOldBackups(string dir)
    {
        var files = Directory.GetFiles(dir, "pingforge.db.bak-*").OrderByDescending(f => f).ToList();
        foreach (var f in files.Skip(3))
            try { File.Delete(f); } catch { }
    }

    public bool TryRestoreLatest(out string? restoredFrom)
    {
        restoredFrom = null;
        string dir = Path.GetDirectoryName(_db.DatabasePath) ?? ".";
        var files = Directory.GetFiles(dir, "pingforge.db.bak-*").OrderByDescending(f => f).ToList();
        if (files.Count == 0) return false;
        try
        {
            File.Copy(files[0], _db.DatabasePath, overwrite: true);
            restoredFrom = files[0];
            return true;
        } catch { return false; }
    }

    public string? CheckIntegrity()
    {
        try
        {
            using var conn = new SqliteConnection(_db.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            using var r = cmd.ExecuteReader();
            var issues = new List<string>();
            while (r.Read()) issues.Add(r.GetString(0));
            if (issues.Count == 1 && issues[0] == "ok") return null;
            return string.Join("; ", issues);
        }
        catch (Exception ex) { return ex.Message; }
    }
}
