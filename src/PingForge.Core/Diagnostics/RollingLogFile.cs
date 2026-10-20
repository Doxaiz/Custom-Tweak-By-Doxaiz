using System.Text;

namespace PingForge.Core.Diagnostics;

/// <summary>
/// Файл журнала с поворотом: текущий файл плюс копии .1, .2 … Старая копия удаляется,
/// текущая никогда не обрезается посреди строки — поворот идёт до записи.
/// </summary>
public sealed class RollingLogFile
{
    /// <summary>Сколько байт держим в текущем файле по умолчанию (5 МБ).</summary>
    public const long DefaultMaxBytes = 5L * 1024 * 1024;

    /// <summary>Сколько файлов держим по умолчанию: текущий и две копии.</summary>
    public const int DefaultMaxFiles = 3;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly Encoding _encoding;

    /// <param name="path">Полный путь к текущему файлу журнала.</param>
    /// <param name="maxBytes">Предел размера текущего файла; проверяется перед каждой записью.</param>
    /// <param name="maxFiles">Сколько файлов держим всего, включая текущий (не меньше двух).</param>
    /// <param name="writeByteOrderMark">Писать метку UTF-8 в начале нового файла (окно так делает, служба — нет).</param>
    public RollingLogFile(
        string path,
        long maxBytes = DefaultMaxBytes,
        int maxFiles = DefaultMaxFiles,
        bool writeByteOrderMark = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Путь к журналу пуст.", nameof(path));
        }

        if (maxBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                maxBytes,
                "Предел меньше килобайта: журнал будет поворачиваться на каждой строке.");
        }

        if (maxFiles < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFiles),
                maxFiles,
                "Меньше двух файлов — значит ротации нет: нужен текущий файл и хотя бы одна копия.");
        }

        FilePath = path;
        MaxBytes = maxBytes;
        MaxFiles = maxFiles;
        _path = path;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: writeByteOrderMark);
    }

    /// <summary>Путь к текущему файлу журнала.</summary>
    public string FilePath { get; }

    /// <summary>Предел размера текущего файла в байтах.</summary>
    public long MaxBytes { get; }

    /// <summary>Сколько файлов держим всего, включая текущий.</summary>
    public int MaxFiles { get; }

    /// <summary>
    /// Путь архивной копии: <c>1</c> — самая свежая, дальше — старее.
    /// </summary>
    public string ArchivePath(int number) => number <= 0
        ? throw new ArgumentOutOfRangeException(nameof(number), number, "Номера копий начинаются с единицы.")
        : $"{_path}.{number}";

    /// <summary>
    /// Дописать строку. Перевод строки добавляется здесь; строка пишется целиком, без обрезки.
    /// </summary>
    public void Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        lock (_gate)
        {
            RotateIfNeeded();

            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, _encoding);
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Повернуть файл, если он дорос до предела. Неудача поворота не мешает записи: журнал важнее порядка.
    /// </summary>
    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= _maxBytes)
            {
                return;
            }

            // Самый старый номер, который ещё держим: maxFiles − 1. Его удаляем, остальные сдвигаем.
            int last = _maxFiles - 1;
            string oldest = $"{_path}.{last}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int number = last - 1; number >= 1; number--)
            {
                string from = $"{_path}.{number}";
                if (File.Exists(from))
                {
                    File.Move(from, $"{_path}.{number + 1}", overwrite: true);
                }
            }

            File.Move(_path, $"{_path}.1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл занят или папка только для чтения. Дописываем в текущий: одна лишняя строка
            // сверх предела — меньшая беда, чем потерянная строка.
        }
    }
}
