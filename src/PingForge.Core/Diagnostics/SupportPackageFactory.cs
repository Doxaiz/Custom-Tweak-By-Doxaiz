using System.Text;

namespace PingForge.Core.Diagnostics;

/// <summary>
/// Фабрика плана пакета поддержки: где лежат файлы, что пишем в сведения, какие пути безопасны.
/// </summary>
public static class SupportPackageFactory
{
    public const int LogLinesInPackage = 500;

    /// <summary>Каталог общих данных программы — тот же, что у службы и базы.</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PingForge");

    /// <summary>Журнал запуска окна лежит в данных пользователя: писать туда можно всегда.</summary>
    public static string StartupLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomTweakByDoxaiz",
        "app-start.log");

    /// <summary>Каталог отчётов об ошибках.</summary>
    public static string CrashDirectory => Path.Combine(DataDirectory, CrashReportWriter.FolderName);

    /// <summary>Куда сохранять готовый пакет: документы пользователя, имя с датой и временем.</summary>
    public static string DefaultTargetPath(DateTime? moment = null)
    {
        DateTime stamp = moment ?? DateTime.Now;
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.GetTempPath();
        }

        return Path.Combine(folder, $"PingForge-support-{stamp:yyyyMMdd-HHmmss}.zip");
    }

    /// <summary>
    /// Составить план. Ничего не пишет на диск и не бросает: отсутствие журнала — это пункт
    /// «логов нет», а не отказ экрана.
    /// </summary>
    public static SupportPackagePlan? CreatePlan()
    {
        var entries = new List<SupportPackageEntry>
        {
            SupportPackageEntry.Text(
                "Сведения о программе",
                "system-info.txt",
                "Версии, Windows, права — то, без чего жалобу не разобрать.",
                DescribeSystem),

            SupportPackageEntry.Text(
                "Настройки — безопасная часть",
                "settings-summary.txt",
                "Профиль и поведение окна. Пути к играм и содержимое файла настроек не попадают.",
                DescribeSettings),

            SupportPackageEntry.LogFile(
                "Журнал службы",
                "service.log",
                "Что служба делала и что ей отвечала Windows.",
                Path.Combine(DataDirectory, "service.log"),
                LogLinesInPackage),

            SupportPackageEntry.LogFile(
                "Журнал запуска окна",
                "app-start.log",
                "Как окно запускалось: сколько заняла распаковка, какие разделы собрались.",
                StartupLogPath,
                LogLinesInPackage),

            SupportPackageEntry.Text(
                "Последний отчёт об ошибке",
                "last-crash.txt",
                "Если программа падала — тип ошибки, стек и последние строки журнала.",
                DescribeLastCrash),
        };

        return SupportPackageBuilder.Plan(DefaultTargetPath(), entries);
    }

    /// <summary>
    /// Текст «сведений о программе». Ни имени компьютера, ни имени пользователя: для разбора хватает
    /// версий, разрядности и прав, а лишнее в чужом почтовом ящике ни к чему.
    /// </summary>
    public static string DescribeSystem()
    {
        var text = new StringBuilder();
        text.AppendLine("Custom Tweak By Doxaiz — сведения о программе");
        text.AppendLine();
        text.AppendLine($"Версия окна: {AppVersion()}");
        text.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        text.AppendLine($"64-разрядная система: {(Environment.Is64BitOperatingSystem ? "да" : "нет")}");
        text.AppendLine($"Версия .NET: {Environment.Version}");
        text.AppendLine($"Процессоров: {Environment.ProcessorCount}");
        text.AppendLine($"Рабочий набор, МБ: {Environment.WorkingSet / (1024 * 1024)}");
        text.AppendLine($"Файл программы: {Environment.ProcessPath ?? "неизвестно"}");
        text.AppendLine($"Культура: {System.Globalization.CultureInfo.CurrentCulture.Name}");
        text.AppendLine();
        text.AppendLine("Этот файл собран по кнопке в окне программы и никуда не отправлялся.");
        return text.ToString();
    }

    /// <summary>
    /// Сводка настроек без путей. Если файл не читается — так и написано: врать «настроек нет»
    /// нельзя, потому что тогда причина отказа будет искаться не там.
    /// </summary>
    public static string DescribeSettings()
    {
        var text = new StringBuilder();
        text.AppendLine("Custom Tweak By Doxaiz — безопасная часть настроек");
        text.AppendLine("Пути к играм и содержимое файла настроек сюда не попадают.");

        try
        {
            string path = Path.Combine(DataDirectory, "settings.json");
            string json = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

            // Читаем без доменной модели AppSettings, чтобы не зависеть от её сборки:
            // здесь важно только показать, что файл есть и что мы не выводим пути к .exe.
            text.AppendLine();
            if (string.IsNullOrWhiteSpace(json))
            {
                text.AppendLine("Файл настроек не найден: программа ещё ничего не сохранила.");
            }
            else
            {
                // Безопасная часть: только общие флаги, без путей к играм и узлов.
                // Разбираем вручную, чтобы не тащить зависимости.
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                string perf = root.TryGetProperty("PerfProfile", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() ?? "не задано" : "не задано";
                bool offerRestore = root.TryGetProperty("OfferRestorePoint", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
                bool startMin = root.TryGetProperty("StartMinimized", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
                bool closeTray = root.TryGetProperty("CloseToTray", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
                int journalRows = root.TryGetProperty("JournalRowsShown", out var j) && j.ValueKind == System.Text.Json.JsonValueKind.Number ? j.GetInt32() : 0;
                bool setupDone = root.TryGetProperty("SetupCompleted", out var sd) && sd.ValueKind == System.Text.Json.JsonValueKind.True;

                int customGames = 0;
                if (root.TryGetProperty("CustomGames", out var cg) && cg.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    customGames = cg.GetArrayLength();
                }

                int routingNodes = 0;
                if (root.TryGetProperty("RoutingNodes", out var rn) && rn.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    routingNodes = rn.GetArrayLength();
                }

                bool autoSelect = root.TryGetProperty("RoutingAutoSelect", out var ra) && ra.ValueKind == System.Text.Json.JsonValueKind.True;

                text.AppendLine();
                text.AppendLine($"Профиль потребления: {perf}");
                text.AppendLine($"Предлагать точку восстановления: {(offerRestore ? "да" : "нет")}");
                text.AppendLine($"Запускаться свёрнутым: {(startMin ? "да" : "нет")}");
                text.AppendLine($"Закрывать в трей: {(closeTray ? "да" : "нет")}");
                text.AppendLine($"Строк журнала на экране: {journalRows}");
                text.AppendLine($"Мастер первого запуска пройден: {(setupDone ? "да" : "нет")}");
                text.AppendLine($"Своих игр добавлено: {customGames}");
                text.AppendLine($"Узлов маршрутизации сохранено: {routingNodes}");
                text.AppendLine($"Автовыбор узла: {(autoSelect ? "да" : "нет")}");
            }

            text.AppendLine();
            text.AppendLine("Пути к играм: в этот файл не попадают. Если нужно, проверьте settings.json вручную — но не прикладывайте его целиком.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException)
        {
            text.AppendLine();
            text.AppendLine($"Файл настроек не прочитался: {ex.GetType().Name}: {ex.Message}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Последний отчёт о падении — если он есть. Берётся самый свежий по имени файла: имена начинаются
    /// с даты, поэтому сортировка по имени и есть сортировка по времени.
    /// </summary>
    public static string DescribeLastCrash()
    {
        try
        {
            if (!Directory.Exists(CrashDirectory))
            {
                return "Отчётов об ошибках нет: программа не падала.";
            }

            string? newest = Directory
                .GetFiles(CrashDirectory, "crash-*.txt")
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();

            if (newest is null)
            {
                return "Отчётов об ошибках нет: программа не падала.";
            }

            return File.ReadAllText(newest) + Environment.NewLine + Environment.NewLine +
                   $"Файл на диске: {newest}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Отчёт о падении не прочитался: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Версия программы — из сборки моделей представления, как и у проверки обновлений.</summary>
    public static string AppVersion() => typeof(SupportPackageFactory).Assembly.GetName().Version?.ToString(3) ?? "неизвестно";
}
