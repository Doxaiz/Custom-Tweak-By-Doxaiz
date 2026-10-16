using System.Net;
using PingForge.Core.Models;

namespace PingForge.Probing;

/// <summary>
/// Подбор Path MTU двоичным поиском по размеру полезной нагрузки с флагом «не фрагментировать».
///
/// Ключевые правила, отличающие это от наивной реализации:
///   • таймаут НЕ считается признаком «пакет слишком большой» — иначе случайная потеря занижает MTU
///     на сотни байт и заявка «низкий пинг» портит ситуацию (фрагментация на 1400 вместо 1500);
///   • размер перепроверяется на повторяемость, а следующий размер — на то, что он действительно не проходит;
///   • цель — игровой сервер или релей, а не произвольный публичный адрес.
/// </summary>
public sealed class DfMtuDiscoveryService
{
    /// <summary>20 байт IPv4 + 8 байт ICMP.</summary>
    public const int Ipv4IcmpHeaderBytes = 28;

    /// <summary>Минимальный MTU IPv4 по RFC 791.</summary>
    public const int MinimumIpv4Mtu = 576;

    public const int EthernetMtu = 1500;

    private const int MaxAttemptsPerSize = 3;

    private readonly IIcmpProbe _icmp;
    private readonly int _maxAttemptsPerSize;

    public DfMtuDiscoveryService(IIcmpProbe icmp, int maxAttemptsPerSize = MaxAttemptsPerSize)
    {
        _icmp = icmp ?? throw new ArgumentNullException(nameof(icmp));
        _maxAttemptsPerSize = Math.Max(1, maxAttemptsPerSize);
    }

    /// <param name="target">Целевой узел — игровой сервер или релей.</param>
    /// <param name="interfaceMtuBytes">Текущий MTU интерфейса: выше него искать нечего.</param>
    /// <param name="minimumMtuBytes">Нижняя граница поиска (для IPv4 ниже 576 не опускаемся).</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<PathMtuDiscoveryResult> DiscoverAsync(
        IPAddress target,
        int interfaceMtuBytes,
        int minimumMtuBytes = MinimumIpv4Mtu,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        int floor = Math.Max(minimumMtuBytes, MinimumIpv4Mtu);
        int ceiling = Math.Max(interfaceMtuBytes, floor);

        // Быстрая проверка: если максимальный размер проходит без фрагментации — менять нечего.
        IcmpProbeReply topReply = await ProbeSizeWithRetriesAsync(target, ceiling, ct).ConfigureAwait(false);

        switch (topReply.Status)
        {
            case IcmpProbeStatus.Success:
                return new PathMtuDiscoveryResult(
                    MtuDiscoveryOutcome.OptimalFound, floor, ceiling, ceiling, ceiling - Ipv4IcmpHeaderBytes,
                    $"Пакет максимального размера ({ceiling} байт MTU) проходит без фрагментации — менять нечего.");

            case IcmpProbeStatus.PermissionDenied:
                return new PathMtuDiscoveryResult(
                    MtuDiscoveryOutcome.UndeterminedIcmpBlocked, floor, ceiling, null, null,
                    "ICMP-пробы требуют прав администратора. Запустите приложение от администратора — " +
                    "иначе определить MTU нельзя, а менять его наугад мы не будем.");

            case IcmpProbeStatus.Timeout:
                // Максимальный размер мог «провалиться» из-за блокировки ICMP: спускаемся к минимуму,
                // чтобы отличить «слишком большой пакет» от «ICMP вообще не ходит».
                break;
        }

        IcmpProbeReply bottomReply = await ProbeSizeWithRetriesAsync(target, floor, ct).ConfigureAwait(false);

        if (bottomReply.Status == IcmpProbeStatus.Timeout)
        {
            return new PathMtuDiscoveryResult(
                MtuDiscoveryOutcome.UndeterminedInconsistent, floor, ceiling, null, null,
                $"Даже минимальный размер ({floor} байт) не отвечает: определить MTU нельзя — похоже, ICMP блокируется на всём пути. " +
                "Текущий MTU оставляем без изменений.");
        }

        if (bottomReply.Status == IcmpProbeStatus.PermissionDenied)
        {
            return new PathMtuDiscoveryResult(
                MtuDiscoveryOutcome.UndeterminedIcmpBlocked, floor, ceiling, null, null,
                "ICMP-пробы требуют прав администратора — определить MTU нельзя.");
        }

        int low = floor;
        int high = ceiling;
        int bestWorking = 0;

        while (low <= high)
        {
            ct.ThrowIfCancellationRequested();

            int mid = low + ((high - low) / 2);
            IcmpProbeReply reply = await ProbeSizeWithRetriesAsync(target, mid, ct).ConfigureAwait(false);

            switch (reply.Status)
            {
                case IcmpProbeStatus.Success:
                    bestWorking = mid;
                    low = mid + 1;
                    break;

                case IcmpProbeStatus.PacketTooBig:
                    high = mid - 1;
                    break;

                default:
                    // Таймаут после всех повторов: НЕ считаем это «слишком большой пакет».
                    return new PathMtuDiscoveryResult(
                        MtuDiscoveryOutcome.UndeterminedInconsistent, floor, ceiling, null, null,
                        $"Размер {mid} байт не ответил {_maxAttemptsPerSize} раза подряд. Тишина не означает «слишком большой пакет», " +
                        "поэтому определить MTU нельзя: канал нестабилен или фильтрует ICMP отдельных размеров.");
            }

            if (bestWorking == ceiling)
            {
                break;
            }
        }

        if (bestWorking < floor)
        {
            return new PathMtuDiscoveryResult(
                MtuDiscoveryOutcome.UndeterminedInconsistent, floor, ceiling, null, null,
                $"Ни один размер в диапазоне {floor}–{ceiling} байт не прошёл: определить MTU нельзя.");
        }

        // Повторяемость: размер должен подтвердиться тремя успешными пробами.
        IcmpProbeReply confirm2 = await ProbeSizeWithRetriesAsync(target, bestWorking, ct).ConfigureAwait(false);
        IcmpProbeReply confirm3 = await ProbeSizeWithRetriesAsync(target, bestWorking, ct).ConfigureAwait(false);
        if (confirm2.Status != IcmpProbeStatus.Success || confirm3.Status != IcmpProbeStatus.Success)
        {
            return new PathMtuDiscoveryResult(
                MtuDiscoveryOutcome.UndeterminedInconsistent, floor, ceiling, null, null,
                $"Размер {bestWorking} байт прошёл нестабильно (не подтвердился трижды) — определить MTU нельзя.");
        }

        // Следующий размер обязан НЕ проходить, иначе найденная граница не является границей.
        IcmpProbeReply oversize = await ProbeSizeWithRetriesAsync(target, bestWorking + 1, ct).ConfigureAwait(false);
        if (oversize.Status == IcmpProbeStatus.Success)
        {
            return new PathMtuDiscoveryResult(
                MtuDiscoveryOutcome.UndeterminedInconsistent, floor, ceiling, null, null,
                $"Пакет {bestWorking + 1} байт тоже проходит — граница размыта (возможна фрагментация в туннеле). Определить MTU нельзя.");
        }

        return new PathMtuDiscoveryResult(
            MtuDiscoveryOutcome.OptimalFound, floor, ceiling, bestWorking, bestWorking - Ipv4IcmpHeaderBytes,
            $"Полезная нагрузка {bestWorking - Ipv4IcmpHeaderBytes} байт прошла без фрагментации (MTU {bestWorking} байт), " +
            $"следующий размер не прошёл. Подтверждено тремя пробами. Текущий MTU интерфейса — {ceiling} байт. " +
            "Значение будет применено только к выбранному игровому подключению.");
    }

    /// <summary>
    /// Проба одного размера с повторами: одиночный таймаут не должен менять границу поиска.
    /// </summary>
    private async Task<IcmpProbeReply> ProbeSizeWithRetriesAsync(IPAddress target, int mtuBytes, CancellationToken ct)
    {
        IcmpProbeReply last = new(IcmpProbeStatus.Timeout, 0, null);

        for (int attempt = 0; attempt < _maxAttemptsPerSize; attempt++)
        {
            last = await ProbeMtuAsync(target, mtuBytes, ct).ConfigureAwait(false);

            if (last.Status != IcmpProbeStatus.Timeout)
            {
                return last;
            }
        }

        return last;
    }

    private Task<IcmpProbeReply> ProbeMtuAsync(IPAddress target, int mtuBytes, CancellationToken ct) =>
        _icmp.SendAsync(
            target,
            payloadBytes: mtuBytes - Ipv4IcmpHeaderBytes,
            dontFragment: true,
            ttl: 128,
            timeout: TimeSpan.FromSeconds(2),
            ct);
}
