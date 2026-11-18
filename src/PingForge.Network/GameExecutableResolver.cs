using System.Text;

namespace PingForge.Network;

/// <summary>
/// Как из выбранного человеком файла получить .exe игры. С рабочего стола люди чаще берут ярлык,
/// а не сам исполняемый файл, и требование «только .exe» заставляло искать игру вручную по папкам.
/// </summary>
/// <remarks>
/// Разбор нарочно без Windows-вызовов: ярлык — это файл, в котором записан путь цели, и путь
/// читается прямо из байтов. Так правило проверяется тестами на любой машине, а не только на Windows.
/// Обещаний сверх прочитанного нет: если в ярлыке нет пути к .exe или этого файла нет на диске,
/// программа говорит об этом словами, а не добавляет игру «наугад».
/// </remarks>
public static class GameExecutableResolver
{
    /// <summary>Хвост, по которому узнаётся исполняемый файл.</summary>
    private const string ExeSuffix = ".exe";

    /// <summary>
    /// Привести выбранный файл к .exe игры. <paramref name="fileExists"/> — проверка диска:
    /// тесты подставляют свою, программа — настоящую.
    /// </summary>
    public static bool TryResolve(
        string? pickedPath,
        Func<string, bool> fileExists,
        out string executablePath,
        out string noteRu,
        out string errorRu)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        executablePath = string.Empty;
        noteRu = string.Empty;
        errorRu = string.Empty;

        if (string.IsNullOrWhiteSpace(pickedPath))
        {
            errorRu = "Файл игры не выбран. Нажмите «Добавить игру» и укажите файл игры, её ярлык с рабочего стола или ссылку.";
            return false;
        }

        string path = pickedPath.Trim().Trim('"');
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (string.Equals(extension, ExeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            executablePath = path;
            return true;
        }

        if (extension == ".lnk")
        {
            return ResolveShortcut(path, fileExists, out executablePath, out noteRu, out errorRu);
        }

        if (extension == ".url")
        {
            return ResolveInternetShortcut(path, out executablePath, out noteRu, out errorRu);
        }

        return Refuse(path, extension, out executablePath, out noteRu, out errorRu);
    }

    /// <summary>Пути .exe, записанные внутри ярлыка Windows. Порядок — как в файле: сначала цель, потом прочее.</summary>
    public static IReadOnlyList<string> ShortcutCandidates(ReadOnlySpan<byte> shortcutBytes)
    {
        var found = new List<string>();

        // В ярлыке путь лежит дважды: как ANSI-строка и как UTF-16. Читаем оба вида — так ярлык
        // на игру в папке с русским названием тоже разбирается.
        foreach (string candidate in SplitAnsiStrings(shortcutBytes))
        {
            Add(candidate);
        }

        foreach (string candidate in SplitUnicodeStrings(shortcutBytes))
        {
            Add(candidate);
        }

        return found;

        void Add(string raw)
        {
            string text = raw.Trim().Trim('"').Trim();

            // В ярлыке путь — это путь, а не имя: «C:\Игры\Моя.exe», «\\сервер\общая\игра.exe»
            // или, если файл разбирают на другой системе, «/games/game.exe». Просто «game.exe»
            // целью не считаем: такой строкой ярлык не запускает программу.
            int drive = text.IndexOf(":\\", StringComparison.Ordinal);
            string path = drive >= 1 ? text[(drive - 1)..] : text;
            if (!LooksLikeExePath(path) || (!path.Contains('\\') && !path.Contains('/')))
            {
                return;
            }

            if (!found.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }
    }

    /// <summary>
    /// Выбрать из кандидатов тот, что существует, и объяснить выбор. Два живых кандидата — не повод
    /// гадать молча: предпочитаем совпадающий по имени с ярлыком, иначе первый, и говорим об этом.
    /// </summary>
    public static bool ChooseExisting(
        IReadOnlyList<string> candidates,
        string shortcutPath,
        Func<string, bool> fileExists,
        out string executablePath,
        out string noteRu,
        out string errorRu)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(fileExists);

        executablePath = string.Empty;
        noteRu = string.Empty;
        errorRu = string.Empty;

        List<string> alive = candidates.Where(candidate => !IsUninstaller(candidate) && SafeExists(candidate)).ToList();
        if (alive.Count == 0)
        {
            string first = candidates.FirstOrDefault() ?? string.Empty;
            errorRu = first.Length == 0
                ? "В ярлыке не записан путь к программе игры. Укажите сам .exe игры: правый щелчок по ярлыку → «Расположение файла»."
                : $"Ярлык ведёт на «{first}», но такого файла на диске нет: игра перемещена или удалена. Укажите сам .exe игры.";
            return false;
        }

        string shortcutName = Path.GetFileNameWithoutExtension(shortcutPath);
        string? byName = alive.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileNameWithoutExtension(candidate), shortcutName, StringComparison.OrdinalIgnoreCase));
        executablePath = byName ?? alive[0];
        noteRu = alive.Count > 1
            ? $"В ярлыке несколько программ; выбрана «{Path.GetFileName(executablePath)}». Если игра запускается другой — укажите её .exe."
            : $"Ярлык ведёт на «{Path.GetFileName(executablePath)}».";
        return true;

        bool SafeExists(string candidate)
        {
            try
            {
                return fileExists(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    private static bool ResolveShortcut(
        string path,
        Func<string, bool> fileExists,
        out string executablePath,
        out string noteRu,
        out string errorRu)
    {
        executablePath = string.Empty;
        noteRu = string.Empty;
        errorRu = string.Empty;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorRu = $"Ярлык «{path}» не прочитался: {ex.Message}. Укажите сам .exe игры.";
            return false;
        }

        IReadOnlyList<string> candidates = ShortcutCandidates(bytes);
        return ChooseExisting(candidates, path, fileExists, out executablePath, out noteRu, out errorRu);
    }

    private static bool ResolveInternetShortcut(
        string path,
        out string executablePath,
        out string noteRu,
        out string errorRu)
    {
        executablePath = string.Empty;
        noteRu = string.Empty;
        errorRu = string.Empty;

        string? target = null;
        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    target = line[4..].Trim();
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorRu = $"Ссылка «{path}» не прочиталась: {ex.Message}. Укажите сам .exe игры.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            errorRu = "В файле ссылки нет адреса. Укажите сам .exe игры или её ярлык.";
            return false;
        }

        string local = target;
        if (local.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            local = Uri.UnescapeDataString(local[8..]).Replace('/', '\\');
        }

        if (!LooksLikeExePath(local))
        {
            errorRu = $"Ссылка ведёт не на файл игры, а на «{target}». Укажите сам .exe игры или её ярлык с рабочего стола.";
            return false;
        }

        executablePath = local;
        noteRu = $"Ссылка ведёт на «{Path.GetFileName(local)}».";
        return true;
    }

    private static bool Refuse(
        string path,
        string extension,
        out string executablePath,
        out string noteRu,
        out string errorRu)
    {
        executablePath = string.Empty;
        noteRu = string.Empty;
        string name = Path.GetFileName(path);
        errorRu = extension.Length == 0
            ? $"«{name}» — не программа и не ярлык. Игра запускается .exe; ярлык с рабочего стола подойдёт — программа найдёт .exe по нему."
            : $"«{name}» ({extension}) — не программа и не ярлык. Игра запускается .exe; ярлык с рабочего стола подойдёт — программа найдёт .exe по нему.";
        return false;
    }

    private static bool LooksLikeExePath(string text) =>
        text.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase) &&
        !text.Contains('\n') &&
        !text.Contains('\r') &&
        text.Length > ExeSuffix.Length;

    private static bool IsUninstaller(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("unwise", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Строки с завершающим нулём в однобайтовой части ярлыка.</summary>
    private static IEnumerable<string> SplitAnsiStrings(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder();
        foreach (byte value in bytes)
        {
            if (value == 0)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            if (value < 0x20 || value > 0x7E)
            {
                builder.Clear();
                continue;
            }

            builder.Append((char)value);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>Строки UTF-16LE: так Windows хранит путь, если в нём есть кириллица.</summary>
    private static IEnumerable<string> SplitUnicodeStrings(ReadOnlySpan<byte> bytes)
    {
        for (int shift = 0; shift < 2; shift++)
        {
            var builder = new StringBuilder();
            for (int index = shift; index + 1 < bytes.Length; index += 2)
            {
                char symbol = (char)(bytes[index] | (bytes[index + 1] << 8));
                if (symbol == '\0')
                {
                    if (builder.Length > 0)
                    {
                        yield return builder.ToString();
                        builder.Clear();
                    }

                    continue;
                }

                if (char.IsControl(symbol) || !IsPrintable(symbol))
                {
                    builder.Clear();
                    continue;
                }

                builder.Append(symbol);
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
            }
        }

        static bool IsPrintable(char symbol) =>
            char.IsLetterOrDigit(symbol) || symbol is ':' or '\\' or '/' or '.' or '_' or '-' or ' ' or '(' or ')' or '~' or '\'' or '&';
    }
}
