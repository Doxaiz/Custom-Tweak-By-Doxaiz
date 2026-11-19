using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Probing;

/// <summary>
/// Иерархия проб: живой поток игры → ICMP → TCP-handshake → честное «измерить нельзя».
///
/// Что исправлено по сравнению с наивной версией:
///   • живой поток игры проверяется первым, но провайдера живого потока в приложении нет —
///     поэтому на практике работают ICMP и TCP-рукопожатие, и это несколько пакетов сверх игры;
///   • RST (ConnectionRefused) считается ВАЛИДНЫМ сэмплом RTT, а не поводом объявить «не измеряется»;
///   • причина отказа сообщается фактическая (нет резолва / нет прав / все таймауты), а не «узел всё блокирует»;
///   • если ничего не измерили — метрики null, в UI будет «—», а не «100 % потерь»;
///   • в статусе всегда указано, каким методом получено число.
/// </summary>
public sealed class RouteProbeService : IRouteProbe
{
    public const int FullReportSampleCount = 30;
    public const int MinimumUsefulSampleCount = 5;

    private static readonly TimeSpan IcmpTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromMilliseconds(700);

    private readonly IIcmpProbe _icmp;
    private readonly ITcpHandshakeProbe _tcp;
    private readonly IHostResolver _resolver;
    private readonly HopTracerouteEngine _traceroute;
    private readonly ILiveFlowRttProvider? _liveFlow;

    public RouteProbeService(
        IIcmpProbe icmp,
        ITcpHandshakeProbe tcp,
        IHostResolver resolver,
        HopTracerouteEngine traceroute,
        ILiveFlowRttProvider? liveFlowProvider = null)
    {
        _icmp = icmp;
        _tcp = tcp;
        _resolver = resolver;
        _traceroute = traceroute;
        _liveFlow = liveFlowProvider;
    }

    /// <summary>Быстрая разведочная проба — допускается меньше 30 сэмплов, результат помечается предварительным.</summary>
    public Task<ProbeStatistics> ProbeQuickAsync(string hostOrIp, int port, string routeLabel, CancellationToken ct = default) =>
        ProbeEndpointAsync(hostOrIp, port, routeLabel, MinimumUsefulSampleCount, ct);

    public async Task<ProbeStatistics> ProbeEndpointAsync(
        string hostOrIp, int port, string routeLabel, int sampleCount = FullReportSampleCount, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostOrIp);

        int count = Math.Max(MinimumUsefulSampleCount, Math.Min(sampleCount, 600));
        bool preliminary = count < FullReportSampleCount;

        IPAddress? target = await ResolveAsync(hostOrIp, ct).ConfigureAwait(false);
        if (target is null)
        {
            return ProbeStatistics.Unmeasurable(
                hostOrIp, routeLabel, ProbeFailureCause.ResolutionFailed, count,
                $"Не удалось разрешить имя «{hostOrIp}» в IP-адрес. Проверьте подключение и DNS.");
        }

        string endpoint = port > 0 ? $"{target}:{port}" : target.ToString();

        // Шаг 1. Живой поток игры — самый честный метод: берём метрики существующего соединения.
        ProbeStatistics? live = TryProbeLiveFlow(endpoint, routeLabel);
        if (live is not null)
        {
            return live;
        }

        IReadOnlyList<RouteHopMetric> hops = await _traceroute.TraceAsync(target, 12, ct).ConfigureAwait(false);

        // Шаг 2. ICMP-эхо.
        IcmpAttemptResult icmpResult = await ProbeWithIcmpAsync(target, count, ct).ConfigureAwait(false);
        if (icmpResult.Samples.Count > 0)
        {
            return StatisticalMetricsCalculator.Compute(new ProbeMeasurementInput(
                endpoint, routeLabel, ProbeMethodUsed.ActiveIcmpEcho, icmpResult.Attempts, icmpResult.Samples,
                ProbeFailureCause.None, null, hops));
        }

        if (icmpResult.HardFailure is ProbeFailureCause.PermissionDenied or ProbeFailureCause.TargetUnreachable)
        {
            return ProbeStatistics.Unmeasurable(
                endpoint, routeLabel, icmpResult.HardFailure.Value, icmpResult.Attempts,
                icmpResult.HardFailure == ProbeFailureCause.PermissionDenied
                    ? "ICMP-пробы запрещены правами доступа: запустите приложение от имени администратора либо включите активный режим замера."
                    : "Узел недоступен: маршрут отсутствует или узел отвечает «сеть недостижима».",
                hops);
        }

        // Шаг 3. TCP-handshake: игровые UDP-серверы не отвечают на произвольные пакеты, поэтому это fallback.
        if (port > 0 && port <= 65535)
        {
            TcpAttemptResult tcpResult = await ProbeWithTcpAsync(new IPEndPoint(target, port), count, ct).ConfigureAwait(false);
            if (tcpResult.Samples.Count > 0)
            {
                string note = tcpResult.RefusedCount > 0
                    ? $"Оценка по TCP: порт {port} отвечает RST (закрыт). Время до RST — валидный сэмпл RTT, " +
                      "но это НЕ замер игрового потока."
                    : $"Оценка по времени TCP-соединения с портом {port} — это не замер игрового UDP-потока.";

                return StatisticalMetricsCalculator.Compute(new ProbeMeasurementInput(
                    endpoint, routeLabel, ProbeMethodUsed.ActiveTcpHandshake, tcpResult.Attempts, tcpResult.Samples,
                    ProbeFailureCause.None, note, hops));
            }

            if (tcpResult.HardFailure == ProbeFailureCause.PermissionDenied)
            {
                return ProbeStatistics.Unmeasurable(
                    endpoint, routeLabel, ProbeFailureCause.PermissionDenied, tcpResult.Attempts,
                    "TCP-пробы запрещены политикой безопасности или правами доступа.", hops);
            }
        }

        // Шаг 4. Ничего не сработало — честно сообщаем причину и не показываем выдуманных чисел.
        string detail = preliminary ? " (предварительная проба)" : string.Empty;
        return ProbeStatistics.Unmeasurable(
            endpoint, routeLabel, ProbeFailureCause.AllProbesTimedOut, icmpResult.Attempts + count,
            $"Ни ICMP, ни TCP-пробы не дали ответа{detail}. Узел скрыт за фильтрацией либо недоступен из этой сети. " +
            "Попробуйте запустить игру: тогда мы сможем измерить её живой поток напрямую.", hops);
    }

    public Task<IReadOnlyList<RouteHopMetric>> TraceRouteAsync(string targetHostOrIp, int maxHops = 12, CancellationToken ct = default) =>
        _traceroute.TraceHostAsync(targetHostOrIp, _resolver, maxHops, ct);

    private ProbeStatistics? TryProbeLiveFlow(string endpoint, string routeLabel)
    {
        if (_liveFlow is null)
        {
            return null;
        }

        if (!_liveFlow.TryGetLiveFlowRtt(0, endpoint, out LiveFlowRttSample sample) || sample.SamplesAvailable <= 0)
        {
            return null;
        }

        ProbeMethodUsed method = sample.MethodName.Contains("udp", StringComparison.OrdinalIgnoreCase)
            ? ProbeMethodUsed.LiveUdpPassiveFlow
            : ProbeMethodUsed.LiveTcpEstats;

        double stability = StatisticalMetricsCalculator.ComputeStability(0, sample.JitterMs, 0);

        // У UDP ретрансмиссий не бывает вовсе: протокол не подтверждает доставку. Писать «ретрансмиссий: 0»
        // значило бы выдавать отсутствие механизма за отсутствие потерь.
        string tail = method == ProbeMethodUsed.LiveUdpPassiveFlow
            ? "Ретрансмиссий в UDP не бывает: протокол не подтверждает доставку."
            : $"Ретрансмиссий за окно наблюдения: {sample.Retransmissions}.";

        if (method == ProbeMethodUsed.LiveUdpPassiveFlow && sample.UnansweredSends > 0)
        {
            tail += $" Отправок без ответа за окно: {sample.UnansweredSends} — это незакрытые пары наблюдения," +
                    " а не измеренные потери в пути.";
        }

        // Центральное значение нужно всему, что стоит дальше по цепочке: без медианы замер считался бы
        // неизмеренным, хотя числа есть. Медиана есть только там, где провайдер видел отдельные замеры;
        // иначе честно берём среднее и говорим об этом.
        double? median = sample.RttMedianMs is { } known && known > 0
            ? Math.Round(known, 2)
            : Math.Round(sample.RttAverageMs, 2);

        if (sample.RttMedianMs is not > 0)
        {
            tail += " Медиана по отдельным замерам недоступна: в отчёте средняя задержка за окно.";
        }

        string note = $"Измерение живого потока игры: {StatisticalMetricsCalculator.Describe(method)}. " + tail;

        // min/max/p95 по живому потоку недоступны: провайдер отдаёт одно число за окно наблюдения.
        // Честно оставляем их null вместо подстановки среднего; медиана есть только тогда, когда провайдер
        // действительно видел отдельные замеры (пассивный UDP-замер видит).
        return new ProbeStatistics(
            endpoint, routeLabel, true, method, ProbeFailureCause.None, note,
            sample.SamplesAvailable, sample.SamplesAvailable,
            null, Math.Round(sample.RttAverageMs, 2), null, median, null, null,
            Math.Round(sample.JitterMs, 2), 0.0, 0, Math.Round(stability, 1), false,
            Array.Empty<RouteHopMetric>(), DateTime.UtcNow);
    }

    private async Task<IPAddress?> ResolveAsync(string hostOrIp, CancellationToken ct)
    {
        if (IPAddress.TryParse(hostOrIp, out IPAddress? parsed))
        {
            return parsed;
        }

        try
        {
            IReadOnlyList<IPAddress> addresses = await _resolver.ResolveAsync(hostOrIp, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<IcmpAttemptResult> ProbeWithIcmpAsync(IPAddress target, int count, CancellationToken ct)
    {
        var samples = new List<double>(count);
        int attempts = 0;
        int consecutiveTimeouts = 0;
        ProbeFailureCause? hardFailure = null;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;

            IcmpProbeReply reply = await _icmp
                .SendAsync(target, 32, dontFragment: false, ttl: 64, timeout: IcmpTimeout, ct)
                .ConfigureAwait(false);

            switch (reply.Status)
            {
                case IcmpProbeStatus.Success:
                    consecutiveTimeouts = 0;
                    samples.Add(Math.Max(0.2, reply.RoundTripMs));
                    break;

                case IcmpProbeStatus.PermissionDenied:
                    hardFailure = ProbeFailureCause.PermissionDenied;
                    return new IcmpAttemptResult(samples, attempts, hardFailure);

                case IcmpProbeStatus.TargetUnreachable:
                    hardFailure = ProbeFailureCause.TargetUnreachable;
                    return new IcmpAttemptResult(samples, attempts, hardFailure);

                default:
                    consecutiveTimeouts++;
                    if (samples.Count == 0 && consecutiveTimeouts >= 3)
                    {
                        return new IcmpAttemptResult(samples, attempts, null);
                    }

                    break;
            }
        }

        return new IcmpAttemptResult(samples, attempts, hardFailure);
    }

    private async Task<TcpAttemptResult> ProbeWithTcpAsync(IPEndPoint target, int count, CancellationToken ct)
    {
        var samples = new List<double>(count);
        int attempts = 0;
        int refused = 0;
        int consecutiveFailures = 0;
        ProbeFailureCause? hardFailure = null;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;

            TcpProbeReply reply = await _tcp.TryConnectAsync(target, TcpTimeout, ct).ConfigureAwait(false);

            switch (reply.Status)
            {
                case TcpProbeStatus.Established:
                    consecutiveFailures = 0;
                    samples.Add(Math.Max(0.2, reply.RoundTripMs));
                    break;

                case TcpProbeStatus.ConnectionRefused:
                    // RST пришёл за измеряемое время — это валидный сэмпл.
                    consecutiveFailures = 0;
                    refused++;
                    samples.Add(Math.Max(0.2, reply.RoundTripMs));
                    break;

                case TcpProbeStatus.PermissionDenied:
                    hardFailure = ProbeFailureCause.PermissionDenied;
                    return new TcpAttemptResult(samples, attempts, refused, hardFailure);

                default:
                    consecutiveFailures++;
                    if (samples.Count == 0 && consecutiveFailures >= 3)
                    {
                        return new TcpAttemptResult(samples, attempts, refused, null);
                    }

                    break;
            }
        }

        return new TcpAttemptResult(samples, attempts, refused, hardFailure);
    }

    private sealed record IcmpAttemptResult(IReadOnlyList<double> Samples, int Attempts, ProbeFailureCause? HardFailure);

    private sealed record TcpAttemptResult(IReadOnlyList<double> Samples, int Attempts, int RefusedCount, ProbeFailureCause? HardFailure);
}

/// <summary>Трассировка с ограничением по хопам. ASN не выдумывается: без офлайн-базы выводится «н/д».</summary>
public sealed class HopTracerouteEngine
{
    private const int MaxConsecutiveSilentHops = 4;

    private readonly IIcmpProbe _icmp;

    public HopTracerouteEngine(IIcmpProbe icmp) => _icmp = icmp;

    public Task<IReadOnlyList<RouteHopMetric>> TraceHostAsync(
        string hostOrIp, IHostResolver resolver, int maxHops = 12, CancellationToken ct = default) =>
        IPAddress.TryParse(hostOrIp, out IPAddress? parsed)
            ? TraceAsync(parsed, maxHops, ct)
            : TraceHostWithResolverAsync(hostOrIp, resolver, maxHops, ct);

    private async Task<IReadOnlyList<RouteHopMetric>> TraceHostWithResolverAsync(
        string host, IHostResolver resolver, int maxHops, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<IPAddress> addresses = await resolver.ResolveAsync(host, ct).ConfigureAwait(false);
            IPAddress? ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return ip is null ? Array.Empty<RouteHopMetric>() : await TraceAsync(ip, maxHops, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return Array.Empty<RouteHopMetric>();
        }
    }

    public async Task<IReadOnlyList<RouteHopMetric>> TraceAsync(IPAddress destination, int maxHops = 12, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int limit = Math.Clamp(maxHops, 1, 32);
        var hops = new List<RouteHopMetric>(limit);
        int silent = 0;

        for (int ttl = 1; ttl <= limit; ttl++)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            IcmpProbeReply reply = await _icmp
                .SendAsync(destination, 32, dontFragment: false, ttl: ttl, timeout: TimeSpan.FromMilliseconds(650), ct)
                .ConfigureAwait(false);
            sw.Stop();

            switch (reply.Status)
            {
                case IcmpProbeStatus.Success:
                case IcmpProbeStatus.PacketTooBig:
                {
                    silent = 0;
                    IPAddress responder = reply.ResponderAddress ?? destination;
                    hops.Add(new RouteHopMetric(ttl, responder.ToString(), Math.Round(sw.Elapsed.TotalMilliseconds, 2), false));
                    if (responder.Equals(destination))
                    {
                        return hops;
                    }

                    break;
                }

                case IcmpProbeStatus.PermissionDenied:
                    hops.Add(new RouteHopMetric(ttl, "*", null, true, AsnLabels.Unknown, "Требуются права администратора"));
                    return hops;

                default:
                    silent++;
                    hops.Add(RouteHopMetric.Timeout(ttl));
                    if (silent >= MaxConsecutiveSilentHops)
                    {
                        return hops;
                    }

                    break;
            }
        }

        return hops;
    }
}
