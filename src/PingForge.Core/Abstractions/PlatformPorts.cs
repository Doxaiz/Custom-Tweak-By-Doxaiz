using PingForge.Core.Models;

namespace PingForge.Core.Abstractions;

/// <summary>
/// Порты платформы. Твики не вызывают реестр и netsh напрямую: они работают через эти интерфейсы,
/// поэтому логику применения/верификации/откатa можно прогнать автотестами без Windows.
/// </summary>

public enum RegistryRootKind
{
    LocalMachine,
    CurrentUser
}

/// <summary>Точное расположение значения в реестре.</summary>
public sealed record RegistryLocation(RegistryRootKind Root, string SubKeyPath, string ValueName)
{
    public override string ToString() =>
        $"{(Root == RegistryRootKind.LocalMachine ? @"HKLM" : @"HKCU")}\\{SubKeyPath}\\{ValueName}";
}

/// <summary>
/// Значение, прочитанное из реестра. Raw содержит то, что реально лежало в системе:
/// если это текст, который нельзя свести к ожидаемому типу, он не выбрасывается, а помечается UnparsableRaw.
/// </summary>
public sealed record RegistryValueSnapshot(
    bool Exists,
    string? CanonicalValue,
    TweakValueDataType DataType,
    string? UnparsableRaw = null)
{
    public static RegistryValueSnapshot Missing(TweakValueDataType dataType) => new(false, null, dataType);

    public static RegistryValueSnapshot Unparsable(string raw, TweakValueDataType dataType) => new(true, null, dataType, raw);

    public bool IsUnparsable => UnparsableRaw is not null;
}

public interface IRegistryPort
{
    Task<RegistryValueSnapshot> ReadAsync(RegistryLocation location, CancellationToken ct = default);

    Task WriteAsync(RegistryLocation location, string canonicalValue, TweakValueDataType dataType, CancellationToken ct = default);

    Task DeleteValueAsync(RegistryLocation location, CancellationToken ct = default);

    /// <summary>
    /// Причина, по которой настройки ветки пользователя сейчас недоступны, или null, если они доступны.
    ///
    /// Нужна из-за того, что служба работает под LocalSystem: её HKCU — это профиль системы.
    /// Писать туда настройку пользователя и рапортовать «применено» — значит показывать изменение,
    /// которого человек не увидит. Реализации по умолчанию (фейки в тестах, не-Windows) считают,
    /// что проблемы нет.
    /// </summary>
    string? DescribeUserHiveProblem() => null;
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public static CommandResult Ok(string stdout) => new(0, stdout, string.Empty);

    public static CommandResult Failed(string message) => new(-1, string.Empty, message);
}

/// <summary>
/// Запуск внешних утилит (netsh, powercfg). Аргументы передаются списком: никакой склейки строк,
/// поэтому значение из системы не может превратиться в дополнительный ключ команды.
/// </summary>
public interface ICommandPort
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

public sealed record NicAdapterRef(
    string InterfaceGuid,
    string DisplayName,
    bool IsUp,
    bool IsPhysical,
    bool IsVirtual)
{
    public TweakTarget ToTarget() => new(InterfaceGuid, DisplayName, $"NIC {DisplayName} ({InterfaceGuid})");
}

public sealed record NicPropertySnapshot(
    bool Exists,
    string? CanonicalValue,
    IReadOnlyList<string> AllowedValues,
    bool IsSupported,
    string SupportReasonRu)
{
    public static NicPropertySnapshot Unsupported(string reason) =>
        new(false, null, Array.Empty<string>(), false, reason);

    public static NicPropertySnapshot Present(string canonicalValue, IReadOnlyList<string> allowed) =>
        new(true, canonicalValue, allowed, true, "Свойство доступно.");
}

/// <summary>
/// Дополнительные свойства сетевого адаптера (то, что видно в «Диспетчере устройств → Дополнительно»).
/// </summary>
public interface INicPropertyPort
{
    Task<IReadOnlyList<NicAdapterRef>> ListAdaptersAsync(CancellationToken ct = default);

    Task<NicPropertySnapshot> ReadAsync(TweakTarget target, string propertyKeyword, CancellationToken ct = default);

    Task WriteAsync(TweakTarget target, string propertyKeyword, string canonicalValue, CancellationToken ct = default);
}

/// <summary>Определение активного адаптера, через который идёт игровой трафик.</summary>
public interface IActiveAdapterResolver
{
    Task<TweakTarget> ResolveActiveAdapterAsync(CancellationToken ct = default);
}
