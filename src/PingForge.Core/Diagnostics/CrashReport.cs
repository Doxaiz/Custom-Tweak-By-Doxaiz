using System.Text;

namespace PingForge.Core.Diagnostics;

/// <summary>Версии и окружение для отчёта о падении.</summary>
public sealed record CrashReportContext(
    string? AppVersionRu = null,
    string? ServiceVersionRu = null,
    string? ProtocolRu = null,
    string? WindowsRu = null,
    bool? RunsAsAdministrator = null,
    string? ProcessPathRu = null);

/// <summary>Результат записи отчёта: путь или причина отказа.</summary>
public sealed record CrashReportWriteResult(bool Written, string PathRu, string ProblemRu)
{
    public static CrashReportWriteResult Ok(string path) => new(true, path, string.Empty);
    public static CrashReportWriteResult Failed(string problem) => new(false, string.Empty, problem);
}

/// <summary>Сборка текста отчёта о падении.</summary>
public static class CrashReportText
{
    public const int DefaultLogLines = 50;

    /// <summary>Собрать текст отчёта. Ошибок не бросает: отчёт о падении не должен падать сам.</summary>
    public static string Build(
        string whatRu,
        Exception? exception,
        CrashReportContext context,
        IEnumerable<string>? logLines = null,
        int maxLogLines = DefaultLogLines,
        DateTime? momentLocal = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTime moment = momentLocal ?? DateTime.Now;
        var text = new StringBuilder();

        text.AppendLine("Custom Tweak By Doxaiz — отчёт об ошибке");
        text.AppendLine("Этот файл лежит на вашем диске. Программа никуда его не отправляет.");
        text.AppendLine();
        text.AppendLine($"Время: {moment:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Что случилось: {(string.IsNullOrWhiteSpace(whatRu) ? "ошибка без описания" : whatRu.Trim())}");
        text.AppendLine();
        text.AppendLine("── Версии и окружение ──");
        text.AppendLine($"Окно: {Value(context.AppVersionRu)}");
        text.AppendLine($"Служба: {Value(context.ServiceVersionRu)}");
        text.AppendLine($"Протокол: {Value(context.ProtocolRu)}");
        text.AppendLine($"Windows: {Value(context.WindowsRu)}");
        text.AppendLine($"Права администратора: {(context.RunsAsAdministrator is null ? "неизвестно" : context.RunsAsAdministrator.Value ? "есть" : "нет")}");
        text.AppendLine($"Файл программы: {Value(context.ProcessPathRu)}");
        text.AppendLine();

        if (exception is null)
        {
            text.AppendLine("── Ошибка ──");
            text.AppendLine("Подробностей нет: исключение не передано.");
        }
        else
        {
            AppendException(text, exception, level: 0);
        }

        text.AppendLine();
        text.AppendLine($"── Журнал: последние {Math.Max(0, maxLogLines)} строк ──");

        List<string> tail = TakeTail(logLines, maxLogLines);
        if (tail.Count == 0)
        {
            text.AppendLine("Журнала нет или он пуст.");
        }
        else
        {
            foreach (string line in tail)
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Последние строки журнала. Отдельным методом — его же использует «пакет для поддержки»,
    /// и обрезка должна быть одна на оба места.
    /// </summary>
    public static List<string> TakeTail(IEnumerable<string>? lines, int count)
    {
        if (lines is null || count <= 0)
        {
            return [];
        }

        var all = new List<string>();
        foreach (string? line in lines)
        {
            all.Add(line ?? string.Empty);
        }

        return all.Count <= count ? all : all.GetRange(all.Count - count, count);
    }

    private static void AppendException(StringBuilder text, Exception exception, int level)
    {
        string indent = new(' ', level * 2);
        text.AppendLine("── Ошибка ──");
        text.AppendLine($"{indent}Тип: {exception.GetType().FullName}");
        text.AppendLine($"{indent}Сообщение: {exception.Message}");
        text.AppendLine($"{indent}Стек:");

        foreach (string line in (exception.StackTrace ?? "стек недоступен").Split('\n'))
        {
            text.AppendLine($"{indent}  {line.TrimEnd('\r')}");
        }

        if (exception is AggregateException aggregate)
        {
            int number = 0;
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                text.AppendLine();
                text.AppendLine($"{indent}Внутренняя ошибка №{++number}:");
                AppendException(text, inner, level + 1);
            }
        }
        else if (exception.InnerException is { } innerException)
        {
            text.AppendLine();
            text.AppendLine($"{indent}Внутренняя ошибка:");
            AppendException(text, innerException, level + 1);
        }
    }

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "неизвестно" : text.Trim();
}

/// <summary>Запись отчёта файлом.</summary>
public static class CrashReportWriter
{
    public const string FolderName = "crash";

    /// <summary>Записать отчёт. Возвращает путь либо причину отказа — исключений не бросает.</summary>
    public static CrashReportWriteResult Write(
        string crashDirectory,
        string whatRu,
        Exception exception,
        CrashReportContext context,
        IEnumerable<string>? logLines = null,
        int maxLogLines = CrashReportText.DefaultLogLines,
        DateTime? momentLocal = null)
    {
        if (string.IsNullOrWhiteSpace(crashDirectory))
        {
            return CrashReportWriteResult.Failed("каталог для отчётов пуст");
        }

        DateTime moment = momentLocal ?? DateTime.Now;

        try
        {
            Directory.CreateDirectory(crashDirectory);

            string path = Path.Combine(crashDirectory, $"crash-{moment:yyyyMMdd-HHmmss-fff}.txt");
            string text = CrashReportText.Build(whatRu, exception, context, logLines, maxLogLines, moment);

            // Метка UTF-8: отчёт открывают в Блокноте и PowerShell, и русский текст должен читаться.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return CrashReportWriteResult.Ok(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return CrashReportWriteResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
