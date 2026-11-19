using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Probing;

namespace PingForge.Network;

/// <summary>
/// Замеры живого пути до игрового сервера для оптимизатора.
///
/// Мост между двумя давно написанными частями: оптимизатору нужен один оконный замер
/// (<see cref="PathMeasurement"/>), а вся честная работа с пробами уже сделана в
/// <see cref="RouteProbeService"/> — иерархия «живой поток → ICMP → TCP-рукопожатие → честное
/// «измерить нельзя»», RST как валидный сэмпл, MAD-джиттер и процент потерь.
///
/// Здесь нельзя выдумывать числа: если проба не удалась, возвращается
/// <see cref="PathMeasurement.Unmeasurable"/> с причиной по-русски. Оптимизатор такие окна
/// не считает и прямо говорит человеку, что поток не измеряется.
/// </summary>
public sealed class RouteProbeMeasurementSource : IRealtimeMeasurementSource
{
    private readonly IRouteProbe _probes;
    private readonly int _samplesPerWindow;

    public RouteProbeMeasurementSource(IRouteProbe probes, int samplesPerWindow = 12)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _samplesPerWindow = Math.Clamp(samplesPerWindow, 5, 60);
    }

    public async Task<PathMeasurement> MeasurePathAsync(string endpoint, int port, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return PathMeasurement.Unmeasurable("у игрового потока нет адреса сервера — мерить нечего");
        }

        ProbeStatistics statistics = await _probes
            .ProbeEndpointAsync(endpoint, Math.Max(0, port), "живая оптимизация", _samplesPerWindow, ct)
            .ConfigureAwait(false);

        if (!statistics.IsMeasurable || statistics.RttMedianMs is not { } median)
        {
            return PathMeasurement.Unmeasurable(statistics.StatusMessageRu);
        }

        return new PathMeasurement(
            IsMeasurable: true,
            RttMedianMs: median,
            JitterMs: statistics.JitterMadMs ?? 0,
            LossPercent: statistics.PacketLossPercent ?? 0,
            Samples: statistics.ReceivedCount,
            MethodRu: DescribeMethod(statistics),
            RttMinMs: statistics.RttMinMs,
            RttAvgMs: statistics.RttAvgMs,
            RttP95Ms: statistics.RttP95Ms,
            Method: statistics.MethodUsed);
    }

    /// <summary>
    /// Каким способом получено число — это видно человеку. «Пинг 42 мс» без метода ничего не значит:
    /// ICMP до сервера и время рукопожатия с его портом — разные величины.
    /// </summary>
    private static string DescribeMethod(ProbeStatistics statistics)
    {
        string method = statistics.MethodUsed switch
        {
            ProbeMethodUsed.LiveTcpEstats => "живой поток игры (TCP-статистика)",
            ProbeMethodUsed.LiveUdpPassiveFlow => "живой поток игры (пассивные метки)",
            ProbeMethodUsed.ActiveIcmpEcho => "ICMP-эхо до игрового сервера",
            ProbeMethodUsed.ActiveTcpHandshake => "время TCP-рукопожатия с портом сервера",
            _ => "метод замера неизвестен",
        };

        return statistics.IsPreliminary
            ? $"{method} — предварительно, сэмплов мало ({statistics.ReceivedCount})"
            : $"{method}, {statistics.ReceivedCount} сэмплов";
    }
}
