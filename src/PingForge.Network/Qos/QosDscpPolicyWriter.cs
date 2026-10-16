using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Network.Qos;

/// <summary>
/// Политика QoS Windows: набор значений в HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\&lt;имя политики&gt;.
/// Это штатный способ пометить пакеты приложения (DSCP 46, «сетевое управление») без драйвера и без
/// перехвата трафика: содержимое пакетов не меняется, фильтр не поднимается.
///
/// Две жёсткие границы, зашитые в код и проверяемые тестами:
///   • PingForge пишет и удаляет только свои политики (имя начинается с «PingForge »);
///   • значения проверяются на допустимость до записи, чтобы в реестр не попало что попало.
/// </summary>
public sealed record QosPolicySpec(
    string PolicyName,
    string ApplicationPath,
    string Protocol,
    string RemoteIp,
    int DscpValue);

public sealed record QosPolicyWriteOutcome(bool Succeeded, string MessageRu, IReadOnlyList<string> ValuesWrittenRu)
{
    public static QosPolicyWriteOutcome Ok(string message, IReadOnlyList<string> values) => new(true, message, values);

    public static QosPolicyWriteOutcome Fail(string message) => new(false, message, Array.Empty<string>());
}

public sealed record QosPolicyVerification(
    bool PolicyPresent,
    string? DscpValueRaw,
    string? ApplicationNameRaw,
    string? RemoteIpRaw,
    string MessageRu);

public sealed class QosDscpPolicyWriter
{
    /// <summary>Префикс имени политики: по нему мы узнаём свои политики и не трогаем чужие.</summary>
    public const string OwnedPolicyPrefix = "PingForge ";

    public const string PolicyRootSubKey = @"SOFTWARE\Policies\Microsoft\Windows\QoS";

    /// <summary>DSCP 46 — «Expedited Forwarding»: типовое значение для голоса и игрового трафика.</summary>
    public const int ExpeditedForwardingDscp = 46;

    private static readonly string[] TemplateValueNames =
    {
        "Version", "Application Name", "Protocol", "Local Port", "Remote Port", "Local IP", "Remote IP",
        "DSCP Value", "Throttle Rate"
    };

    private readonly IRegistryPort? _registry;

    public QosDscpPolicyWriter(IRegistryPort? registry) => _registry = registry;

    public static string PolicyNameFor(string processName, int dscp) =>
        $"{OwnedPolicyPrefix}{GameProcessCatalog.Normalize(processName)} DSCP{dscp.ToString(CultureInfo.InvariantCulture)}";

    public static bool IsOurs(string policyName) =>
        !string.IsNullOrWhiteSpace(policyName) &&
        policyName.StartsWith(OwnedPolicyPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<QosPolicyWriteOutcome> ApplyAsync(QosPolicySpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (_registry is null)
        {
            return QosPolicyWriteOutcome.Fail(
                "Порт реестра не подключён или программе не дали прав: политику QoS записать нельзя.");
        }

        if (!IsOurs(spec.PolicyName))
        {
            return QosPolicyWriteOutcome.Fail(
                $"Политика «{spec.PolicyName}» создана не PingForge: чужие политики QoS не перезаписываются.");
        }

        if (Validate(spec) is { } problem)
        {
            return QosPolicyWriteOutcome.Fail(problem);
        }

        var written = new List<string>();

        foreach ((string name, string value) in BuildValues(spec))
        {
            await _registry.WriteAsync(
                Location(spec.PolicyName, name),
                value,
                TweakValueDataType.String,
                ct).ConfigureAwait(false);

            written.Add($"{name} = {value}");
        }

        return QosPolicyWriteOutcome.Ok(
            $"Политика QoS «{spec.PolicyName}» создана. Она помечает пакеты процесса " +
            $"{(spec.ApplicationPath == "*" ? "всех приложений" : spec.ApplicationPath)} значением DSCP {spec.DscpValue}. " +
            "Выигрыш будет только там, где маршрутизатор уважает приоритет: на пустом канале разницы не будет.",
            written);
    }

    public async Task<QosPolicyWriteOutcome> RemoveAsync(QosPolicySpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (_registry is null)
        {
            return QosPolicyWriteOutcome.Fail("Порт реестра не подключён: снять политику QoS нельзя.");
        }

        if (!IsOurs(spec.PolicyName))
        {
            return QosPolicyWriteOutcome.Fail($"Политика «{spec.PolicyName}» не наша — не трогаем.");
        }

        foreach (string name in TemplateValueNames)
        {
            await _registry.DeleteValueAsync(Location(spec.PolicyName, name), ct).ConfigureAwait(false);
        }

        return QosPolicyWriteOutcome.Ok(
            "Значения политики QoS удалены. Пустой раздел реестра остаётся и ничего не делает — " +
            "это нормальное поведение Windows, а не «недоудалённый мусор».",
            Array.Empty<string>());
    }

    public async Task<QosPolicyVerification> VerifyAsync(QosPolicySpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (_registry is null)
        {
            return new QosPolicyVerification(false, null, null, null, "Порт реестра не подключён: проверить политику нельзя.");
        }

        RegistryValueSnapshot dscp = await _registry.ReadAsync(Location(spec.PolicyName, "DSCP Value"), ct).ConfigureAwait(false);
        RegistryValueSnapshot app = await _registry.ReadAsync(Location(spec.PolicyName, "Application Name"), ct).ConfigureAwait(false);
        RegistryValueSnapshot ip = await _registry.ReadAsync(Location(spec.PolicyName, "Remote IP"), ct).ConfigureAwait(false);

        bool present = dscp.Exists && app.Exists;

        return new QosPolicyVerification(
            present,
            dscp.CanonicalValue,
            app.CanonicalValue,
            ip.CanonicalValue,
            present
                ? $"Политика на месте: DSCP {dscp.CanonicalValue}, приложение {app.CanonicalValue}, адрес {ip.CanonicalValue ?? "*"}."
                : "Политики в реестре нет — применять было нечего или запись не удалась.");
    }

    private static RegistryLocation Location(string policyName, string valueName) =>
        new(RegistryRootKind.LocalMachine, $"{PolicyRootSubKey}\\{policyName}", valueName);

    private static IEnumerable<(string Name, string Value)> BuildValues(QosPolicySpec spec)
    {
        string dscp = spec.DscpValue.ToString(CultureInfo.InvariantCulture);

        yield return ("Version", "1.0");
        yield return ("Application Name", spec.ApplicationPath);
        yield return ("Protocol", spec.Protocol);
        yield return ("Local Port", "*");
        yield return ("Remote Port", "*");
        yield return ("Local IP", "*");
        yield return ("Remote IP", spec.RemoteIp);
        yield return ("DSCP Value", dscp);
        yield return ("Throttle Rate", "-1");
    }

    /// <summary>Проверка значений до записи: в реестр не должно попасть ничего лишнего.</summary>
    internal static string? Validate(QosPolicySpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.PolicyName) || spec.PolicyName.Length > 120)
        {
            return "Имя политики пустое или слишком длинное.";
        }

        if (spec.PolicyName.IndexOfAny(new[] { '\\', '/', '"' }) >= 0)
        {
            return "Имя политики содержит недопустимые символы.";
        }

        string app = spec.ApplicationPath;

        if (app != "*")
        {
            if (app.IndexOfAny(new[] { '"', '\r', '\n', '\\' }) >= 0 && !app.Contains(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Путь приложения выглядит некорректно: ожидается имя процесса вида game.exe или полный путь.";
            }

            if (app.Contains('\r') || app.Contains('\n') || app.Contains('"'))
            {
                return "Путь приложения содержит недопустимые символы.";
            }
        }

        if (spec.Protocol is not ("TCP" or "UDP" or "*"))
        {
            return $"Протокол «{spec.Protocol}» не поддерживается: только TCP, UDP или *.";
        }

        if (spec.RemoteIp != "*")
        {
            if (!IPAddress.TryParse(spec.RemoteIp, out IPAddress? ip))
            {
                return $"Адрес «{spec.RemoteIp}» не похож на IP-адрес.";
            }

            if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                return "Поддерживаются только IPv4 и IPv6 адреса.";
            }
        }

        if (spec.DscpValue is < 0 or > 63)
        {
            return $"DSCP {spec.DscpValue} вне диапазона 0…63.";
        }

        return null;
    }
}
