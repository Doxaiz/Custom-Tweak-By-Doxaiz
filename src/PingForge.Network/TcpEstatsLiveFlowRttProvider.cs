using PingForge.Probing;

namespace PingForge.Network;

/// <summary>
/// Живой TCP-поток игры: задержка берётся из статистики уже открытого соединения, без единого
/// дополнительного пакета. Это третий способ замера, которого не хватало: до него живым был только
/// UDP-поток, а TCP-игры (Minecraft TCP, лобби и матчмейкинг) мерились пробами.
///
/// Границы, которые нельзя обходить:
///   • соединение ищется по адресу и порту сервера; без порта — отказ, «похожее» соединение не берётся;
///   • ноль и отрицательное число задержки — не замер: числа не бывают нулевыми, значит данных нет;
///   • исключение читателя не превращается в выдуманное число: провайдер молчит, и цепочка идёт к пробам;
///   • ретрансмиссии берутся из того же снимка соединения (`PktsRetrans`), а не подставляются нулём.
/// </summary>
public sealed class TcpEstatsLiveFlowRttProvider : ILiveFlowRttProvider
{
    /// <summary>Имя метода в образце. По нему <see cref="RouteProbeService"/> понимает, что это TCP.</summary>
    public const string MethodName = "tcp-estats";

    private readonly ITcpConnectionStatsReader _reader;

    public TcpEstatsLiveFlowRttProvider(ITcpConnectionStatsReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>Почему последний замер не состоялся: причина из читателя или пустая строка.</summary>
    public string LastProblemRu { get; private set; } = string.Empty;

    public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
    {
        sample = null!;
        LastProblemRu = string.Empty;

        if (!UdpObservationsLiveFlowRttProvider.TrySplitEndpoint(gameServerEndpoint, out string address, out int port))
        {
            LastProblemRu = "Адрес сервера без порта: TCP-соединение по нему не различить.";
            return false;
        }

        IReadOnlyList<LiveTcpConnectionStats> connections;
        try
        {
            connections = _reader.ReadEstablishedConnections(processId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Упавшее чтение статистики — это «нет данных», а не «нет задержки»: молчим и не выдумываем.
            LastProblemRu = $"Статистику TCP-соединений прочитать не удалось ({ex.GetType().Name}).";
            return false;
        }

        if (connections.Count == 0)
        {
            LastProblemRu = string.IsNullOrWhiteSpace(_reader.ProblemRu)
                ? "Установленных TCP-соединений не найдено."
                : _reader.ProblemRu;
            return false;
        }

        LiveTcpConnectionStats? match = connections.FirstOrDefault(connection =>
            string.Equals(connection.RemoteAddress, address, StringComparison.OrdinalIgnoreCase)
            && connection.RemotePort == port);

        if (match is null)
        {
            LastProblemRu = $"Среди TCP-соединений нет соединения с {address}:{port}.";
            return false;
        }

        if (match.SmoothedRttMs <= 0)
        {
            LastProblemRu = "Стек ещё не собрал измерений RTT по этому соединению: числа нет.";
            return false;
        }

        sample = new LiveFlowRttSample(
            MethodName,
            Math.Round(match.SmoothedRttMs, 2),
            Math.Round(match.RttVariationMs, 2),
            Retransmissions: match.Retransmissions,
            SamplesAvailable: Math.Max(1, match.Samples),
            UnansweredSends: 0,
            RttMedianMs: null);

        return true;
    }
}

/// <summary>
/// Цепочка провайдеров живого потока: первый, кто знает числа, и отвечает. Порядок не случайный —
/// пассивный UDP-замер не требует прав администратора и ничего не читает в системе, поэтому он первый,
/// а статистика TCP-соединения идёт второй. Способ замера называет тот провайдер, который ответил.
/// </summary>
public sealed class ChainedLiveFlowRttProvider : ILiveFlowRttProvider
{
    private readonly IReadOnlyList<ILiveFlowRttProvider> _providers;

    public ChainedLiveFlowRttProvider(params ILiveFlowRttProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.Where(provider => provider is not null).ToArray();
    }

    public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
    {
        foreach (ILiveFlowRttProvider provider in _providers)
        {
            if (provider.TryGetLiveFlowRtt(processId, gameServerEndpoint, out sample))
            {
                return true;
            }
        }

        sample = null!;
        return false;
    }
}
