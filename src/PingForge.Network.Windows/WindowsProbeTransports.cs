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

/// <summary>
/// Настоящий UDP-транспорт DNS-запросов для бенчмарка резолверов. До задачи F контракт
/// <see cref="IDnsQueryTransport"/> существовал только с двойником из тестов: бенчмарк был
/// «мёртвым» кодом. Транспорт собирает минимальный A-запрос (заголовок + вопрос), отправляет
/// на 53-й порт и разбирает ответ ровно настолько, насколько нужно бенчмаркеру для проверки:
/// ID, признак ответа, имя вопроса, код возврата и число ответов.
///
/// Валидация — не здесь: транспорт честно возвращает null при таймауте и любое число ответов
/// как есть, а решения (перехват, доля успеха, скорость) принимает бенчмаркер.
/// </summary>
public sealed class UdpDnsQueryTransport : IDnsQueryTransport
{
    public async Task<DnsRawResponse?> QueryAsync(
        IPAddress server,
        string domain,
        ushort transactionId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253)
        {
            return null;
        }

        byte[] query = BuildQuery(domain, transactionId);

        try
        {
            using var client = new UdpClient();
            client.Connect(new IPEndPoint(server, 53));
            await client.SendAsync(query, query.Length, ct).ConfigureAwait(false);

            Task<UdpReceiveResult> receive = client.ReceiveAsync(ct);
            Task completed = await Task.WhenAny(receive, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != receive || !receive.IsCompletedSuccessfully)
            {
                return null;   // таймаут или отмена — это «нет ответа», а не «пустой ответ»
            }

            UdpReceiveResult result = receive.Result;
            return ParseResponse(result.Buffer, result.RemoteEndPoint);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Минимальный DNS-запрос: заголовок (RD=1, QDCOUNT=1) + вопрос A/IN.</summary>
    private static byte[] BuildQuery(string domain, ushort transactionId)
    {
        using var buffer = new MemoryStream();
        buffer.Write([(byte)(transactionId >> 8), (byte)(transactionId & 0xFF)]);        // ID
        buffer.Write([0x01, 0x00]);                                                     // флаги: рекурсия желательна
        buffer.Write([0x00, 0x01]);                                                     // QDCOUNT = 1
        buffer.Write([0x00, 0x00]);                                                     // ANCOUNT
        buffer.Write([0x00, 0x00]);                                                     // NSCOUNT
        buffer.Write([0x00, 0x00]);                                                     // ARCOUNT

        foreach (string label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            buffer.Write([(byte)bytes.Length]);
            buffer.Write(bytes);
        }

        buffer.Write([0x00]);                                                           // конец имени
        buffer.Write([0x00, 0x01]);                                                     // QTYPE = A
        buffer.Write([0x00, 0x01]);                                                     // QCLASS = IN
        return buffer.ToArray();
    }

    /// <summary>Разбор ответа до полей, которые проверяет бенчмаркер. Битый пакет → null.</summary>
    private static DnsRawResponse? ParseResponse(byte[] data, IPEndPoint source)
    {
        if (data.Length < 12)
        {
            return null;
        }

        ushort id = (ushort)((data[0] << 8) | data[1]);
        bool isResponse = (data[2] & 0x80) != 0;
        int responseCode = data[3] & 0x0F;
        int answerCount = (data[6] << 8) | data[7];

        // Имя вопроса: метки до нулевого байта; сжатие (указатель 0xC0…) — следуем один раз.
        int offset = 12;
        var name = new System.Text.StringBuilder();
        int jumps = 0;
        while (offset < data.Length && jumps < 2)
        {
            byte length = data[offset];
            if (length == 0)
            {
                offset++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= data.Length || jumps > 0)
                {
                    return null;
                }

                offset = ((length & 0x3F) << 8) | data[offset + 1];
                jumps++;
                continue;
            }

            if ((length & 0xC0) != 0 || offset + 1 + length > data.Length)
            {
                return null;
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            name.Append(System.Text.Encoding.ASCII.GetString(data, offset + 1, length));
            offset += 1 + length;
        }

        return new DnsRawResponse(source, id, isResponse, name.Length > 0 ? name.ToString() : "?", responseCode, answerCount);
    }
}
