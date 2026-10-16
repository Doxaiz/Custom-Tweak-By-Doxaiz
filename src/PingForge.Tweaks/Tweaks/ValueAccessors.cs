using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Доступ к одному конкретному параметру системы. Всё, что ниже этого интерфейса, — платформа
/// (реестр, WMI, netsh); всё, что выше — одинаковая для всех твиков логика применения, проверки и откатa.
/// </summary>
public interface IValueAccessor
{
    TweakMechanismKind Mechanism { get; }

    /// <summary>Нужен ли этому параметру конкретный сетевой адаптер (GUID/имя берутся из цели снапшота).</summary>
    bool IsAdapterScoped { get; }

    /// <summary>Работает ли параметр на этой машине и на этом адаптере.</summary>
    Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default);

    Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default);

    Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default);

    Task DeleteAsync(TweakTarget target, CancellationToken ct = default);
}

/// <summary>
/// Значение в реестре. Путь может быть привязан к GUID адаптера: подстановка выполняется в момент
/// вызова по цели из снапшота, поэтому смена активного адаптера не уводит запись на чужой NIC.
/// </summary>
public sealed class RegistryValueAccessor : IValueAccessor
{
    public const string AdapterGuidToken = "{adapter}";

    private readonly IRegistryPort _port;
    private readonly RegistryRootKind _root;
    private readonly string _subKeyTemplate;
    private readonly string _valueName;
    private readonly TweakValueDataType _dataType;

    public RegistryValueAccessor(
        IRegistryPort port,
        RegistryRootKind root,
        string subKeyTemplate,
        string valueName,
        TweakValueDataType dataType)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        _root = root;
        _subKeyTemplate = subKeyTemplate;
        _valueName = valueName;
        _dataType = dataType;
    }

    public TweakMechanismKind Mechanism => TweakMechanismKind.Registry;

    public bool IsAdapterScoped => _subKeyTemplate.Contains(AdapterGuidToken, StringComparison.Ordinal);

    public RegistryLocation ResolveLocation(TweakTarget target)
    {
        string path = _subKeyTemplate;
        if (IsAdapterScoped)
        {
            string guid = target.AdapterInterfaceGuid
                ?? throw new InvalidOperationException(
                    $"Твик требует конкретный сетевой адаптер, но в цели нет GUID интерфейса («{target.ResourceLocator}»).");

            path = path.Replace(AdapterGuidToken, guid, StringComparison.Ordinal);
        }

        return new RegistryLocation(_root, path, _valueName);
    }

    public Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default) =>
        Task.FromResult(IsAdapterScoped && target.AdapterInterfaceGuid is null
            ? TweakSupport.Unsupported("Активный сетевой адаптер не определён — параметр применить некуда.")
            : TweakSupport.Supported(ResolveLocation(target).ToString()));

    public async Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default)
    {
        RegistryValueSnapshot snapshot = await _port.ReadAsync(ResolveLocation(target), ct).ConfigureAwait(false);
        return (snapshot.Exists, snapshot.CanonicalValue);
    }

    public async Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default) =>
        await _port.WriteAsync(ResolveLocation(target), canonicalValue, _dataType, ct).ConfigureAwait(false);

    public async Task DeleteAsync(TweakTarget target, CancellationToken ct = default) =>
        await _port.DeleteValueAsync(ResolveLocation(target), ct).ConfigureAwait(false);
}

/// <summary>Дополнительное свойство адаптера (например, «Interrupt Moderation»).</summary>
public sealed class NicPropertyAccessor : IValueAccessor
{
    private readonly INicPropertyPort _port;
    private readonly string _keyword;

    public NicPropertyAccessor(INicPropertyPort port, string propertyKeyword)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyKeyword);
        _keyword = propertyKeyword;
    }

    public TweakMechanismKind Mechanism => TweakMechanismKind.WmiNicAdvancedProperty;

    public bool IsAdapterScoped => true;

    public string Keyword => _keyword;

    public async Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default)
    {
        NicPropertySnapshot snapshot = await _port.ReadAsync(target, _keyword, ct).ConfigureAwait(false);
        return snapshot.IsSupported
            ? TweakSupport.Supported(snapshot.SupportReasonRu)
            : TweakSupport.Unsupported(snapshot.SupportReasonRu);
    }

    public async Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default)
    {
        NicPropertySnapshot snapshot = await _port.ReadAsync(target, _keyword, ct).ConfigureAwait(false);
        return (snapshot.Exists, snapshot.CanonicalValue);
    }

    public async Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default) =>
        await _port.WriteAsync(target, _keyword, canonicalValue, ct).ConfigureAwait(false);

    public Task DeleteAsync(TweakTarget target, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Дополнительное свойство адаптера нельзя удалить: его можно только вернуть к прежнему значению из снапшота.");
}

/// <summary>
/// Глобальные настройки TCP/IP, которыми управляет netsh (`netsh int tcp show global` / `set global`).
/// Разбор вывода вынесен в NetshOutputParser и покрыт тестами на русском и английском выводе.
/// </summary>
public sealed class NetshGlobalAccessor : IValueAccessor
{
    private readonly ICommandPort _port;
    private readonly string _settingKey;

    public NetshGlobalAccessor(ICommandPort port, string settingKey)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        _settingKey = settingKey;
    }

    public TweakMechanismKind Mechanism => TweakMechanismKind.NetshCommand;

    public bool IsAdapterScoped => false;

    public string SettingKey => _settingKey;

    public async Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default)
    {
        CommandResult result = await _port
            .RunAsync("netsh", new[] { "interface", "tcp", "show", "global" }, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return TweakSupport.Unsupported(
                $"netsh не смог прочитать глобальные параметры TCP (код {result.ExitCode}). Запустите приложение от администратора.");
        }

        IReadOnlyDictionary<string, string> parsed = NetshOutputParser.ParseTcpGlobal(result.StandardOutput);
        return parsed.ContainsKey(_settingKey)
            ? TweakSupport.Supported($"netsh сообщает параметр «{_settingKey}».")
            : TweakSupport.Unsupported(
                $"В выводе netsh нет параметра «{_settingKey}» — в этой версии Windows он не поддерживается.");
    }

    public async Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default)
    {
        CommandResult result = await _port
            .RunAsync("netsh", new[] { "interface", "tcp", "show", "global" }, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (false, null);
        }

        IReadOnlyDictionary<string, string> parsed = NetshOutputParser.ParseTcpGlobal(result.StandardOutput);
        return parsed.TryGetValue(_settingKey, out string? value) ? (true, value) : (false, null);
    }

    public async Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default)
    {
        if (!TweakValueCanonicalFormatter.IsSafeCliToken(canonicalValue))
        {
            throw new InvalidOperationException(
                $"Значение «{canonicalValue}» не похоже на служебный параметр netsh — команда не будет выполнена.");
        }

        CommandResult result = await _port
            .RunAsync("netsh", new[] { "interface", "tcp", "set", "global", $"{_settingKey}={canonicalValue}" }, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"netsh отказал (код {result.ExitCode}): {NetshOutputParser.FirstMeaningfulLine(result.StandardError, result.StandardOutput)}");
        }
    }

    public Task DeleteAsync(TweakTarget target, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Глобальный параметр TCP/IP нельзя «удалить»: его можно только вернуть к прежнему значению из снапшота.");
}
