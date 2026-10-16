using System.Globalization;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// MTU конкретного сетевого подключения. Читается из таблицы подынтерфейсов, пишется одной командой
/// с `store=persistent`, чтобы значение пережило перезагрузку.
/// </summary>
public sealed class NetshSubinterfaceMtuAccessor : IValueAccessor
{
    private readonly ICommandPort _port;

    public NetshSubinterfaceMtuAccessor(ICommandPort port) =>
        _port = port ?? throw new ArgumentNullException(nameof(port));

    public TweakMechanismKind Mechanism => TweakMechanismKind.MtuSubinterface;

    public bool IsAdapterScoped => true;

    public async Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target.AdapterDisplayName))
        {
            return TweakSupport.Unsupported(
                "Неизвестно имя сетевого подключения (например, «Ethernet»), а без него netsh не найдёт интерфейс.");
        }

        CommandResult result = await _port
            .RunAsync("netsh", new[] { "interface", "ipv4", "show", "subinterfaces" }, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return TweakSupport.Unsupported(
                $"netsh не смог показать подынтерфейсы (код {result.ExitCode}): " +
                NetshOutputParser.FirstMeaningfulLine(result.StandardError, result.StandardOutput));
        }

        return NetshOutputParser.ParseSubinterfaceMtu(result.StandardOutput, target.AdapterDisplayName!) is not null
            ? TweakSupport.Supported($"Подынтерфейс «{target.AdapterDisplayName}» найден.")
            : TweakSupport.Unsupported($"В таблице подынтерфейсов нет «{target.AdapterDisplayName}».");
    }

    public async Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default)
    {
        CommandResult result = await _port
            .RunAsync("netsh", new[] { "interface", "ipv4", "show", "subinterfaces" }, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (false, null);
        }

        int? mtu = NetshOutputParser.ParseSubinterfaceMtu(result.StandardOutput, target.AdapterDisplayName ?? string.Empty);
        return mtu is null
            ? (false, null)
            : (true, mtu.Value.ToString(CultureInfo.InvariantCulture));
    }

    public async Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default)
    {
        if (!int.TryParse(canonicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mtu) ||
            mtu is < 576 or > 9216)
        {
            throw new InvalidOperationException($"Значение MTU «{canonicalValue}» вне допустимого диапазона 576…9216.");
        }

        CommandResult result = await _port.RunAsync(
            "netsh",
            new[]
            {
                "interface", "ipv4", "set", "subinterface",
                target.AdapterDisplayName ?? string.Empty,
                $"mtu={mtu.ToString(CultureInfo.InvariantCulture)}",
                "store=persistent"
            },
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"netsh отказал в установке MTU (код {result.ExitCode}): " +
                NetshOutputParser.FirstMeaningfulLine(result.StandardError, result.StandardOutput));
        }
    }

    public Task DeleteAsync(TweakTarget target, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "MTU подынтерфейса нельзя «удалить»: у каждого интерфейса MTU есть всегда. " +
            "Прежнее значение берётся из снапшота и записывается обратно.");
}

/// <summary>
/// DNS-серверы сетевого подключения. Список хранится канонически как «ip1|ip2».
/// Если до оптимизации адреса приходили от DHCP, откат возвращает именно DHCP, а не вписывает произвольный адрес.
/// </summary>
public sealed class NetshDnsServersAccessor : IValueAccessor
{
    private readonly ICommandPort _port;

    public NetshDnsServersAccessor(ICommandPort port) =>
        _port = port ?? throw new ArgumentNullException(nameof(port));

    public TweakMechanismKind Mechanism => TweakMechanismKind.DnsResolverConfig;

    public bool IsAdapterScoped => true;

    public async Task<TweakSupport> CheckSupportAsync(TweakTarget target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target.AdapterDisplayName))
        {
            return TweakSupport.Unsupported("Неизвестно имя сетевого подключения — netsh не найдёт интерфейс.");
        }

        CommandResult result = await _port.RunAsync(
            "netsh",
            new[] { "interface", "ipv4", "show", "dnsservers", $"name={target.AdapterDisplayName}" },
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return TweakSupport.Unsupported(
                $"netsh не смог показать DNS-серверы (код {result.ExitCode}): " +
                NetshOutputParser.FirstMeaningfulLine(result.StandardError, result.StandardOutput));
        }

        return TweakSupport.Supported($"DNS-серверы интерфейса «{target.AdapterDisplayName}» доступны для чтения.");
    }

    public async Task<(bool Exists, string? Raw)> ReadAsync(TweakTarget target, CancellationToken ct = default)
    {
        CommandResult result = await _port.RunAsync(
            "netsh",
            new[] { "interface", "ipv4", "show", "dnsservers", $"name={target.AdapterDisplayName}" },
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (false, null);
        }

        IReadOnlyList<string> servers = NetshOutputParser.ParseIpv4DnsServers(result.StandardOutput);
        return servers.Count == 0
            ? (false, null)
            : (true, NetshOutputParser.JoinServers(servers));
    }

    public async Task WriteAsync(TweakTarget target, string canonicalValue, CancellationToken ct = default)
    {
        IReadOnlyList<string> servers = NetshOutputParser.SplitServers(canonicalValue);
        if (servers.Count == 0)
        {
            throw new InvalidOperationException("Список DNS-серверов пуст — применять нечего.");
        }

        foreach (string server in servers)
        {
            if (!System.Net.IPAddress.TryParse(server, out _))
            {
                throw new InvalidOperationException($"«{server}» не является IP-адресом DNS-сервера.");
            }
        }

        string name = target.AdapterDisplayName ?? string.Empty;

        CommandResult first = await _port.RunAsync(
            "netsh",
            new[] { "interface", "ipv4", "set", "dnsservers", $"name={name}", "static", servers[0], "primary", "validate=no" },
            ct).ConfigureAwait(false);

        if (!first.Succeeded)
        {
            throw new InvalidOperationException(
                $"netsh отказал в установке основного DNS (код {first.ExitCode}): " +
                NetshOutputParser.FirstMeaningfulLine(first.StandardError, first.StandardOutput));
        }

        for (int i = 1; i < servers.Count; i++)
        {
            CommandResult extra = await _port.RunAsync(
                "netsh",
                new[]
                {
                    "interface", "ipv4", "add", "dnsservers", $"name={name}", servers[i],
                    $"index={i + 1}", "validate=no"
                },
                ct).ConfigureAwait(false);

            if (!extra.Succeeded)
            {
                throw new InvalidOperationException(
                    $"netsh отказал в добавлении DNS «{servers[i]}» (код {extra.ExitCode}): " +
                    NetshOutputParser.FirstMeaningfulLine(extra.StandardError, extra.StandardOutput) +
                    " Первый сервер уже установлен — верните прежние настройки кнопкой «Откатить».");
            }
        }
    }

    public async Task DeleteAsync(TweakTarget target, CancellationToken ct = default)
    {
        // Так выглядит «вернуть как было», если адреса приходили от DHCP и мы их перезаписали.
        CommandResult result = await _port.RunAsync(
            "netsh",
            new[] { "interface", "ipv4", "set", "dnsservers", $"name={target.AdapterDisplayName}", "source=dhcp" },
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"netsh отказал в возврате DNS на автоматическое получение (код {result.ExitCode}): " +
                NetshOutputParser.FirstMeaningfulLine(result.StandardError, result.StandardOutput));
        }
    }
}
