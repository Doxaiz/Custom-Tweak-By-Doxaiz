using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Кто именно «пользователь» на этой машине.
///
/// Служба работает под LocalSystem и своего интерактивного пользователя не имеет: её HKCU — профиль
/// системы. Поэтому, когда SID запросившего почему-то неизвестен, профиль определяется по активному
/// сеансу: берётся токен пользователя, вошедшего в консольный сеанс, и из него — SID.
///
/// Если ни того, ни другого нет, метод честно возвращает null: вызывающая сторона покажет отказ
/// с причиной, а не запишет настройку в профиль службы.
/// </summary>
internal static class InteractiveUserSession
{
    private const uint NoActiveSession = 0xFFFFFFFF;

    /// <summary>SID пользователя активного консольного сеанса или null, если его нет.</summary>
    public static string? TryGetActiveUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == NoActiveSession)
            {
                return null;
            }

            // Токен сеанса доступен администратору и системе: обычному процессу этот вызов не удаётся,
            // но обычному процессу он и не нужен — он сам работает в своём профиле.
            if (!WTSQueryUserToken(sessionId, out IntPtr token) || token == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using WindowsIdentity identity = new(token);
                return identity.User?.Value;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or Win32Exception or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
