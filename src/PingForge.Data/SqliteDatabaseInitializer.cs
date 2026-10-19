using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PingForge.Data;

/// <summary>
/// Открывает соединения с правильными прагмами и применяет единственный источник DDL —
/// встроенный ресурс Schema\pingforge_schema.sql (никаких копий схемы в C#-коде).
/// </summary>
public sealed class SqliteDatabaseInitializer
{
    // v4: в rollback_status добавлен статус RequiresManualVerification (твик исчез из каталога).
    // v5: сессии оптимизации получили итог, действия, пометку откатa и честный статус незавершённой.
    public const int SchemaVersion = 5;

    /// <summary>
    /// Колонки, которых не было в схеме v4. База человека обновляется миграцией, а не удалением:
    /// история сессий и журнал изменений должны пережить обновление программы.
    /// </summary>
    private static readonly (string Name, string Ddl)[] SessionColumnsV5 =
    [
        ("game_title_ru", "ALTER TABLE route_optimization_sessions ADD COLUMN game_title_ru TEXT NOT NULL DEFAULT ''"),
        ("applied_actions_json", "ALTER TABLE route_optimization_sessions ADD COLUMN applied_actions_json TEXT NOT NULL DEFAULT '[]'"),
        ("rollback_state", "ALTER TABLE route_optimization_sessions ADD COLUMN rollback_state TEXT NOT NULL DEFAULT 'Unknown'"),
        ("outcome", "ALTER TABLE route_optimization_sessions ADD COLUMN outcome TEXT NOT NULL DEFAULT 'NotFinished'"),
        ("is_completed", "ALTER TABLE route_optimization_sessions ADD COLUMN is_completed INTEGER NOT NULL DEFAULT 0"),
    ];

    private const string SchemaResourceName = "PingForge.Data.Schema.pingforge_schema.sql";

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public SqliteDatabaseInitializer(string? customFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(customFilePath))
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PingForge");
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "pingforge.db");
        }
        else
        {
            string? directory = Path.GetDirectoryName(customFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            DatabasePath = customFilePath;
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // ForeignKeys — прагма уровня соединения: без неё ON DELETE CASCADE молча не работает.
            ForeignKeys = true,

            // Cache = Shared в сочетании с WAL даёт SQLITE_LOCKED при параллельной записи.
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    /// <summary>
    /// Отпускает файлы базы: закрывает пул соединений.
    /// </summary>
    /// <remarks>
    /// Пул держит дескрипторы файлов даже после того, как соединение закрыто. Пока они живы, Windows
    /// не даёт ни удалить файл базы, ни переименовать, ни заменить его — Linux таких ограничений не
    /// накладывает, поэтому ошибка видна только на Windows. Вызывать перед удалением, копированием,
    /// заменой или архивацией файла базы.
    /// </remarks>
    public static void ClearFileHandles() => SqliteConnection.ClearAllPools();

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            """;
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        string ddl = ReadEmbeddedSchema();

        await using SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = ddl;
            await schemaCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ApplySessionMigrationsAsync(connection, ct).ConfigureAwait(false);

        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(1) FROM schema_version_history WHERE version_number = $v;";
        checkCommand.Parameters.AddWithValue("$v", SchemaVersion);

        long existing = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        if (existing > 0)
        {
            return;
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO schema_version_history (version_number, installed_utc, notes)
            VALUES ($v, $ts, 'Схема v5: сессии оптимизации — итог, действия, пометка откатa и честный статус незавершённой сессии');
            """;
        insertCommand.Parameters.AddWithValue("$v", SchemaVersion);
        insertCommand.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Миграция v5 поверх существующей базы. Проверяются настоящие колонки таблицы
    /// (<c>pragma_table_info</c>), а не только номер версии: база могла быть создана частично
    /// обновлённой сборкой, и тогда «версия записана» ещё не значит «колонки есть».
    /// </summary>
    private static async Task ApplySessionMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = "SELECT name FROM pragma_table_info('route_optimization_sessions');";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach ((string name, string ddl) in SessionColumnsV5)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            await using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<int> GetSchemaVersions()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version_number FROM schema_version_history ORDER BY version_number;";
        using var reader = command.ExecuteReader();

        var versions = new List<int>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static string ReadEmbeddedSchema()
    {
        Assembly assembly = typeof(SqliteDatabaseInitializer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"Встроенный ресурс схемы «{SchemaResourceName}» не найден.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
