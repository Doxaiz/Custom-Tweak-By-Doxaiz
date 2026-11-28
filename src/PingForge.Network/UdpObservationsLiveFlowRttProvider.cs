using System.Globalization;
using PingForge.Probing;

namespace PingForge.Network;

/// <summary>
/// Живой UDP-поток игры из наблюдений сетевого стека: задержка считается по парам «отправка → ответ»,
/// которые уже видел <c>EtwUdpPeerListener</c> (он живёт в Windows-слое и здесь не виден).
/// Ни одного лишнего пакета не отправляется.
///
/// Это первая настоящая реализация <see cref="ILiveFlowRttProvider"/>: до неё интерфейс был объявлен
/// и нигде не реализован, поэтому цепочка замера до него не доходила вовсе. Второй живой способ —
/// статистика уже открытого TCP-соединения — живёт отдельно, в <see cref="TcpEstatsLiveFlowRttProvider"/>:
/// UDP и TCP читаются из разных источников, и смешивать их в одном классе значило бы прятать, какой
/// именно способ дал числа.
///
/// Границы:
///   • без порта собеседника сопоставлять нечего — отказ, а не «примерный» замер;
///   • номер процесса можно не знать (передаётся 0): тогда берётся самый свежий замер по этому
///     адресу и порту, но чужой собеседник за него не выдаётся;
///   • ретрансмиссий у UDP не бывает, поэтому в образце всегда ноль, а текст отчёта это объясняет;
///   • нет полных пар — false, числа не выдумываются.
/// </summary>
public sealed class UdpObservationsLiveFlowRttProvider : ILiveFlowRttProvider
{
    /// <summary>Имя метода в образце. По нему <see cref="RouteProbeService"/> понимает, что это UDP.</summary>
    public const string MethodName = "udp-passive";

    private readonly PassiveUdpRttTracker _tracker;

    public UdpObservationsLiveFlowRttProvider(PassiveUdpRttTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public bool TryGetLiveFlowRtt(int processId, string gameServerEndpoint, out LiveFlowRttSample sample)
    {
        sample = null!;

        if (!TrySplitEndpoint(gameServerEndpoint, out string address, out int port))
        {
            return false;
        }

        PassiveUdpRttSummary summary;
        if (processId > 0)
        {
            if (!_tracker.TryGet(processId, address, port, out summary))
            {
                return false;
            }
        }
        else if (!_tracker.TryGetFreshest(address, port, out summary))
        {
            return false;
        }

        if (summary.Samples <= 0)
        {
            return false;
        }

        // Джиттер считаем той же мерой, что и у активных проб: среднее абсолютное отклонение соседних
        // замеров. Так числа в отчёте нельзя сравнивать «на глаз» по-разному для разных методов.
        double jitter = StatisticalMetricsCalculator.NeighbourMeanAbsoluteDeviation(summary.SamplesInOrder);

        // Среднее и медиана — разные числа, и подменять одно другим нельзя: отчёт называет оба.
        double average = summary.SamplesInOrder.Count > 0
            ? summary.SamplesInOrder.Average()
            : summary.MedianMs;

        sample = new LiveFlowRttSample(
            MethodName,
            Math.Round(average, 2),
            Math.Round(jitter, 2),
            Retransmissions: 0,
            SamplesAvailable: summary.Samples,
            UnansweredSends: summary.UnansweredSends,
            RttMedianMs: Math.Round(summary.MedianMs, 2));

        return true;
    }

    /// <summary>
    /// «1.2.3.4:27960», «[2001:db8::1]:27015». Без порта — отказ: адрес без порта не отличает сервер
    /// от любого другого собеседника того же хоста.
    /// </summary>
    public static bool TrySplitEndpoint(string? endpoint, out string address, out int port)
    {
        address = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        string text = endpoint.Trim();
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        // Адрес наружу отдаём только вместе с годным портом: при отказе оба выхода пустые,
        // иначе вызывающий получит половину разбора и решит, что её можно использовать.
        string candidate = text[..colon].Trim('[', ']');
        if (candidate.Length == 0)
        {
            return false;
        }

        if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort)
            || parsedPort is <= 0 or > 65535)
        {
            port = 0;
            return false;
        }

        address = candidate;
        port = parsedPort;
        return true;
    }
}
