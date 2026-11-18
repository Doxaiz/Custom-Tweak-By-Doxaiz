using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using PingForge.App.ViewModels;
using PingForge.Network;
using Microsoft.Win32;

namespace PingForge.App;

/// <summary>
/// Поиск установленных игр на диске.
///
/// Что делает: читает библиотеки Steam (файл <c>steamapps\libraryfolders.vdf</c> — обычный текст),
/// обходит <c>steamapps\common</c> и несколько привычных папок, и ищет файлы с именами известных игр.
/// Чего не делает: не читает реестр целиком, не лазит по всему диску и не называет игрой первый
/// попавшийся .exe. Поиск ограничен по числу папок и по времени — «ищу игры» не должно превращаться
/// в «программа зависла на минуту».
/// </summary>
public sealed partial class WindowsGameLocator : IGameExecutableProbe
{
    private const int MaxDirectories = 6000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    public IReadOnlyDictionary<string, string> FindInstalled(IReadOnlyCollection<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var wanted = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        DateTime deadline = DateTime.UtcNow + Budget;

        foreach (string root in LibraryRoots())
        {
            if (found.Count == wanted.Count || DateTime.UtcNow > deadline || visited > MaxDirectories)
            {
                break;
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            Walk(root, depth: 0, wanted, found, ref visited, deadline);
        }

        return found;
    }

    /// <summary>Где вообще бывают игры. Steam — по своим библиотекам, остальное — обычные папки.</summary>
    private static IEnumerable<string> LibraryRoots()
    {
        var roots = new List<string>();

        foreach (string steam in SteamRoots())
        {
            string common = Path.Combine(steam, "steamapps", "common");
            if (Directory.Exists(common))
            {
                roots.Add(common);
            }

            // Все дополнительные библиотеки: "path" "D:\\SteamLibrary" и т.п.
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                continue;
            }

            try
            {
                foreach (Match match in PathInVdf().Matches(File.ReadAllText(vdf)))
                {
                    string path = match.Groups[1].Value.Replace(@"\\", @"\");
                    string libraryCommon = Path.Combine(path, "steamapps", "common");
                    if (Directory.Exists(libraryCommon))
                    {
                        roots.Add(libraryCommon);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Библиотеку не прочитать — не беда: остальные пути проверяются дальше.
            }
        }

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string? baseDirectory in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(baseDirectory))
            {
                continue;
            }

            roots.Add(Path.Combine(baseDirectory, "Epic Games"));
            roots.Add(Path.Combine(baseDirectory, "Riot Games"));
            roots.Add(Path.Combine(baseDirectory, "Steam", "steamapps", "common"));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (!string.IsNullOrEmpty(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Steam");
        }

        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "Steam");
        }
    }

    private static void Walk(
        string directory,
        int depth,
        HashSet<string> wanted,
        Dictionary<string, string> found,
        ref int visited,
        DateTime deadline)
    {
        if (depth > 3 || DateTime.UtcNow > deadline || visited > MaxDirectories || found.Count == wanted.Count)
        {
            return;
        }

        visited++;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (wanted.Contains(name) && !found.ContainsKey(name))
                {
                    found[name] = file;
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                Walk(child, depth + 1, wanted, found, ref visited, deadline);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папку не прочитать — пропускаем: это обычное дело, а не ошибка программы.
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathInVdf();
}

/// <summary>Запуск игры и проверка «уже идёт». Отдельно от модели, чтобы модель проверялась тестами.</summary>
public sealed class ProcessGameStarter : IGameStarter
{
    public bool TryStart(string executablePath, out string errorRu)
    {
        errorRu = string.Empty;

        try
        {
            if (!File.Exists(executablePath))
            {
                errorRu = $"файла нет: {executablePath}. Если игру удалили или перенесли — укажите путь заново.";
                return false;
            }

            using Process? process = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            });

            if (process is null)
            {
                errorRu = "Windows не дала запустить файл — обычно это значит, что нужны права администратора.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            errorRu = ex.Message;
            return false;
        }
    }

    public bool IsRunning(string processName)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(processName);
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Выбор файла игры руками: обычное окно Windows. Отдельный порт — чтобы модель не зависела от окон.
/// </summary>
/// <remarks>
/// В окне видны не только .exe: с рабочего стола люди берут ярлык, а иногда — файл ссылки. Разбор
/// таких файлов делает <see cref="GameExecutableResolver"/>: диалог лишь не мешает их выбрать.
/// Открывается папка рабочего стола: там чаще всего и лежит то, что человек хочет указать.
/// </remarks>
public sealed class OpenFileExecutablePicker : IExecutablePicker
{
    public string? Pick(string titleRu)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Custom Tweak By Doxaiz — выберите файл игры: {titleRu}",
            Filter = "Программы и ярлыки (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            FilterIndex = 1,
            CheckFileExists = true,
        };

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
        {
            dialog.InitialDirectory = desktop;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
