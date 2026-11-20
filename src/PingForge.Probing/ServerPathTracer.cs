using System.Diagnostics;
using System.Net;

namespace PingForge.Probing;

/// <summary>
/// Почему трасса закончилась. Каждая причина называется человеку словами — «просто остановилась»
/// без объяснения запрещено: по трассе человек решает, где искать причину задержки.
/// </summary>
public enum ServerPathStopReason
{
    /// <summary>Дошли до адреса игрового сервера — трасса полная.</summary>
    ReachedTarget,

    /// <summary>Кончился лимит хопов: дальше цель не отвечает.</summary>
    MaxHops,

    /// <summary>Четыре хопа подряд молчат — дальше строить бессмысленно.</summary>
    SilenceLimit,

    /// <summary>Вышел общий лимит времени (по умолчанию 60 секунд).</summary>
    TimeLimit,

    /// <summary>Человек нажал «Отмена».</summary>
    Cancelled,

    /// <summary>Windows не дала отправить ICMP-пробы — измерять нечем.</summary>
    PermissionDenied,
}

/// <summary>
/// Один хоп трассы. Числа nullable: если пробу не измерили, среднего и 95-го нет — это
/// <c>null</c>, а не ноль. Анонимный хоп (на TTL никто не ответил) остаётся в списке отдельной
/// строкой, а не пропадает: дырка в трассе — тоже факт.
/// </summary>
public sealed record ServerPathProbeHop(
    int HopNumber,
    string? ResponderAddress,
    int ProbesSent,
    int ProbesAnswered,
    IReadOnlyList<double> Samples)
{
    /// <summary>Никто не ответил: адреса нет, и выдумывать его нельзя.</summary>
    public bool IsAnonymous => string.IsNullOrWhiteSpace(ResponderAddress);

    /// <summary>Среднее по ответившим пробам. Нет ответов — <c>null</c>, не 0.</summary>
    public double? AvgRttMs => Samples.Count == 0
        ? null
        : Math.Round(Samples.Average(), 2);

    /// <summary>95-й процентиль по ответившим пробам (ближайший ранг). Нет ответов — <c>null</c>.</summary>
    public double? P95RttMs => Percentile(Samples, 0.95);

    /// <summary>
    /// Потери считаются только там, где хоп вообще ответил: у анонимного хопа «100 % потерь»
    /// звучало бы как потеря пакетов до сервера, хотя это лишь запрет отвечать на TTL.
    /// Нет измерения — <c>null</c>.
    /// </summary>
    public double? LossPercent => IsAnonymous || ProbesSent == 0
        ? null
        : Math.Round(100.0 * (ProbesSent - ProbesAnswered) / ProbesSent, 1);

    private static double? Percentile(IReadOnlyList<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        double[] sorted = samples.OrderBy(value => value).ToArray();
        int rank = (int)Math.Ceiling(percentile * sorted.Length);
        rank = Math.Clamp(rank, 1, sorted.Length);
        return Math.Round(sorted[rank - 1], 2);
    }
}

/// <summary>
/// Итог трассы: строки хопов и честная причина остановки. Пакеты считаются здесь же — человек
/// видит, сколько именно проб ушло в сеть.
/// </summary>
public sealed record ServerPathResult(
    IReadOnlyList<ServerPathProbeHop> Hops,
    int ProbesSent,
    int ProbesAnswered,
    ServerPathStopReason StopReason,
    string? StopNoteRu = null);

/// <summary>
/// Трасса до адреса игрового сервера (задача H). Свой ICMP-трассировщик проекта:
///
///   • TTL 1..30, по три пробы на хоп — средний и 95-й RTT считаются по ответившим пробам;
///   • общий лимит по умолчанию 60 секунд и отмена токеном: результат возвращается с тем,
///     что успели измерить, а не выбрасывается;
///   • анонимный хоп остаётся строкой «не отвечает», значения остаются <c>null</c>;
///   • после четырёх молчащих хопов подряд трасса останавливается — в интернет «наугад»
///     десятки пакетов не льются;
///   • ничего не меняет в системе: это только эхо-запросы с TTL.
///
/// Правило честности: подписей ASN и «виновников» здесь нет. Трасса показывает, где выросло
/// время между соседними ответившими хопами, и не утверждает, чей это узел.
/// </summary>
public sealed class ServerPathTracer
{
    /// <summary>Сколько хопов максимум — как у стандартной трассировки Windows.</summary>
    public const int DefaultMaxHops = 30;

    /// <summary>Три пробы на хоп — этого хватает на среднее и 95-й без «снайперских» выводов.</summary>
    public const int DefaultProbesPerHop = 3;

    /// <summary>Общий лимит трассы.</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>Столько хопов подряд могут молчать, прежде чем трасса остановится.</summary>
    public const int MaxConsecutiveSilentHops = 4;

    /// <summary>Ожидание одной пробы: дольше держать строку «идёт» нельзя.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(650);

    private readonly IIcmpProbe _icmp;

    public ServerPathTracer(IIcmpProbe icmp) => _icmp = icmp ?? throw new ArgumentNullException(nameof(icmp));

    public async Task<ServerPathResult> TraceAsync(
        IPAddress destination,
        Action<ServerPathProbeHop>? onHop = null,
        int maxHops = DefaultMaxHops,
        int probesPerHop = DefaultProbesPerHop,
        TimeSpan? timeLimit = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int hopLimit = Math.Clamp(maxHops, 1, 32);
        int probes = Math.Clamp(probesPerHop, 1, 10);
        TimeSpan limit = timeLimit ?? DefaultTimeLimit;
        if (limit <= TimeSpan.Zero)
        {
            limit = DefaultTimeLimit;
        }

        var watch = Stopwatch.StartNew();
        var hops = new List<ServerPathProbeHop>(hopLimit);
        int sent = 0;
        int answered = 0;
        int silentRun = 0;

        for (int ttl = 1; ttl <= hopLimit; ttl++)
        {
            if (ct.IsCancellationRequested)
            {
                return Finish(hops, sent, answered, ServerPathStopReason.Cancelled, "Трасса отменена по кнопке.");
            }

            if (watch.Elapsed >= limit)
            {
                return Finish(hops, sent, answered, ServerPathStopReason.TimeLimit, LimitNote(limit));
            }

            var samples = new List<double>(probes);
            string? responder = null;
            int hopSent = 0;
            int hopAnswered = 0;
            bool permissionDenied = false;

            for (int probe = 0; probe < probes; probe++)
            {
                if (ct.IsCancellationRequested)
                {
                    return FinishWithPendingHop(hops, sent, answered, ttl, responder, hopSent, hopAnswered,
                        samples, onHop, ServerPathStopReason.Cancelled, "Трасса отменена по кнопке.");
                }

                TimeSpan remaining = limit - watch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    // Хоп без единой пробы строкой «не отвечает» не рисуем: пробы не было — и вывода нет.
                    return FinishWithPendingHop(hops, sent, answered, ttl, responder, hopSent, hopAnswered,
                        samples, onHop, ServerPathStopReason.TimeLimit, LimitNote(limit));
                }

                TimeSpan timeout = remaining < ProbeTimeout ? remaining : ProbeTimeout;

                IcmpProbeReply reply;
                try
                {
                    reply = await _icmp
                        .SendAsync(destination, 32, dontFragment: false, ttl: ttl, timeout: timeout, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Отмена токеном — это отмена: отдаём уже измеренное, а не теряем его.
                    return FinishWithPendingHop(hops, sent, answered, ttl, responder, hopSent, hopAnswered,
                        samples, onHop, ServerPathStopReason.Cancelled, "Трасса отменена по кнопке.");
                }

                hopSent++;
                sent++;

                if (reply.Status is IcmpProbeStatus.Success or IcmpProbeStatus.PacketTooBig)
                {
                    responder ??= (reply.ResponderAddress ?? destination).ToString();
                    samples.Add(Math.Max(0.2, reply.RoundTripMs));
                    hopAnswered++;
                    answered++;
                }
                else if (reply.Status == IcmpProbeStatus.PermissionDenied)
                {
                    permissionDenied = true;
                    hopSent--;   // проба не ушла в сеть: права не дали
                    sent--;
                    break;
                }
            }

            // Права не дали отправить ни одной пробы — строки «не отвечает» быть не должно:
            // пробы не было, значит и вывода о хопе нет.
            if (permissionDenied && hopSent == 0)
            {
                return Finish(hops, sent, answered, ServerPathStopReason.PermissionDenied,
                    "Windows не дала отправить ICMP-пробы: нужны права администратора. Измерять нечем — числа не выдумываем.");
            }

            var completed = new ServerPathProbeHop(ttl, responder, hopSent, hopAnswered, samples);
            hops.Add(completed);
            onHop?.Invoke(completed);

            if (permissionDenied)
            {
                return Finish(hops, sent, answered, ServerPathStopReason.PermissionDenied,
                    "Windows не дала отправить ICMP-пробы: нужны права администратора. Измерять нечем — числа не выдумываем.");
            }

            if (responder is not null)
            {
                silentRun = 0;
                if (string.Equals(responder, destination.ToString(), StringComparison.Ordinal))
                {
                    return Finish(hops, sent, answered, ServerPathStopReason.ReachedTarget, null);
                }
            }
            else
            {
                silentRun++;
                if (silentRun >= MaxConsecutiveSilentHops)
                {
                    return Finish(hops, sent, answered, ServerPathStopReason.SilenceLimit,
                        "Четыре хопа подряд не ответили — дальше строить нет смысла.");
                }
            }
        }

        return Finish(hops, sent, answered, ServerPathStopReason.MaxHops,
            $"Трасса дошла до предела в {hopLimit} хопов: цель так и не ответила.");
    }

    /// <summary>
    /// Закончить трассу посреди хопа: если хоть одна проба успела уйти, её строка остаётся
    /// в таблице — это измеренный факт. Ноль проб строкой не рисуем.
    /// </summary>
    private static ServerPathResult FinishWithPendingHop(
        List<ServerPathProbeHop> hops,
        int sent,
        int answered,
        int ttl,
        string? responder,
        int hopSent,
        int hopAnswered,
        List<double> samples,
        Action<ServerPathProbeHop>? onHop,
        ServerPathStopReason reason,
        string? note)
    {
        if (hopSent > 0)
        {
            var pending = new ServerPathProbeHop(ttl, responder, hopSent, hopAnswered, samples);
            hops.Add(pending);
            onHop?.Invoke(pending);
        }

        return Finish(hops, sent, answered, reason, note);
    }

    private static string LimitNote(TimeSpan limit) =>
        $"Трасса остановлена по лимиту {Math.Round(limit.TotalSeconds)} секунд: дольше не строю.";

    private static ServerPathResult Finish(
        List<ServerPathProbeHop> hops, int sent, int answered, ServerPathStopReason reason, string? note) =>
        new(hops, sent, answered, reason, note);
}
