using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PingForge.Probing;

namespace PingForge.Network.Windows;

/// <summary>
/// ICMP-эхо настоящим <see cref="Ping"/> из .NET.
///
/// Чего здесь нет: «своего» сырого сокета. Он был бы точнее в редких случаях, но требует прав
/// администратора — а значит, замеры переставали бы работать в обычном запуске. Семья Windows
/// и так разрешает ICMP-эхо обычному пользователю.
///
/// Отказ не скрывается: каждая причина (таймаут, «сеть недостижима», «пакет не влезает», нет прав)
/// возвращается отдельным статусом — по ним проверка маршрута решает, пробовать ли TCP.
/// </summary>
public sealed class SystemNetIcmpProbe : IIcmpProbe
{
    /// <summary>Эхо-запрос в Windows не требует администратора — это и есть причина, почему он первый.</summary>
    public bool RequiresAdministratorRights => false;

    public async Task<IcmpProbeReply> SendAsync(
        IPAddress target,
        int payloadBytes,
        bool dontFragment,
        int ttl,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var options = new PingOptions(Math.Clamp(ttl, 1, 255), dontFragment);
        byte[] payload = new byte[Math.Clamp(payloadBytes, 0, 65_000)];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        try
        {
            using var ping = new Ping();
            PingReply reply = await ping
                .SendPingAsync(target, timeout, payload, options, ct)
                .ConfigureAwait(false);

            return reply.Status switch
            {
                IPStatus.Success => new IcmpProbeReply(IcmpProbeStatus.Success, reply.RoundtripTime, reply.Address),
                IPStatus.TimedOut => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null),
                IPStatus.PacketTooBig => new IcmpProbeReply(IcmpProbeStatus.PacketTooBig, 0, reply.Address),
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
                    or IPStatus.DestinationPortUnreachable or IPStatus.DestinationUnreachable
                    => new IcmpProbeReply(IcmpProbeStatus.TargetUnreachable, 0, reply.Address),
                _ => new IcmpProbeReply(IcmpProbeStatus.Other, 0, reply.Address),
            };
        }
        catch (PingException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied })
        {
            // Сюда приходят и «нет прав», и блокировка ICMP политикой/антивирусом.
            return new IcmpProbeReply(IcmpProbeStatus.PermissionDenied, 0, null);
        }
        catch (PingException)
        {
            return new IcmpProbeReply(IcmpProbeStatus.Other, 0, null);
        }
        catch (OperationCanceledException)
        {
            return new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null);
        }
    }
}

/// <summary>
/// TCP-рукопожатие с замером времени до ответа.
///
/// Важная деталь, которую легко испортить: <b>RST — это валидный сэмпл</b>. Если порт закрыт,
/// сервер всё равно ответил за измеряемое время, и спешить с выводом «не измеряется» нельзя.
/// Ошибка по времени и «сеть недостижима» — разные вещи, и они возвращаются разными статусами.
/// </summary>
public sealed class SocketTcpHandshakeProbe : ITcpHandshakeProbe
{
    public async Task<TcpProbeReply> TryConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var watch = Stopwatch.StartNew();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(target, limit.Token).ConfigureAwait(false);
            watch.Stop();
            return new TcpProbeReply(TcpProbeStatus.Established, watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            watch.Stop();
            return new TcpProbeReply(TcpProbeStatus.Timeout, watch.Elapsed.TotalMilliseconds);
        }
        catch (SocketException ex)
        {
            watch.Stop();
            return ex.SocketErrorCode switch
            {
                // Порт закрыт, но ответ пришёл — это время ответа, а не отказ.
                SocketError.ConnectionRefused => new TcpProbeReply(TcpProbeStatus.ConnectionRefused, watch.Elapsed.TotalMilliseconds),
                SocketError.TimedOut => new TcpProbeReply(TcpProbeStatus.Timeout, watch.Elapsed.TotalMilliseconds),
                SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown
                    or SocketError.NetworkDown
                    => new TcpProbeReply(TcpProbeStatus.Unreachable, watch.Elapsed.TotalMilliseconds),
                SocketError.AccessDenied => new TcpProbeReply(TcpProbeStatus.PermissionDenied, watch.Elapsed.TotalMilliseconds),
                _ => new TcpProbeReply(TcpProbeStatus.Unreachable, watch.Elapsed.TotalMilliseconds),
            };
        }
    }
}

/// <summary>Разрешение имени в IP обычным системным DNS. Пустой ответ — честный «не разрешилось».</summary>
public sealed class SystemHostResolver : IHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return [parsed];
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return [];
        }
    }
}
