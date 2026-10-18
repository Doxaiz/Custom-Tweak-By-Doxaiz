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
///   • <c>GetTokenInformation</c> — из токена читаются его владелец (SID и имя) и группы:
///     администратор — это включённая, а не «только запрет» (deny-only), группа администраторов.
///     У процесса без повышения та же группа есть, но помечена deny-only, и права не даёт.
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

            // Сведения о клиенте читаются прямо из токена через GetTokenInformation — для этого
            // хватает TOKEN_QUERY, с которым токен и открывался. Собирать WindowsIdentity из
            // первичного токена чужого процесса нельзя: ей нужно олицетворение, и на живом
            // раннере это честно падало с «Access is denied» — личность не читалась ни у кого.
            string sid = ReadUserSid(token) ?? "SID не прочитан";
            string name = TranslateName(sid);
            bool isAdmin = ReadIsAdministrator(token);

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

    /// <summary>SID владельца токена — то, что служба пишет в журнал и отдаёт в ping.</summary>
    private static string? ReadUserSid(SafeTokenHandle token)
    {
        IntPtr buffer = GetTokenBuffer(token, TokenUser);
        if (buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr sidPtr = Marshal.ReadIntPtr(buffer, 0);
            return sidPtr == IntPtr.Zero ? null : new SecurityIdentifier(sidPtr).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Администратор ли клиент: группа администраторов (S-1-5-32-544) присутствует в токене
    /// и включена. У процесса без повышения та же группа помечена «только запрет» (deny-only) —
    /// это НЕ права, и WindowsPrincipal ответил бы так же.
    /// </summary>
    private static bool ReadIsAdministrator(SafeTokenHandle token)
    {
        IntPtr buffer = GetTokenBuffer(token, TokenGroups);
        if (buffer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int count = Marshal.ReadInt32(buffer, 0);
            int stride = IntPtr.Size == 8 ? 16 : 8;
            int offset = IntPtr.Size == 8 ? 8 : 4;

            for (int i = 0; i < count; i++)
            {
                int entry = offset + (i * stride);
                IntPtr sidPtr = Marshal.ReadIntPtr(buffer, entry);
                if (sidPtr == IntPtr.Zero)
                {
                    continue;
                }

                string groupSid = new SecurityIdentifier(sidPtr).Value;
                uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, entry + IntPtr.Size));

                bool enabled = (attributes & GroupEnabled) != 0;
                bool denyOnly = (attributes & GroupUseForDenyOnly) != 0;
                if (groupSid == AdministratorsSid && enabled && !denyOnly)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Имя учётной записи по SID. Не перевелось — честно «не прочитано», а не падение.</summary>
    private static string TranslateName(string sid)
    {
        if (sid == "SID не прочитан")
        {
            return "имя не прочитано";
        }

        try
        {
            string name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            return string.IsNullOrWhiteSpace(name) ? "имя не прочитано" : name;
        }
        catch (Exception ex) when (ex is SystemException or UnauthorizedAccessException)
        {
            return "имя не прочитано";
        }
    }

    /// <summary>Буфер сведений о токене: размер спрашивается у системы, а не угадывается.</summary>
    private static IntPtr GetTokenBuffer(SafeTokenHandle token, int informationClass)
    {
        GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out int length);
        if (length <= 0)
        {
            return IntPtr.Zero;
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        if (!GetTokenInformation(token, informationClass, buffer, length, out _))
        {
            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }

        return buffer;
    }

    private const int TokenUser = 1;

    private const int TokenGroups = 2;

    private const uint GroupEnabled = 0x00000004;

    private const uint GroupUseForDenyOnly = 0x00000010;

    private const string AdministratorsSid = "S-1-5-32-544";

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

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
