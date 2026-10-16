using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;

namespace PingForge.Tests;

/// <summary>Фейковый реестр: хранит значения в памяти, умеет «проглатывать» запись для проверки честности.</summary>
public sealed class FakeRegistryPort : IRegistryPort
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Если true — порт делает вид, что записал, но значение не меняет (имитация прав/политики).</summary>
    public bool IgnoreWrites { get; set; }

    public int WriteCount { get; private set; }

    public int DeleteCount { get; private set; }

    public void Seed(RegistryLocation location, string? value) => _values[Key(location)] = value;

    public string? Get(RegistryLocation location) => _values.TryGetValue(Key(location), out string? value) ? value : null;

    public bool Exists(RegistryLocation location) => _values.ContainsKey(Key(location));

    public Task<RegistryValueSnapshot> ReadAsync(RegistryLocation location, CancellationToken ct = default)
    {
        bool exists = _values.TryGetValue(Key(location), out string? value);
        return Task.FromResult(exists
            ? new RegistryValueSnapshot(true, value, TweakValueDataType.DWord)
            : RegistryValueSnapshot.Missing(TweakValueDataType.DWord));
    }

    public Task WriteAsync(RegistryLocation location, string canonicalValue, TweakValueDataType dataType, CancellationToken ct = default)
    {
        WriteCount++;
        if (!IgnoreWrites)
        {
            _values[Key(location)] = canonicalValue;
        }

        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(RegistryLocation location, CancellationToken ct = default)
    {
        DeleteCount++;
        if (!IgnoreWrites)
        {
            _values.Remove(Key(location));
        }

        return Task.CompletedTask;
    }

    private static string Key(RegistryLocation location) => location.ToString();
}

public sealed class FakeNicPropertyPort : INicPropertyPort
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NicAdapterRef> _adapters = new();

    public bool IgnoreWrites { get; set; }

    public int WriteCount { get; private set; }

    public void SeedAdapter(string interfaceGuid, string displayName, bool isUp = true) =>
        _adapters.Add(new NicAdapterRef(interfaceGuid, displayName, isUp, IsPhysical: true, IsVirtual: false));

    public void SeedProperty(string interfaceGuid, string keyword, string? value, params string[] allowed)
    {
        string key = $"{interfaceGuid}|{keyword}";
        _values[key] = value;
        _allowed[key] = allowed;
    }

    public string? Get(string interfaceGuid, string keyword) =>
        _values.TryGetValue($"{interfaceGuid}|{keyword}", out string? value) ? value : null;

    public Task<IReadOnlyList<NicAdapterRef>> ListAdaptersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NicAdapterRef>>(_adapters);

    public Task<NicPropertySnapshot> ReadAsync(TweakTarget target, string propertyKeyword, CancellationToken ct = default)
    {
        string key = $"{target.AdapterInterfaceGuid}|{propertyKeyword}";
        if (!_values.TryGetValue(key, out string? value))
        {
            return Task.FromResult(NicPropertySnapshot.Unsupported($"Свойство «{propertyKeyword}» отсутствует у адаптера."));
        }

        return Task.FromResult(NicPropertySnapshot.Present(value ?? string.Empty, _allowed[key]));
    }

    public Task WriteAsync(TweakTarget target, string propertyKeyword, string canonicalValue, CancellationToken ct = default)
    {
        WriteCount++;
        if (!IgnoreWrites)
        {
            _values[$"{target.AdapterInterfaceGuid}|{propertyKeyword}"] = canonicalValue;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeCommandPort : ICommandPort
{
    private readonly List<string> _invocations = new();

    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } =
        (_, _) => CommandResult.Ok(string.Empty);

    public IReadOnlyList<string> Invocations => _invocations;

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        _invocations.Add($"{fileName} {string.Join(' ', arguments)}");
        return Task.FromResult(Handler(fileName, arguments));
    }
}

public sealed class FakeActiveAdapterResolver : IActiveAdapterResolver
{
    public FakeActiveAdapterResolver(string interfaceGuid = "{GUID-ADAPTER-A}", string displayName = "Ethernet")
    {
        Target = new TweakTarget($"NIC {displayName}", interfaceGuid, displayName);
    }

    public TweakTarget Target { get; }

    public int CallCount { get; private set; }

    public Task<TweakTarget> ResolveActiveAdapterAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(Target);
    }
}

public sealed class FakeMtuAdvice : IMtuAdviceProvider
{
    private readonly ComputedTargetValue _value;

    public FakeMtuAdvice(string? mtu = "1492", string explanation = "Path MTU найден DF-пробами: 1492 байта.")
    {
        _value = mtu is null
            ? ComputedTargetValue.Unavailable("ICMP заблокирован: определить MTU нельзя.")
            : new ComputedTargetValue(mtu, TweakValueDataType.DWord, explanation);
    }

    public int CallCount { get; private set; }

    public Task<ComputedTargetValue> GetRecommendedMtuAsync(TweakTarget target, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_value);
    }
}

public sealed class FakeDnsAdvice : IDnsAdviceProvider
{
    private readonly ComputedTargetValue _value;

    public FakeDnsAdvice(string? servers = "1.1.1.1|8.8.8.8", string explanation = "Лучший warm-отклик: 1.1.1.1 (9.4 мс).")
    {
        _value = servers is null
            ? ComputedTargetValue.Unavailable("Бенчмарк DNS не запускался.")
            : new ComputedTargetValue(servers, TweakValueDataType.MultiString, explanation);
    }

    public Task<ComputedTargetValue> GetRecommendedDnsAsync(TweakTarget target, CancellationToken ct = default) =>
        Task.FromResult(_value);
}

public sealed class FakeNicOptions : INicPropertyOptionProvider
{
    private readonly IReadOnlyList<string> _allowed;

    public FakeNicOptions(params string[] allowed) => _allowed = allowed;

    public Task<IReadOnlyList<string>> GetAllowedValuesAsync(TweakTarget target, string keyword, CancellationToken ct = default) =>
        Task.FromResult(_allowed);
}

/// <summary>
/// Эмулятор netsh: держит состояние MTU, DNS и глобальных параметров TCP и меняет его
/// ровно так же, как живая утилита. Нужен, чтобы тесты проверяли не «вызвали команду»,
/// а «система действительно изменилась и вернулась назад».
/// </summary>
public sealed class FakeNetshEmulator : ICommandPort
{
    private readonly Dictionary<string, int> _mtuByInterface = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _dnsByInterface = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tcpGlobal = new(StringComparer.OrdinalIgnoreCase)
    {
        ["receivewindowautotuninglevel"] = "normal",
        ["receivesidescalingstate"] = "enabled",
        ["ecncapability"] = "disabled",
        ["maxsynretransmissions"] = "4"
    };

    private readonly List<string> _invocations = new();

    public IReadOnlyList<string> Invocations => _invocations;

    /// <summary>Если задано — команда «падает» с этим текстом (проверка честных сообщений об отказе).</summary>
    public Func<IReadOnlyList<string>, string?>? FailWhen { get; set; }

    public void SeedInterface(string name, int mtu, params string[] dnsServers)
    {
        _mtuByInterface[name] = mtu;
        _dnsByInterface[name] = dnsServers.ToList();
    }

    public int? GetMtu(string name) => _mtuByInterface.TryGetValue(name, out int mtu) ? mtu : null;

    public IReadOnlyList<string> GetDns(string name) =>
        _dnsByInterface.TryGetValue(name, out List<string>? servers) ? servers : Array.Empty<string>();

    public string? GetTcpGlobal(string canonicalLabel) =>
        _tcpGlobal.TryGetValue(canonicalLabel, out string? value) ? value : null;

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        _invocations.Add($"{fileName} {string.Join(' ', arguments)}");

        string? failure = FailWhen?.Invoke(arguments);
        if (failure is not null)
        {
            return Task.FromResult(new CommandResult(1, string.Empty, failure));
        }

        // netsh int tcp show global
        if (arguments.Contains("tcp") && arguments.Contains("show") && arguments.Contains("global"))
        {
            string text = string.Join("\r\n", _tcpGlobal.Select(kv => $"{kv.Key} : {kv.Value}"));
            return Task.FromResult(CommandResult.Ok(text));
        }

        // netsh int tcp set global key=value
        if (arguments.Contains("tcp") && arguments.Contains("set") && arguments.Contains("global"))
        {
            string assignment = arguments.Last();
            int eq = assignment.IndexOf('=');
            if (eq <= 0)
            {
                return Task.FromResult(new CommandResult(1, string.Empty, "синтаксис: параметр=значение"));
            }

            _tcpGlobal[assignment[..eq]] = assignment[(eq + 1)..];
            return Task.FromResult(CommandResult.Ok("Параметр обновлён."));
        }

        // netsh int ipv4 show subinterfaces
        if (arguments.Contains("subinterfaces") && arguments.Contains("show"))
        {
            var lines = new List<string> { "MTU     Состояние     Админ. состояние    Тип          Имя интерфейса" };
            lines.AddRange(_mtuByInterface.Select(kv => $"{kv.Value}    Подключено    Включено            Выделенный   {kv.Key}"));
            return Task.FromResult(CommandResult.Ok(string.Join("\r\n", lines)));
        }

        // netsh int ipv4 set subinterface <name> mtu=N store=persistent
        if (arguments.Contains("subinterface") && arguments.Contains("set"))
        {
            string name = arguments[4];
            string mtuArgument = arguments.FirstOrDefault(a => a.StartsWith("mtu=", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("эмулятор netsh: нет аргумента mtu=");

            _mtuByInterface[name] = int.Parse(mtuArgument[4..], System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(CommandResult.Ok(string.Empty));
        }

        // netsh int ipv4 show dnsservers name=X
        if (arguments.Contains("dnsservers") && arguments.Contains("show"))
        {
            string name = Value(arguments, "name=");
            IReadOnlyList<string> servers = GetDns(name);
            string text = servers.Count == 0
                ? $"Настроенные DNS-серверы для интерфейса \"{name}\"\r\nНастроен автоматически"
                : $"Настроенные DNS-серверы для интерфейса \"{name}\"\r\n" + string.Join("\r\n", servers);
            return Task.FromResult(CommandResult.Ok(text));
        }

        // netsh int ipv4 set dnsservers name=X static <ip> primary validate=no
        if (arguments.Contains("dnsservers") && arguments.Contains("static"))
        {
            string name = Value(arguments, "name=");
            string ip = arguments[Array.IndexOf(arguments.ToArray(), "static") + 1];
            _dnsByInterface[name] = new List<string> { ip };
            return Task.FromResult(CommandResult.Ok(string.Empty));
        }

        // netsh int ipv4 add dnsservers name=X <ip> index=N validate=no
        if (arguments.Contains("dnsservers") && arguments.Contains("add"))
        {
            string name = Value(arguments, "name=");
            int index = Array.IndexOf(arguments.ToArray(), "dnsservers");
            string ip = arguments[index + 2];
            if (!_dnsByInterface.TryGetValue(name, out List<string>? servers))
            {
                servers = new List<string>();
                _dnsByInterface[name] = servers;
            }

            if (!servers.Contains(ip, StringComparer.Ordinal))
            {
                servers.Add(ip);
            }

            return Task.FromResult(CommandResult.Ok(string.Empty));
        }

        // netsh int ipv4 set dnsservers name=X source=dhcp
        if (arguments.Contains("dnsservers") && arguments.Contains("source=dhcp"))
        {
            _dnsByInterface[Value(arguments, "name=")] = new List<string>();
            return Task.FromResult(CommandResult.Ok(string.Empty));
        }

        return Task.FromResult(CommandResult.Ok(string.Empty));
    }

    private static string Value(IReadOnlyList<string> arguments, string prefix) =>
        arguments.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? string.Empty;
}
