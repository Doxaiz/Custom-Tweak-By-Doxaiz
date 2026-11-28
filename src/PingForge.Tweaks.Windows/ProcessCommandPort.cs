using System.Diagnostics;
using System.Globalization;
using System.Text;
using PingForge.Core.Abstractions;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Запуск netsh и PowerShell. Ключевые правила, без которых это место становится дырой:
///   • никакой склейки командной строки — только ArgumentList, поэтому значение из системы
///     не может превратиться в дополнительный ключ команды;
///   • скрытое окно и запрет интерактива;
///   • таймаут и поддержка отмены: висящий netsh не должен блокировать интерфейс;
///   • вывод читается в OEM-кодировке консоли: netsh на русской Windows печатает CP866,
///     и без этого русские подписи превращаются в мусор, а разбор — в «параметр не найден».
/// </summary>
public sealed class ProcessCommandPort : ICommandPort
{
    private static readonly object ProviderLock = new();
    private static bool _providerRegistered;

    private readonly TimeSpan _timeout;
    private readonly Encoding _outputEncoding;

    public ProcessCommandPort(TimeSpan? timeout = null, Encoding? outputEncoding = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _outputEncoding = outputEncoding ?? OemEncoding();
    }

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = _outputEncoding,
            StandardErrorEncoding = _outputEncoding
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return CommandResult.Failed($"Не удалось запустить «{fileName}».");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);

            // Процесс убит, а чтение его вывода осталось незавершённым. Раньше эти задачи просто
            // бросали: их исключения (поток закрылся вместе с процессом) становились
            // «ненаблюдаемыми» и всплывали при сборке мусора в случайном месте. Дожидаемся коротко
            // и гасим — результат уже не нужен, важен порядок.
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return CommandResult.Failed($"«{fileName}» не ответил за {_timeout.TotalSeconds:F0} с и был остановлен.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        return new CommandResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    /// <summary>
    /// Дождаться чтения вывода после остановки процесса и погасить ошибки: читать уже нечего,
    /// а «повисшая» задача чтения позже всплывает как ненаблюдаемое исключение.
    /// </summary>
    private static async Task DrainAsync(params Task<string>[] readers)
    {
        try
        {
            await Task.WhenAny(Task.WhenAll(readers), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                       or InvalidOperationException)
        {
            // Поток закрылся вместе с процессом — это ожидаемо, а не ошибка запуска команды.
        }
    }

    /// <summary>OEM-кодировка консоли текущей системы (на русской Windows — CP866).</summary>
    public static Encoding OemEncoding()
    {
        RegisterCodePagesProvider();

        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static void RegisterCodePagesProvider()
    {
        lock (ProviderLock)
        {
            if (_providerRegistered)
            {
                return;
            }

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
            {
                // На сборочной машине без Windows провайдер может быть недоступен — это не мешает компиляции.
            }

            _providerRegistered = true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Процесс уже завершился — нормальная гонка, а не ошибка приложения.
        }
    }
}
