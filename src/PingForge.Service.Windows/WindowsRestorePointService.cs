using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Service.Windows;

/// <summary>
/// Точка восстановления Windows через штатную команду <c>Checkpoint-Computer</c>.
///
/// Коды результата — часть контракта, поэтому здесь нет «просто ошибки»: пользователь должен видеть,
/// сработало ли, а если нет — почему именно. Самые частые причины честно разделены:
///   • защита системы выключена (Windows не создаёт точки в принципе);
///   • суточный предел: Windows по умолчанию делает не больше одной точки в 24 часа;
///   • нет прав: точку восстановления может создать только администратор;
///   • всё остальное — ошибка с текстом от Windows, а не «не вышло».
///
/// Поведение проверяется только на Windows: на сборочной машине без Windows этот файл лишь компилируется.
/// </summary>
public sealed class WindowsRestorePointService : ISystemRestorePointService
{
    private readonly ICommandPort _command;
    private readonly TimeSpan _timeout;

    public WindowsRestorePointService(ICommandPort command, TimeSpan? timeout = null)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public (RestorePointResultKind Outcome, string MessageRu) TryCreateRestorePoint(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "PingForge: перед изменением настроек";
        }

        // Описание обрезается до 255 символов: Windows молча отклоняет более длинные.
        if (description.Length > 255)
        {
            description = description[..255];
        }

        CommandResult result;
        try
        {
            using var timeoutSource = new CancellationTokenSource(_timeout);
            result = _command
                .RunAsync("powershell", BuildArguments(description), timeoutSource.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            return (RestorePointResultKind.FailedWithError,
                $"Точка восстановления не создана за {_timeout.TotalMinutes:0} мин: Windows не ответила вовремя. " +
                "Твики применяются, но откат останется только средствами программы.");
        }
        catch (Exception ex)
        {
            return (RestorePointResultKind.FailedWithError,
                $"Точка восстановления не создана: {ex.Message}. Продолжаю без неё.");
        }

        string output = (result.StandardOutput + " " + result.StandardError).Trim();
        return Interpret(result.ExitCode, output);
    }

    /// <summary>
    /// Разбор ответа: сначала известные случаи, потом общий. Порядок именно такой — иначе
    /// «запрещено политикой» легко принять за «не хватило прав», а это разные советы пользователю.
    /// </summary>
    public static (RestorePointResultKind Outcome, string MessageRu) Interpret(int exitCode, string output)
    {
        string text = output ?? string.Empty;

        if (Contains(text, "системы защиты", "system protection", "disable"))
        {
            return (RestorePointResultKind.SystemProtectionDisabled,
                "Точка восстановления не создана: защита системы отключена. Включите её в «Свойствах компьютера → Защита системы», " +
                "если хотите откатываться и средствами Windows. Программа и без этого вернёт прежние настройки.");
        }

        if (Contains(text, "в течение", "within", "frequency", "сутки", "1440"))
        {
            return (RestorePointResultKind.SkippedFrequencyLimit24h,
                "Точка восстановления пропущена из-за суточного лимита Windows: точки создаются не чаще одной в 24 часа. " +
                "Это не ошибка — программа всё равно запомнила прежние значения и сможет вернуть их.");
        }

        if (exitCode != 0 && (Contains(text, "администратор", "administrator", "access", "доступ")))
        {
            return (RestorePointResultKind.FailedWithError,
                "Точка восстановления не создана: нужны права администратора. Служба PingForge запускается от LocalSystem — " +
                "проверьте, что она установлена и работает.");
        }

        if (exitCode != 0)
        {
            return (RestorePointResultKind.FailedWithError,
                $"Точка восстановления не создана (код {exitCode}): {Shorten(text)}. Твики всё равно применятся, " +
                "а вернуть прежние значения можно средствами программы — снапшот уже готов.");
        }

        return (RestorePointResultKind.CreatedSuccessfully,
            "Точка восстановления Windows создана: откатиться можно будет даже средствами самой Windows.");
    }

    private static IReadOnlyList<string> BuildArguments(string description) =>
    [
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy", "Bypass",
        "-Command",
        $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS'",
    ];

    private static bool Contains(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";
}
