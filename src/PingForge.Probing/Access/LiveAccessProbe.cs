using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace PingForge.Probing.Access;

/// <summary>Один замер доступа к имени. Ничего не меняет: только соединения и чтение ответа.</summary>
public interface IAccessProbe
{
    Task<AccessObservation> ProbeAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// Живой замер доступа: DNS → TCP → TLS → (при отказе TLS) обычный HTTP.
///
/// Что важно и что нет:
///   • сертификат НЕ проверяется на доверие: задача — узнать, дошло ли рукопожатие, а не доверять сайту;
///   • ничего не отправляется, кроме обычного запроса «открой главную» — как у браузера;
///   • ни один пакет не подменяется и не задерживается: перехвата в программе нет.
///
/// Границы честности: замер говорит, что видно на этом пути. Отличить «провайдер рвёт соединение» от
/// «сервер сам рвёт соединение» по одному запросу нельзя — это и написано в выводе.
/// </summary>
public sealed class LiveAccessProbe : IAccessProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    private readonly int _port;

    public LiveAccessProbe(int port = 443) => _port = port;

    public async Task<AccessObservation> ProbeAsync(string host, CancellationToken ct = default)
    {
        string name = (host ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return AccessObservation.NotMeasured(string.Empty);
        }

        // Имя могло прийти с «http://» и путём — оставляем только хост, иначе DNS не поймёт.
        name = NormalizeHost(name);
        if (name.Length == 0)
        {
            return AccessObservation.NotMeasured(string.Empty);
        }

        var stopwatch = Stopwatch.StartNew();
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return new AccessObservation(name, _port, DnsFailure: Shorten(ex));
        }

        int dnsMs = (int)stopwatch.ElapsedMilliseconds;
        if (addresses.Length == 0)
        {
            return new AccessObservation(name, _port, DnsMilliseconds: dnsMs, DnsFailure: "пустой ответ DNS");
        }

        IPAddress address = addresses[0];
        using var client = new TcpClient(address.AddressFamily);
        var connectWatch = Stopwatch.StartNew();
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(address, _port, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            // Отказ и недоступность — разные выводы, поэтому различаем их явным признаком, а не догадкой.
            bool refused = ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
            return new AccessObservation(
                name,
                _port,
                DnsMilliseconds: dnsMs,
                AddressCount: addresses.Length,
                FirstAddress: address.ToString(),
                ConnectFailure: Shorten(ex),
                ConnectionRefused: refused);
        }

        int connectMs = (int)connectWatch.ElapsedMilliseconds;

        var observation = new AccessObservation(
            name,
            _port,
            DnsMilliseconds: dnsMs,
            AddressCount: addresses.Length,
            FirstAddress: address.ToString(),
            ConnectMilliseconds: connectMs);

        // TLS:AcceptAnyCertificate — узнать, дошло ли рукопожатие. Доверять сертификату здесь не нужно.
        string? tlsFailure = null;
        int? tlsMs = null;
        var tlsWatch = Stopwatch.StartNew();
        try
        {
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, _, _, _) => true);
            using var tlsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tlsTimeout.CancelAfter(TlsTimeout);
            var options = new SslClientAuthenticationOptions { TargetHost = name };
            await ssl.AuthenticateAsClientAsync(options, tlsTimeout.Token).ConfigureAwait(false);
            if (!ssl.IsAuthenticated)
            {
                tlsFailure = "рукопожатие не завершилось";
            }
            else
            {
                tlsMs = (int)tlsWatch.ElapsedMilliseconds;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tlsFailure = "таймаут TLS: сервер молчит";
        }
        catch (Exception ex)
        {
            tlsFailure = Shorten(ex);
        }

        if (tlsMs is not null)
        {
            return observation with { TlsMilliseconds = tlsMs };
        }

        // TLS не поднялся: смотрим, не подменил ли провайдер обычный HTTP-ответ заглушкой.
        (int? status, string? location, bool stub) = await ProbeHttpAsync(name, ct).ConfigureAwait(false);
        return observation with
        {
            TlsFailure = tlsFailure,
            HttpStatus = status,
            HttpLocation = location,
            HttpLooksLikeStub = stub,
        };
    }

    /// <summary>Убрать «http://», «https://», путь и порт: DNS ждёт имя, а не адресную строку.</summary>
    public static string NormalizeHost(string input)
    {
        string value = input.Trim();
        int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            value = value[(schemeEnd + 3)..];
        }

        int slash = value.IndexOfAny(['/', '?', '#']);
        if (slash >= 0)
        {
            value = value[..slash];
        }

        int colon = value.IndexOf(':');
        if (colon > 0)
        {
            value = value[..colon];
        }

        return value.Trim().TrimEnd('.');
    }

    private static async Task<(int? Status, string? Location, bool Stub)> ProbeHttpAsync(string host, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HttpTimeout);
            await client.ConnectAsync(host, 80, timeout.Token).ConfigureAwait(false);

            using NetworkStream stream = client.GetStream();
            byte[] request = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: PingForge-access-check\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int total = 0;
            while (total < 16384)
            {
                int read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                total += read;
            }

            string text = Encoding.UTF8.GetString(buffer.ToArray());
            int? status = ParseStatus(text);
            string? location = ParseHeader(text, "Location");
            bool stub = AccessClassifier.LooksLikeStub(status, location, text);
            return (status, location, stub);
        }
        catch (Exception)
        {
            // HTTP-проверка не удалась — это не повод выдумывать ответ: остаётся только то, что видели.
            return (null, null, false);
        }
    }

    private static int? ParseStatus(string response)
    {
        int space = response.IndexOf(' ');
        if (space <= 0 || !response.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return null;
        }

        int end = response.IndexOfAny([' ', '\r', '\n'], space + 1);
        string code = end > space ? response[(space + 1)..end] : string.Empty;
        return int.TryParse(code, out int value) && value is >= 100 and <= 599 ? value : null;
    }

    private static string? ParseHeader(string response, string name)
    {
        foreach (string line in response.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                break; // конец заголовков
            }

            if (trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(name.Length + 1)..].Trim();
            }
        }

        return null;
    }

    private static string Shorten(Exception ex)
    {
        string message = ex switch
        {
            SocketException socket => socket.SocketErrorCode switch
            {
                SocketError.ConnectionReset => "соединение сброшено (RST)",
                SocketError.ConnectionRefused => "в соединении отказано",
                SocketError.TimedOut => "таймаут",
                SocketError.HostUnreachable => "узел недостижим",
                SocketError.NetworkUnreachable => "сеть недостижима",
                _ => socket.SocketErrorCode.ToString(),
            },
            _ => ex.Message,
        };

        string inner = ex.InnerException is null ? string.Empty : $" ({Shorten(ex.InnerException)})";
        string text = $"{ex.GetType().Name}: {message}{inner}";
        return text.Length <= 220 ? text : text[..220];
    }
}
