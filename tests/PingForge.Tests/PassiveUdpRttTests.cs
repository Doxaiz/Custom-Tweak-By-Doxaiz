using PingForge.Network;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Пассивный замер UDP-задержки по событиям сетевого стека: пара «отправка → ответ» — это замер,
/// а всё остальное — не замер, и числа из этого не выдумываются.
/// </summary>
public sealed class PassiveUdpRttTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Пара_отправка_ответ_даёт_задержку()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(1));

        Assert.True(tracker.Observe(Send(at: T0)));
        Assert.True(tracker.Observe(Reply(at: T0.AddMilliseconds(42))));

        Assert.True(tracker.TryGet(4242, Peer, Port, out PassiveUdpRttSummary summary));
        Assert.Equal(1, summary.Samples);
        Assert.Equal(42.0, summary.MedianMs, 3);
        Assert.Equal(0, summary.UnansweredSends);
    }

    [Fact]
    public void Ответ_без_отправки_числом_не_считается()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0);

        Assert.True(tracker.Observe(Reply(at: T0.AddMilliseconds(30))));

        Assert.False(tracker.TryGet(4242, Peer, Port, out _));
    }

    [Fact]
    public void Ответ_раньше_отправки_отбрасывается()
    {
        // Сбитые часы или переставленные события: отрицательной задержки не бывает, и «потерей» это тоже не считается.
        var tracker = new PassiveUdpRttTracker(clock: () => T0);

        tracker.Observe(Send(at: T0));
        tracker.Observe(Reply(at: T0.AddMilliseconds(-5)));

        Assert.False(tracker.TryGet(4242, Peer, Port, out _));
    }

    [Fact]
    public void Ответ_позже_окна_становится_неотвеченной_отправкой()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(10));

        tracker.Observe(Send(at: T0));
        tracker.Observe(Reply(at: T0.AddSeconds(3)));

        // Замеров нет: три секунды — это не задержка пути, а потерянный ответ.
        Assert.False(tracker.TryGet(4242, Peer, Port, out _));

        // И это не молчание: поздний ответ засчитан как отправка без ответа, а следующая пара — замер.
        tracker.Observe(Send(at: T0.AddSeconds(6)));
        tracker.Observe(Reply(at: T0.AddSeconds(6).AddMilliseconds(25)));

        Assert.True(tracker.TryGet(4242, Peer, Port, out PassiveUdpRttSummary summary));
        Assert.Equal(1, summary.Samples);
        Assert.Equal(25.0, summary.MedianMs, 3);
        Assert.Equal(1, summary.UnansweredSends);
    }

    [Fact]
    public void Вторая_отправка_без_ответа_считается_потерей()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(10));

        tracker.Observe(Send(at: T0));
        tracker.Observe(Send(at: T0.AddSeconds(4)));
        tracker.Observe(Reply(at: T0.AddSeconds(4).AddMilliseconds(35)));

        Assert.True(tracker.TryGet(4242, Peer, Port, out PassiveUdpRttSummary summary));
        Assert.Equal(1, summary.Samples);
        Assert.Equal(35.0, summary.MedianMs, 3);
        Assert.Equal(1, summary.UnansweredSends);
    }

    [Fact]
    public void Собеседники_разных_процессов_не_смешиваются()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(1));

        tracker.Observe(Send(at: T0, processId: 111));
        tracker.Observe(Reply(at: T0.AddMilliseconds(20), processId: 111));

        tracker.Observe(Send(at: T0.AddSeconds(1), processId: 222));
        tracker.Observe(Reply(at: T0.AddSeconds(1).AddMilliseconds(80), processId: 222));

        Assert.True(tracker.TryGet(111, Peer, Port, out PassiveUdpRttSummary first));
        Assert.True(tracker.TryGet(222, Peer, Port, out PassiveUdpRttSummary second));
        Assert.Equal(20.0, first.MedianMs, 3);
        Assert.Equal(80.0, second.MedianMs, 3);

        // Процесс, которого мы не наблюдали, числа не получает: чужой замер за его не выдаётся.
        Assert.False(tracker.TryGet(333, Peer, Port, out _));
    }

    [Fact]
    public void Окно_хранит_последние_замеры()
    {
        var tracker = new PassiveUdpRttTracker(samplesPerPeer: 3, clock: () => T0.AddSeconds(10));

        foreach (int rtt in new[] { 10, 20, 30, 40, 50 })
        {
            tracker.Observe(Send(at: T0));
            tracker.Observe(Reply(at: T0.AddMilliseconds(rtt)));
        }

        Assert.True(tracker.TryGet(4242, Peer, Port, out PassiveUdpRttSummary summary));
        Assert.Equal(3, summary.Samples);
        Assert.Equal(40.0, summary.MedianMs, 3);
        // Порядок важен: джиттер считается по соседним замерам, а не по отсортированным.
        Assert.Equal(3, summary.SamplesInOrder.Count);
        Assert.Equal(30.0, summary.SamplesInOrder[0], 3);
        Assert.Equal(40.0, summary.SamplesInOrder[1], 3);
        Assert.Equal(50.0, summary.SamplesInOrder[2], 3);
    }

    [Fact]
    public void Медиана_считается_и_по_чётному_числу_замеров()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(2));

        tracker.Observe(Send(at: T0));
        tracker.Observe(Reply(at: T0.AddMilliseconds(10)));
        tracker.Observe(Send(at: T0.AddSeconds(1)));
        tracker.Observe(Reply(at: T0.AddSeconds(1).AddMilliseconds(21)));

        Assert.True(tracker.TryGet(4242, Peer, Port, out PassiveUdpRttSummary summary));
        Assert.Equal(2, summary.Samples);
        Assert.Equal(15.5, summary.MedianMs, 3);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public void Приватные_и_служебные_адреса_не_наблюдаются(string address)
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0);

        Assert.False(tracker.Observe(Send(at: T0, address: address)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Мусорные_наблюдения_отбрасываются()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0);

        Assert.False(tracker.Observe(Send(at: T0, processId: 0)));
        Assert.False(tracker.Observe(Send(at: T0, port: 0)));
        Assert.False(tracker.Observe(Send(at: T0, port: 70000)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Самый_свежий_собеседник_выбирается_по_времени()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(5));

        tracker.Observe(Send(at: T0, processId: 1));
        tracker.Observe(Reply(at: T0.AddMilliseconds(10), processId: 1));
        tracker.Observe(Send(at: T0.AddSeconds(2), processId: 2));
        tracker.Observe(Reply(at: T0.AddSeconds(2).AddMilliseconds(70), processId: 2));

        Assert.True(tracker.TryGetFreshest(Peer, Port, out PassiveUdpRttSummary freshest));
        Assert.Equal(70.0, freshest.MedianMs, 3);

        // Другой собеседник по тому же порту не подставляется.
        Assert.False(tracker.TryGetFreshest("203.0.113.99", Port, out _));
    }

    [Fact]
    public void Остывший_собеседник_забывается()
    {
        var tracker = new PassiveUdpRttTracker(maxIdle: TimeSpan.FromSeconds(5), clock: () => T0.AddMinutes(5));

        tracker.Observe(Send(at: T0, processId: 1));
        tracker.Observe(Reply(at: T0.AddMilliseconds(10), processId: 1));
        Assert.Equal(1, tracker.Count);

        // Событие другого процесса через десять секунд: первый собеседник уже остыл.
        tracker.Observe(Send(at: T0.AddSeconds(10), processId: 2));
        Assert.Equal(1, tracker.Count);
        Assert.False(tracker.TryGet(1, Peer, Port, out _));
    }

    [Fact]
    public void Очистка_забывает_всё()
    {
        var tracker = new PassiveUdpRttTracker(clock: () => T0.AddSeconds(1));

        tracker.Observe(Send(at: T0));
        tracker.Observe(Reply(at: T0.AddMilliseconds(12)));
        Assert.True(tracker.TryGet(4242, Peer, Port, out _));

        tracker.Clear();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.TryGet(4242, Peer, Port, out _));
    }

    [Fact]
    public void Окно_и_сроки_должны_быть_положительными()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassiveUdpRttTracker(samplesPerPeer: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassiveUdpRttTracker(maxRoundTrip: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassiveUdpRttTracker(maxIdle: TimeSpan.FromSeconds(-1)));
    }

    private const string Peer = "203.0.113.10";
    private const int Port = 27015;

    private static UdpPeerObservation Send(
        DateTime at,
        int processId = 4242,
        string address = Peer,
        int port = Port) =>
        new(processId, address, port, IsSend: true, ObservedUtc: at);

    private static UdpPeerObservation Reply(
        DateTime at,
        int processId = 4242,
        string address = Peer,
        int port = Port) =>
        new(processId, address, port, IsSend: false, ObservedUtc: at);
}
