using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Сборка платформенных портов для Windows. Единственное место, где создаются «живые» доступы
/// к реестру, netsh и командлетам: всё остальное приложение работает через интерфейсы и потому
/// проверяемо автотестами.
/// </summary>
public sealed class WindowsTweakPlatform : IDisposable
{
    public WindowsTweakPlatform(TimeSpan? commandTimeout = null)
    {
        Command = new ProcessCommandPort(commandTimeout);
        Registry = new WindowsRegistryPort();
        NicProperties = new PowerShellNicPropertyPort(Command);
        ActiveAdapter = new WindowsActiveAdapterResolver();
    }

    public ICommandPort Command { get; }

    public IRegistryPort Registry { get; }

    public INicPropertyPort NicProperties { get; }

    public IActiveAdapterResolver ActiveAdapter { get; }

    /// <summary>
    /// Порты для каталога твиков. Советники по MTU/DNS подключает приложение: им нужны замеры,
    /// которых у слоя твиков нет.
    /// </summary>
    public TweakPlatformPorts ToCatalogPorts(
        IMtuAdviceProvider? mtuAdvice = null,
        IDnsAdviceProvider? dnsAdvice = null,
        INicPropertyOptionProvider? nicOptions = null) =>
        new(
            Registry: Registry,
            NicProperties: NicProperties,
            Command: Command,
            ActiveAdapter: ActiveAdapter,
            MtuAdvice: mtuAdvice,
            DnsAdvice: dnsAdvice,
            NicOptions: nicOptions ?? new PowerShellNicPropertyOptions(NicProperties));

    public void Dispose()
    {
        // Порты Windows не держат долгоживущих ресурсов: каждый вызов открывает и закрывает
        // ключ реестра или процесс. Метод оставлен, чтобы состав платформы можно было расширять
        // (например, кэшем здоровья релея) без изменения вызывающего кода.
    }
}

/// <summary>Список допустимых значений свойства NIC — нужен для вычисляемого максимума буферов.</summary>
public sealed class PowerShellNicPropertyOptions : INicPropertyOptionProvider
{
    private readonly INicPropertyPort _port;

    public PowerShellNicPropertyOptions(INicPropertyPort port) =>
        _port = port ?? throw new ArgumentNullException(nameof(port));

    public async Task<IReadOnlyList<string>> GetAllowedValuesAsync(
        TweakTarget target,
        string keyword,
        CancellationToken ct = default)
    {
        NicPropertySnapshot snapshot = await _port.ReadAsync(target, keyword, ct).ConfigureAwait(false);
        return snapshot.IsSupported ? snapshot.AllowedValues : Array.Empty<string>();
    }
}
