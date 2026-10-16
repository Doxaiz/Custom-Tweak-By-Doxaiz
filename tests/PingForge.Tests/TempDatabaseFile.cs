using PingForge.Data;

namespace PingForge.Tests;

/// <summary>
/// Временный файл базы для тестов: выдаёт уникальный путь и убирает за собой не только сам файл,
/// но и побочные файлы SQLite (-wal, -shm, -journal).
/// </summary>
/// <remarks>
/// Перед удалением обязательно закрывается пул соединений: пул держит дескрипторы файлов даже после
/// того, как соединение закрыто. На Linux занятый файл всё равно удаляется, поэтому ошибка незаметна,
/// а на Windows удаление падает с «файл используется другим процессом» — именно на этом упала первая
/// сборка на GitHub. Ошибка удаления не глушится: если файл не убрался, тест должен сказать об этом,
/// а не оставить мусор в temp и промолчать.
/// </remarks>
internal static class TempDatabaseFile
{
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    public static string Create(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.db");

    public static void Delete(string path)
    {
        SqliteDatabaseInitializer.ClearFileHandles();

        DeleteWithRetry(path);

        foreach (string suffix in SidecarSuffixes)
        {
            DeleteWithRetry(path + suffix);
        }
    }

    private static void DeleteWithRetry(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 3)
            {
                // Windows иногда отпускает файл на доли секунды позже: индексатор, антивирус.
                Thread.Sleep(150);
            }
        }
    }
}
