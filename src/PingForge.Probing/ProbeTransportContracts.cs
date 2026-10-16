using System.Net;

namespace PingForge.Probing;

public enum IcmpProbeStatus
{
    Success,
    Timeout,
    TargetUnreachable,
    PacketTooBig,
    PermissionDenied,
    Other
}

public sealed record IcmpProbeReply(IcmpProbeStatus Status, double RoundTripMs, IPAddress? ResponderAddress);

public enum TcpProbeStatus
{
    /// <summary>Соединение установлено.</summary>
    Established,

    /// <summary>RST — порт закрыт. Это ВАЛИДНЫЙ сэмпл RTT: мы получили ответ за измеряемое время.</summary>
    ConnectionRefused,
    Timeout,
    Unreachable,
    PermissionDenied
}

public sealed record TcpProbeReply(TcpProbeStatus Status, double RoundTripMs);

/// <summary>Провайдер живого потока игры: метрики берутся из уже существующего соединения.</summary>
public sealed record LiveFlowRttSample(string MethodName, double RttAverageMs, double JitterMs, int Retransmissions, int SamplesAvailable);

public interface ILiveFlowRttProvider
{
    /// <summary>
    /// Возвращает метрики живого соединения игры (TCP через SIO_TCP_INFO, UDP — по пассивным меткам
    /// на ответах сервера). Если поток недоступен — false. Числа не выдумываются.
    /// </summary>
    bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample);
}

/// <summary>Отправка ICMP-эха с управлением TTL и флагом «не фрагментировать».</summary>
public interface IIcmpProbe
{
    Task<IcmpProbeReply> SendAsync(IPAddress target, int payloadBytes, bool dontFragment, int ttl, TimeSpan timeout, CancellationToken ct = default);

    bool RequiresAdministratorRights { get; }
}

/// <summary>TCP-проба рукопожатием: применима там, где ICMP запрещён.</summary>
public interface ITcpHandshakeProbe
{
    Task<TcpProbeReply> TryConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct = default);
}

public interface IHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default);
}
