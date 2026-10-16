using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PingForge.Network;

namespace PingForge.Network.Windows;

/// <summary>
/// Имя процесса по идентификатору через <c>QueryFullProcessImageName</c> с минимальными правами
/// (PROCESS_QUERY_LIMITED_INFORMATION). Так читается и путь запуска, в том числе у процессов из другой
/// учётной записи — а значит, «это Steam или игра» решается по реальному файлу, а не по догадке.
///
/// Если прочитать не удалось (процесс системный, закрылся, прав не хватило) — возвращается null:
/// «не знаю имени» и «процесс без имени» — разные вещи, и детектор считает их по-разному.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ProcessNameResolver : IProcessNameResolver
{
    private const int ProcessQueryLimitedInformation = 0x1000;

    private readonly int _maxPath;
    private readonly Dictionary<int, string?> _cache = new();

    public Win32ProcessNameResolver(int maxPathCharacters = 1024) => _maxPath = maxPathCharacters;

    public Task<string?> TryGetProcessNameAsync(int processId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (processId <= 4)
        {
            return Task.FromResult<string?>(null);
        }

        lock (_cache)
        {
            if (_cache.TryGetValue(processId, out string? cached))
            {
                return Task.FromResult(cached);
            }
        }

        string? name = Query(processId);

        lock (_cache)
        {
            _cache[processId] = name;
        }

        return Task.FromResult(name);
    }

    private string? Query(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(_maxPath);
            int size = buffer.Capacity;

            if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return null;
            }

            string fullPath = buffer.ToString(0, size);
            return string.IsNullOrWhiteSpace(fullPath) ? null : GameProcessCatalog.Normalize(fullPath);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
