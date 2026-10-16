using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace PingForge.App;

/// <summary>
/// Журнал запуска. Пишет по строке на каждую ступень старта — с временем и числом миллисекунд
/// от запуска процесса.
///
/// Зачем он нужен: окно программы не запускалось до этого ни на одной машине, и «запустил — ничего
/// не произошло» было неотличимо от «программа упала молча» и от «программа ещё распаковывается».
/// Журнал делает разницу видимой: он появляется раньше окна, а время от старта процесса до первой
/// строки показывает, сколько ушло на распаковку одиночного .exe.
///
/// Файл лежит в папке данных пользователя: писать туда можно всегда, в отличие от папки общих
/// данных службы, куда обычный пользователь может не иметь доступа.
/// </summary>
public static class StartupLog
{
    private const long MaxBytes = 512 * 1024;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static readonly object Gate = new();
    private static readonly DateTime StartedAt = DateTime.Now;

    private static string? _path;
    private static string? _pathProblem;
    private static TextWriter? _console;

    /// <summary>Путь к журналу. Если записать не удалось никуда — пустая строка, и об этом сказано в ProblemRu.</summary>
    public static string Path
    {
        get
        {
            if (_path is null)
            {
                _path = ChoosePath(out _pathProblem);
            }

            return _path;
        }
    }

    /// <summary>Почему журнал недоступен (если недоступен). Пусто — журнал пишется.</summary>
    public static string? ProblemRu => _pathProblem;

    /// <summary>Включить дублирование журнала в консоль: нужно для самопроверки на сборочной машине.</summary>
    public static void EnableConsoleOutput()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return;
            }

            var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(writer);
            _console = writer;
        }
        catch (Exception)
        {
            // Консоли может не быть вовсе (запуск двойным щелчком) — это не ошибка.
        }
    }

    public static void Note(string messageRu) => Write("  ", messageRu);

    public static void Step(string messageRu) => Write("→ ", messageRu);

    public static void Warn(string messageRu) => Write("! ", messageRu);

    public static void Problem(string whatRu, Exception ex) =>
        Write("!!", $"{whatRu}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");

    /// <summary>Сколько времени прошло от запуска процесса (включая распаковку одиночного .exe).</summary>
    public static long ElapsedMs => (long)(DateTime.Now - StartedAt).TotalMilliseconds;

    /// <summary>Как приложение запущено: путь, режим, версия, разрядность и права. Всё это нужно при разборе жалобы.</summary>
    public static void WriteHeader(string versionRu, bool selfCheck, bool quiet, bool isAdministrator)
    {
        Step($"Custom Tweak By Doxaiz {versionRu} — запуск");
        Note($"Время от запуска процесса до этой строки: {ElapsedMs} мс (в него входит распаковка одиночного .exe)");
        Note($"Файл программы: {Environment.ProcessPath ?? "неизвестно"}");
        Note($"Режим: {(selfCheck ? "самопроверка (--self-check)" : quiet ? "без диалогов (--quiet)" : "обычный")}");
        Note($"Windows: {Environment.OSVersion.VersionString}, 64-разрядная: {Environment.Is64BitOperatingSystem}, процессов: {Environment.ProcessorCount}");
        Note($"Права администратора: {(isAdministrator ? "есть" : "нет")} — это нормально: систему меняет служба, а не окно");
        Note($"Журнал: {Path}{(ProblemRu is null ? string.Empty : $" (недоступен: {ProblemRu})")}");
    }

    private static void Write(string prefix, string messageRu)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff} +{ElapsedMs,6} мс] {prefix}{messageRu}";

        lock (Gate)
        {
            try
            {
                string path = Path;
                if (path.Length > 0)
                {
                    RotateIfHuge(path);
                    string text = File.Exists(path) ? Environment.NewLine + line : line;
                    File.AppendAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                }
                else
                {
                    _pathProblem ??= "журнал недоступен: ни одна папка не приняла запись";
                }
            }
            catch (Exception ex)
            {
                // Журнал не должен мешать программе работать: если записать не вышло — молча запоминаем.
                _pathProblem = $"{ex.GetType().Name}: {ex.Message}";
            }

            try
            {
                _console?.WriteLine(line);
            }
            catch (Exception)
            {
                // Консоль могла закрыться — не наша забота.
            }
        }
    }

    private static void RotateIfHuge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }
        }
        catch (Exception)
        {
            // Не смогли повернуть файл — не беда, допишем в текущий.
        }
    }

    /// <summary>
    /// Куда писать журнал. Первым идёт папка данных пользователя, вторым — временная,
    /// третьим — папка рядом с программой (она может быть только для чтения — это последний шанс).
    /// Куда запись не прошла — не берём: «журнал есть, но пустой» хуже, чем честное «журнала нет».
    /// </summary>
    private static string ChoosePath(out string? problem)
    {
        problem = null;

        var candidates = new List<string>();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(System.IO.Path.Combine(localAppData, "CustomTweakByDoxaiz", "app-start.log"));
        }

        string temp = System.IO.Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(temp))
        {
            candidates.Add(System.IO.Path.Combine(temp, "CustomTweakByDoxaiz-app-start.log"));
        }

        string? exeFolder = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeFolder))
        {
            candidates.Add(System.IO.Path.Combine(exeFolder, "app-start.log"));
        }

        var problems = new List<string>();
        foreach (string candidate in candidates)
        {
            try
            {
                string? folder = System.IO.Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.AppendAllText(candidate, string.Empty);
                return candidate;
            }
            catch (Exception ex)
            {
                problems.Add($"{candidate} — {ex.GetType().Name}");
            }
        }

        problem = string.Join("; ", problems);
        return string.Empty;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}

/// <summary>
/// Перехватчик необработанных ошибок. Без него .NET просто завершает процесс, и человек видит
/// ровно то, на что жаловался пользователь: «нажал — и ничего». Теперь ошибка попадает в журнал,
/// а при обычном запуске ещё и в окно с текстом.
/// </summary>
public static class CrashGuard
{
    /// <summary>Диалоги показывать нельзя: самопроверка на сборочной машине и режим --quiet.</summary>
    public static bool Silent { get; set; }

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Report("Ошибка в работе программы", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Report("Ошибка при запуске программы", ex);
            }
            else
            {
                StartupLog.Warn($"Необработанная ошибка без подробностей: {e.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("Ошибка в фоновой задаче", e.Exception);
            e.SetObserved();
        };
    }

    public static void Report(string whatRu, Exception ex)
    {
        StartupLog.Problem(whatRu, ex);

        if (Silent)
        {
            return;
        }

        try
        {
            string logLine = StartupLog.Path.Length > 0
                ? $"Подробности записаны в файл:{Environment.NewLine}{StartupLog.Path}"
                : $"Журнал записать не удалось: {StartupLog.ProblemRu}";

            MessageBox.Show(
                $"Программа столкнулась с ошибкой.{Environment.NewLine}{Environment.NewLine}" +
                $"{whatRu}: {ex.GetType().Name}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"{logLine}{Environment.NewLine}{Environment.NewLine}" +
                "Если программа не запускается совсем — приложите этот файл, когда напишете о проблеме." +
                Environment.NewLine +
                "Программа ничего не меняла в системе: любое изменение делается только по нажатию кнопки.",
                "Custom Tweak By Doxaiz — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Если и окно с ошибкой не показать — остаётся журнал.
        }
    }
}
