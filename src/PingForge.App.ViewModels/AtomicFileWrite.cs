using System.Text;

namespace PingForge.App.ViewModels;

/// <summary>
/// Запись файла «целиком или никак»: текст пишется во временный файл рядом и подменяет целевой
/// одним движением. Сбой посреди записи (диск заполнился, флешку вынули) оставляет прошлую копию
/// целой, а не половину новой.
/// </summary>
/// <remarks>
/// Одно место на все выгрузки программы: экспорт настроек и CSV журнала ведут себя одинаково,
/// и правило про подмену не приходится держать в голове дважды.
/// </remarks>
public static class AtomicFileWrite
{
    /// <summary>Хвост временного файла: он лежит рядом с целевым и не остаётся после сбоя.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Записать текст атомарно. <paramref name="overwrite"/> = false — не затирать уже существующий
    /// файл: два экспорта с одним именем не должны съедать друг друга молча.
    /// </summary>
    /// <exception cref="IOException">Подмена не удалась — прежний файл остался как был.</exception>
    /// <exception cref="UnauthorizedAccessException">Писать некуда или нечем.</exception>
    public static async Task WriteAllTextAsync(
        string path,
        string text,
        Encoding? encoding = null,
        bool overwrite = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string temp = path + TempSuffix;
        await File.WriteAllTextAsync(temp, text, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(true);

        try
        {
            File.Move(temp, path, overwrite);
        }
        catch
        {
            // Недописанный временный файл человеку не нужен: убираем, чтобы рядом с копией не рос мусор.
            TryDeleteTemp(path);
            throw;
        }
    }

    /// <summary>Убрать недописанный временный файл. Молча: он ни на что не влияет, а мешать уборке нечем.</summary>
    public static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path + TempSuffix))
            {
                File.Delete(path + TempSuffix);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не убралось — беды нет: имя с хвостом .tmp ни на что не влияет, а целевой файл цел.
        }
    }
}
