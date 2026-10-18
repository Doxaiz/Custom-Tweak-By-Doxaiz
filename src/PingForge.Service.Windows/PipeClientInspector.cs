using System.ComponentModel;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PingForge.Service.Windows;

/// <summary>
/// Кто подключился к каналу: имя учётной записи, её SID и — главное — есть ли у неё права
/// администратора <b>в своём собственном процессе</b>.
/// </summary>
/// <remarks>
/// Почему не <c>pipe.RunAsClient(...)</c>, хотя это первое, что приходит в голову.
///
/// .NET-клиент (<see cref="NamedPipeClientStream"/>) по умолчанию подключается с уровнем
/// олицетворения <c>Anonymous</c>. При таком уровне <c>RunAsClient</c> НЕ олицетворяет клиента:
/// код выполняется в контексте самого сервера, то есть службы от LocalSystem. Проверка
/// «администратор ли клиент» в этом случае отвечает «да» всегда — и дыра остаётся открытой,
/// только теперь её не видно. Поэтому права берутся не через олицетворение, а по процессу клиента:
///   • <c>GetNamedPipeClientProcessId</c> — идентификатор процесса клиента на другом конце канала
///     (сервер спрашивает именно про клиента; обратная функция вернула бы PID самой службы);
///   • <c>OpenProcessToken(TOKEN_QUERY = 0x0008)</c> — его токен; права на сам процесс при этом
///     минимальны (PROCESS_QUERY_LIMITED_INFORMATION), их хватает, чтобы спросить токен;
///   • <c>WindowsPrincipal.IsInRole(Administrator)</c> — по токену видно, включена ли группа
///     администраторов: у процесса без повышения она есть, но помечена «только запрет»
///     (deny-only), и роль не подтверждается.
///
/// Любая ошибка чтения трактуется как «прав нет»: отказать безопаснее, чем разрешить.
/// </remarks>
public sealed record PipeClientIdentity(
    bool IsAdministrator,
    string NameRu,
    string Sid,
    int ProcessId,
    string? ProblemRu)
{
    /// <summary>Права определить не удалось. Команды, меняющие систему, такому клиенту не положены.</summary>
    public static PipeClientIdentity Unknown(string problemRu) =>
        new(false, "не определён", "не определён", 0, problemRu);

    /// <summary>Строка для журнала службы: по ней видно, кому и почему было отказано.</summary>
    public string DescribeRu() =>
        ProblemRu is null
            ? $"клиент: {NameRu} (SID {Sid}), процесс {ProcessId}, права администратора: {(IsAdministrator ? "есть" : "нет")}"
            : $"клиент: права не определены ({ProblemRu}) — считаю, что прав нет";
}

/// <summary>Чтение прав клиента канала. Вынесено отдельно, чтобы отказ по правам был виден в журнале.</summary>
public static class PipeClientInspector
{
    /// <summary>
    /// Определить, кто подключился. Метод не бросает исключений: любая неудача превращается
    /// в «прав нет» с объяснением — служба из-за этого не останавливается и молча не разрешает.
    /// </summary>
    public static PipeClientIdentity Inspect(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        if (!OperatingSystem.IsWindows())
        {
            return PipeClientIdentity.Unknown("канал работает не в Windows");
        }

        if (!pipe.IsConnected)
        {
            return PipeClientIdentity.Unknown("клиент уже отключился");
        }

        try
        {
            if (!TryGetClientProcessId(pipe, out int processId))
            {
                return PipeClientIdentity.Unknown("система не назвала процесс клиента");
            }

            using SafeTokenHandle? token = OpenClientToken(processId, out string? problem);
            if (token is null)
            {
                return PipeClientIdentity.Unknown(problem ?? "не удалось открыть токен процесса клиента");
            }

            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var principal = new WindowsPrincipal(identity);

            string name;
            try
            {
                name = string.IsNullOrWhiteSpace(identity.Name) ? "имя не прочитано" : identity.Name;
            }
            catch (Exception ex) when (ex is SystemException or UnauthorizedAccessException)
            {
                // Имя требует обращения к базе учётных записей; его отсутствие не повод терять права.
                name = $"имя не прочитано ({ex.GetType().Name})";
            }

            string sid;
            bool isAdmin;
            try
            {
                sid = identity.User?.Value ?? "SID не прочитан";
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is SystemException or UnauthorizedAccessException)
            {
                // SID и роль читаются из того же токена: не прочитались — это «прав нет»,
                // а не падение канала. Служба обязана пережить любого клиента.
                return PipeClientIdentity.Unknown($"токен клиента открыт, но не прочитался ({ex.GetType().Name}: {ex.Message})");
            }

            // Имя, SID и роль прочитаны до закрытия дескрипторов: токен живёт только внутри этого блока.
            return new PipeClientIdentity(isAdmin, name, sid, processId, null);
        }
        catch (Exception ex)
        {
            // Контракт метода — не бросать исключений вообще. Любая неудача (включая те типы,
            // что здесь не перечислить заранее: SystemException от чтения токена, ошибки
            // маршалинга дескрипторов) превращается в «прав нет» с объяснением. Один странный
            // клиент не должен стоить службе канала.
            return PipeClientIdentity.Unknown($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Открыть токен процесса клиента. Прав на сам процесс почти не нужно: открывается он
    /// с PROCESS_QUERY_LIMITED_INFORMATION — этого хватает, чтобы спросить токен, и мало
    /// для чего-либо ещё. Служба идёт от LocalSystem и откроет любой процесс, но просит минимум:
    /// лишние права в коде, который проверяет чужие права, — это отдельный риск.
    /// </summary>
    private static SafeTokenHandle? OpenClientToken(int processId, out string? problem)
    {
        problem = null;

        IntPtr raw = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (raw == IntPtr.Zero)
        {
            problem = $"процесс клиента {processId} не открылся (ошибка {Marshal.GetLastWin32Error()}): возможно, он уже завершился";
            return null;
        }

        // Дескриптор процесса нужен только на время запроса токена: сам токен живёт своей жизнью.
        using var process = new SafeProcessHandle(raw, ownsHandle: true);
        if (!OpenProcessToken(process.DangerousGetHandle(), TokenQuery, out SafeTokenHandle opened) || opened.IsInvalid)
        {
            problem = $"токен процесса {processId} не открылся (ошибка {Marshal.GetLastWin32Error()})";
            opened?.Dispose();
            return null;
        }

        return opened;
    }

    private static bool TryGetClientProcessId(NamedPipeServerStream pipe, out int processId)
    {
        // Win32 отдаёт PID беззнаковым DWORD: читаем как uint и только потом приводим к int,
        // чтобы широкий PID не превратился в отрицательное число на полпути.
        uint rawProcessId = 0;
        bool ok = GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), ref rawProcessId);
        processId = ok ? unchecked((int)rawProcessId) : 0;
        return ok;
    }

    /// <summary>
    /// TOKEN_QUERY (0x0008) — право спросить сведения о токене (кто владелец, какие группы включены).
    /// Это право на ТОКЕН, а не на процесс: 0x1000 в маску прав токена не входит, и с ним
    /// OpenProcessToken честно завершается с ERROR_ACCESS_DENIED — токен не открывается ни у кого.
    /// </summary>
    private const uint TokenQuery = 0x0008;

    /// <summary>Тот же минимум прав, но на сам процесс: спросить его токен.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, ref uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    /// <summary>Дескриптор токена: закрывается сам, иначе каждый клиент оставлял бы в системе ручку.</summary>
    private sealed class SafeTokenHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
