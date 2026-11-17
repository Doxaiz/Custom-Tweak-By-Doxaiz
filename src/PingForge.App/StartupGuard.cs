using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PingForge.Core.Diagnostics;

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
    private const long MaxBytes = RollingLogFile.DefaultMaxBytes;
    private const int MaxFiles = RollingLogFile.DefaultMaxFiles;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static readonly object Gate = new();
    private static RollingLogFile? _logFile;
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
                    // Метка UTF-8 нужна ради чтения журнала в старых редакторах и PowerShell:
                    // без неё русский текст превращается в «кракозябры», а журнал читают люди.
                    _logFile ??= new RollingLogFile(path, MaxBytes, MaxFiles, writeByteOrderMark: true);
                    _logFile.Append(line);
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

        // Команды окна (кнопки) — тоже путь к ошибке. Без этого исключение из async void-команды
        // приходило как «ошибка в работе программы» без указания, что именно нажали.
        ViewModels.AsyncRelayCommand.OnFailure = (whatRu, ex) => Report(whatRu, ex);
    }

    public static void Report(string whatRu, Exception ex)
    {
        StartupLog.Problem(whatRu, ex);

        // Отчёт о падении пишется всегда — и в тихом режиме тоже: самопроверка на сборочной машине
        // должна оставлять такой же файл, как у человека, иначе разбирать её сбой нечем.
        CrashReportWriteResult crash = WriteCrashFile(whatRu, ex);

        if (Silent)
        {
            return;
        }

        try
        {
            string logLine = StartupLog.Path.Length > 0
                ? $"Подробности записаны в файл:{Environment.NewLine}{StartupLog.Path}"
                : $"Журнал записать не удалось: {StartupLog.ProblemRu}";

            string crashLine = crash.Written
                ? $"Отчёт об ошибке: {crash.PathRu}"
                : $"Отчёт об ошибке записать не удалось: {crash.ProblemRu}";

            MessageBox.Show(
                $"Программа столкнулась с ошибкой.{Environment.NewLine}{Environment.NewLine}" +
                $"{whatRu}: {ex.GetType().Name}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"{logLine}{Environment.NewLine}{crashLine}{Environment.NewLine}{Environment.NewLine}" +
                "Оба файла лежат на вашем диске; программа никуда их не отправляет. Приложите отчёт " +
                "об ошибке, если напишете о проблеме — или соберите «пакет для поддержки» в разделе «О программе»." +
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

    /// <summary>
    /// Записать текстовый отчёт о падении в <c>ProgramData\PingForge\crash</c>. Ничего не бросает:
    /// этот код работает уже во время падения, и второе исключение здесь недопустимо.
    ///
    /// В отчёт идут последние строки журнала запуска и журнала службы — по ним видно, что программа
    /// делала перед ошибкой. Ни настроек, ни путей к играм в нём нет: отчёт может уехать в чужой
    /// почтовый ящик, и лишнего в нём быть не должно.
    /// </summary>
    private static CrashReportWriteResult WriteCrashFile(string whatRu, Exception ex)
    {
        try
        {
            // Пути берутся из одного места — того же, откуда их берёт «пакет для поддержки»:
            // отчёт о падении и пакет обязаны находить одни и те же журналы.
            string dataDirectory = SupportPackageFactory.DataDirectory;

            var context = new CrashReportContext(
                AppVersionRu: SupportPackageFactory.AppVersion(),
                WindowsRu: $"{Environment.OSVersion.VersionString}, 64-разрядная: {Environment.Is64BitOperatingSystem}",
                ProcessPathRu: Environment.ProcessPath);

            CrashReportWriteResult result = CrashReportWriter.Write(
                SupportPackageFactory.CrashDirectory,
                whatRu,
                ex,
                context,
                ReadLogTail(SupportPackageFactory.StartupLogPath, dataDirectory),
                maxLogLines: 50);

            if (result.Written)
            {
                StartupLog.Note($"Отчёт об ошибке записан: {result.PathRu}");
            }
            else
            {
                StartupLog.Warn($"Отчёт об ошибке не записался: {result.ProblemRu}");
            }

            return result;
        }
        catch (Exception failure)
        {
            // Отчёт о падении не имеет права уронить программу ещё раз.
            StartupLog.Warn($"Отчёт об ошибке не записался: {failure.GetType().Name}: {failure.Message}");
            return CrashReportWriteResult.Failed($"{failure.GetType().Name}: {failure.Message}");
        }
    }

    /// <summary>Последние строки обоих журналов. Чего прочитать не удалось — того просто нет в отчёте.</summary>
    private static List<string> ReadLogTail(string appLogPath, string dataDirectory)
    {
        var lines = new List<string>();

        foreach ((string title, string path) in new[]
                 {
                     ("журнал запуска окна", appLogPath),
                     ("журнал службы", Path.Combine(dataDirectory, "service.log")),
                 })
        {
            lines.Add($"── {title}: {(string.IsNullOrWhiteSpace(path) ? "путь неизвестен" : path)}");

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    string[] all = File.ReadAllLines(path);
                    lines.AddRange(CrashReportText.TakeTail(all, 25));
                }
                else
                {
                    lines.Add("файла нет");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"не прочитался: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return lines;
    }
}
