using System.Net;
using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Probing;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Задача H: трасса «Путь до сервера». Проверяется то, что человек увидит:
///   • три пробы на хоп, средний и 95-й считаются по ответившим пробам;
///   • анонимный хоп остаётся строкой «не отвечает», а неизмеренное — null, не ноль;
///   • трасса заканчивается на адресе цели, по лимиту времени или по кнопке «Отмена»;
///   • без живой игры в сеть не уходит ни один пакет;
///   • «где растёт задержка» честно разделяет домашнюю сеть и путь провайдера/магистрали
///     и подсказывает альтернативный маршрут из блока «Сервер маршрутизации».
/// </summary>
public sealed class ServerPathTests
{
    private const string TargetIp = "203.0.113.9";

    // ── Трасса: пробы, среднее, 95-й, потери ────────────────────────────────────────────────

    [Fact]
    public async Task Три_пробы_на_хоп_дают_среднее_и_95й_процентиль()
    {
        var icmp = new ScriptedIcmp((ttl, call) => ttl switch
        {
            1 => Success(call == 1 ? 10 : call == 2 ? 20 : 30, "192.168.1.1"),
            _ => Success(40, TargetIp)
        });

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 4);

        ServerPathProbeHop first = result.Hops[0];
        Assert.Equal(3, first.ProbesSent);
        Assert.Equal(3, first.ProbesAnswered);
        Assert.Equal(20d, first.AvgRttMs);
        Assert.Equal(30d, first.P95RttMs);
        Assert.Equal(0d, first.LossPercent);
        Assert.Equal(6, result.ProbesSent);
    }

    [Fact]
    public async Task Трасса_останавливается_когда_ответил_адрес_цели()
    {
        var icmp = new ScriptedIcmp((ttl, _) => ttl < 3
            ? Success(5, ttl == 1 ? "192.168.1.1" : "10.0.0.1")
            : Success(40, TargetIp));

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 10);

        Assert.Equal(3, result.Hops.Count);
        Assert.Equal(TargetIp, result.Hops[^1].ResponderAddress);
        Assert.Equal(ServerPathStopReason.ReachedTarget, result.StopReason);
        Assert.Equal(3, result.Hops.Select(hop => hop.HopNumber).Distinct().Count());
    }

    [Fact]
    public async Task Анонимный_хоп_остаётся_строкой_без_единого_числа()
    {
        var icmp = new ScriptedIcmp((ttl, _) => ttl switch
        {
            1 => Success(3, "192.168.1.1"),
            2 => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null),
            _ => Success(30, TargetIp)
        });

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 5);

        ServerPathProbeHop anonymous = result.Hops.Single(hop => hop.HopNumber == 2);
        Assert.True(anonymous.IsAnonymous);
        Assert.Null(anonymous.ResponderAddress);
        Assert.Null(anonymous.AvgRttMs);
        Assert.Null(anonymous.P95RttMs);
        Assert.Null(anonymous.LossPercent);
        Assert.Equal(3, anonymous.ProbesSent);
    }

    [Fact]
    public async Task Потери_считаются_из_отправленных_проб_а_не_из_догадок()
    {
        var icmp = new ScriptedIcmp((ttl, call) => ttl switch
        {
            1 when call == 1 => Success(10, "192.168.1.1"),
            1 when call == 2 => Success(12, "192.168.1.1"),
            1 => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null),
            _ => Success(40, TargetIp)
        });

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 3);

        ServerPathProbeHop first = result.Hops[0];
        Assert.Equal(3, first.ProbesSent);
        Assert.Equal(2, first.ProbesAnswered);
        Assert.Equal(33.3d, first.LossPercent);
        Assert.Equal(11d, first.AvgRttMs);
    }

    [Fact]
    public async Task Четыре_молчащих_хопа_подряд_останавливают_трассу()
    {
        var icmp = new ScriptedIcmp((_, _) => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null));

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 30);

        Assert.Equal(4, result.Hops.Count);
        Assert.All(result.Hops, hop => Assert.True(hop.IsAnonymous));
        Assert.Equal(12, result.ProbesSent);
        Assert.Equal(ServerPathStopReason.SilenceLimit, result.StopReason);
    }

    [Fact]
    public async Task Неизмеренные_пробы_не_превращаются_в_ноль()
    {
        var icmp = new ScriptedIcmp((ttl, call) => ttl switch
        {
            1 when call <= 2 => Success(call * 10, "192.168.1.1"),
            1 => new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null),
            _ => Success(50, TargetIp)
        });

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 3);

        // Средний — по двум ответам (10 и 20), а не по трём с выдуманным нулём.
        Assert.Equal(15d, result.Hops[0].AvgRttMs);
        Assert.Equal(20d, result.Hops[0].P95RttMs);
        Assert.NotEqual(0d, result.Hops[0].AvgRttMs);
    }

    [Fact]
    public async Task Лимит_времени_останавливает_трассу_и_отдаёт_частичный_результат()
    {
        var icmp = new SlowIcmp(TimeSpan.FromMilliseconds(40));

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(
            IPAddress.Parse(TargetIp),
            maxHops: 30,
            timeLimit: TimeSpan.FromMilliseconds(90));

        Assert.Equal(ServerPathStopReason.TimeLimit, result.StopReason);
        Assert.True(result.Hops.Count >= 1);
        Assert.Equal(result.Hops.Sum(hop => hop.ProbesSent), result.ProbesSent);
    }

    [Fact]
    public async Task Отмена_по_кнопке_отдаёт_то_что_успели_измерить()
    {
        using var cts = new CancellationTokenSource();

        var icmp = new ScriptedIcmp((ttl, call) =>
        {
            if (call == 2)
            {
                cts.Cancel();
            }

            return Success(5 + call, "192.168.1.1");
        });

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(
            IPAddress.Parse(TargetIp), maxHops: 30, ct: cts.Token);

        Assert.Equal(ServerPathStopReason.Cancelled, result.StopReason);
        Assert.Single(result.Hops);
        Assert.Equal(2, result.Hops[0].ProbesSent);
        Assert.NotNull(result.Hops[0].AvgRttMs);
    }

    [Fact]
    public async Task Отмена_до_старта_не_отправляет_ни_одной_пробы()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var icmp = new ScriptedIcmp((_, _) => Success(5, "192.168.1.1"));

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(
            IPAddress.Parse(TargetIp), maxHops: 30, ct: cts.Token);

        Assert.Empty(result.Hops);
        Assert.Equal(0, result.ProbesSent);
        Assert.Equal(ServerPathStopReason.Cancelled, result.StopReason);
    }

    [Fact]
    public async Task Отказ_в_правах_останавливает_трассу_честным_текстом()
    {
        var icmp = new ScriptedIcmp((_, _) => new IcmpProbeReply(IcmpProbeStatus.PermissionDenied, 0, null));

        ServerPathResult result = await new ServerPathTracer(icmp).TraceAsync(IPAddress.Parse(TargetIp), maxHops: 5);

        Assert.Equal(ServerPathStopReason.PermissionDenied, result.StopReason);
        Assert.Contains("ICMP", result.StopNoteRu, StringComparison.Ordinal);
        Assert.Equal(0, result.ProbesSent);
    }

    // ── Разбор «где растёт задержка» ───────────────────────────────────────────────────────

    [Fact]
    public void Прирост_на_первом_шаге_называет_домашнюю_сеть()
    {
        var hops = new List<ServerPathProbeHop>
        {
            Hop(1, "192.168.1.1", 9, 10, 11),
            Hop(2, "10.0.0.1", 10, 11, 12),
            Hop(3, "203.0.113.9", 11, 12, 13),
        };

        Assert.Equal(1, ServerPathGrowth.Bottleneck(hops)?.HopNumber);
        Assert.Contains("домашней сети", ServerPathGrowth.DescribeRu(hops), StringComparison.Ordinal);
    }

    [Fact]
    public void Прирост_дальше_по_трассе_подсказывает_альтернативный_маршрут()
    {
        var hops = new List<ServerPathProbeHop>
        {
            Hop(1, "192.168.1.1", 2, 3, 2),
            Hop(2, "10.0.0.1", 3, 4, 3),
            Hop(3, "203.0.113.9", 42, 44, 43),
        };

        Assert.Equal(3, ServerPathGrowth.Bottleneck(hops)?.HopNumber);

        string note = ServerPathGrowth.DescribeRu(hops);
        Assert.Contains("Сервер маршрутизации", note, StringComparison.Ordinal);
        Assert.Contains("Измерить все пути", note, StringComparison.Ordinal);
        Assert.Contains("WARP", note, StringComparison.Ordinal);
        Assert.Contains("/32", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Ровная_трасса_не_выдумывает_виновника()
    {
        var hops = new List<ServerPathProbeHop>
        {
            Hop(1, "192.168.1.1", 2, 2, 2),
            Hop(2, "10.0.0.1", 2.2, 2.2, 2.2),
            Hop(3, "203.0.113.9", 2.4, 2.4, 2.4),
        };

        Assert.Null(ServerPathGrowth.Bottleneck(hops));
        Assert.Contains("ровная", ServerPathGrowth.DescribeRu(hops), StringComparison.Ordinal);
    }

    [Fact]
    public void Трасса_без_ответов_не_делает_выводов()
    {
        var hops = new List<ServerPathProbeHop>
        {
            new(1, null, 3, 0, Array.Empty<double>()),
            new(2, null, 3, 0, Array.Empty<double>()),
        };

        Assert.Empty(ServerPathGrowth.Points(hops));
        Assert.Null(ServerPathGrowth.Bottleneck(hops));
        Assert.Contains("Ни один хоп не ответил", ServerPathGrowth.DescribeRu(hops), StringComparison.Ordinal);
    }

    [Fact]
    public void Анонимный_хоп_не_становится_источником_прироста()
    {
        var hops = new List<ServerPathProbeHop>
        {
            Hop(1, "192.168.1.1", 5, 5, 5),
            new(2, null, 3, 0, Array.Empty<double>()),
            Hop(3, "203.0.113.9", 6, 6, 6),
        };

        IReadOnlyList<ServerPathGrowthPoint> points = ServerPathGrowth.Points(hops);

        Assert.Equal(2, points.Count);
        Assert.DoesNotContain(points, point => point.HopNumber == 2);
        Assert.Equal(1, ServerPathGrowth.Bottleneck(hops)?.HopNumber);
    }

    // ── Модель окна: без игры — ноль пакетов, отмена, строки ────────────────────────────────

    [Fact]
    public async Task Без_живой_игры_ноль_пакетов_и_честный_текст()
    {
        var runner = new FakeRunner();
        var model = new ServerPathViewModel(runner, () => null);

        await model.TraceAsync();

        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, model.SentPackets);
        Assert.Empty(model.Hops);
        Assert.Contains("Сначала запустите игру", model.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Имя_вместо_IPv4_не_трассируется()
    {
        var runner = new FakeRunner();
        var model = new ServerPathViewModel(runner, () => "game.example:27015");

        await model.TraceAsync();

        Assert.Equal(0, runner.Calls);
        Assert.Contains("не IPv4", model.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task После_трассы_анонимный_хоп_показан_прочерком_а_цель_подсвечена()
    {
        var runner = new FakeRunner
        {
            Behavior = _ => Task.FromResult(new ServerPathResult(
                [
                    Hop(1, "192.168.1.1", 4, 4, 4),
                    new(2, null, 3, 0, Array.Empty<double>()),
                    Hop(3, TargetIp, 40, 41, 42),
                ],
                9,
                6,
                ServerPathStopReason.ReachedTarget))
        };
        var model = new ServerPathViewModel(runner, () => TargetIp + ":27015");

        await model.TraceAsync();

        Assert.Equal(3, model.Hops.Count);
        Assert.Equal("не отвечает", model.Hops[1].AddressRu);
        Assert.Equal("—", model.Hops[1].AvgRttRu);
        Assert.Equal("—", model.Hops[1].LossRu);
        Assert.True(model.Hops[2].IsBottleneck);
        Assert.Equal(9, model.SentPackets);
        Assert.Contains("ответили: 2", model.SummaryRu, StringComparison.Ordinal);
        Assert.Contains("Сервер маршрутизации", model.GrowthNoteRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Отмена_кнопкой_завершает_трассу_и_освобождает_кнопку()
    {
        var runner = new FakeRunner
        {
            Behavior = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ServerPathResult([], 0, 0, ServerPathStopReason.Cancelled);
            }
        };
        var model = new ServerPathViewModel(runner, () => TargetIp + ":27015");

        Task run = model.TraceAsync();
        await WaitUntilAsync(() => model.IsRunning);
        Assert.True(model.CancelCommand.CanExecute(null));

        model.Cancel();
        await run;

        Assert.False(model.IsRunning);
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.Contains("отменена", model.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Повторное_нажатие_во_время_трассы_не_запускает_вторую()
    {
        var completion = new TaskCompletionSource<ServerPathResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner { Behavior = _ => completion.Task };
        var model = new ServerPathViewModel(runner, () => TargetIp + ":27015");

        Task first = model.TraceAsync();
        await WaitUntilAsync(() => model.IsRunning);
        await model.TraceAsync();

        Assert.Equal(1, runner.Calls);

        completion.SetResult(new ServerPathResult(
            [Hop(1, TargetIp, 10, 10, 10)], 3, 3, ServerPathStopReason.ReachedTarget));
        await first;

        Assert.False(model.IsRunning);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void Кнопка_отмены_недоступна_в_покое()
    {
        var model = new ServerPathViewModel(new FakeRunner(), () => null);

        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.Contains("не строился", model.StatusRu, StringComparison.Ordinal);
        Assert.Equal(0, model.SentPackets);
    }

    [Fact]
    public async Task Ошибка_трассы_не_оставляет_старые_числа_за_свежие()
    {
        var runner = new FakeRunner
        {
            Behavior = _ => throw new InvalidOperationException("сеть недоступна")
        };
        var model = new ServerPathViewModel(runner, () => TargetIp + ":27015");

        await model.TraceAsync();

        Assert.Equal(0, model.SentPackets);
        Assert.Empty(model.Hops);
        Assert.Contains("не удалось построить", model.StatusRu, StringComparison.Ordinal);
        Assert.False(model.IsRunning);
    }

    [Fact]
    public void В_разметке_есть_блок_и_кнопка_пути_до_сервера()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-OptimizeView.xaml");
        string xaml = File.ReadAllText(path);

        Assert.Contains("x:Name=\"ServerPathCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ServerPathButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Показать путь", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding TraceCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding CancelCommand", xaml, StringComparison.Ordinal);
    }

    // ── Двойники и помощники ────────────────────────────────────────────────────────────────

    private static IcmpProbeReply Success(double ms, string responder) =>
        new(IcmpProbeStatus.Success, ms, IPAddress.Parse(responder));

    private static ServerPathProbeHop Hop(int number, string address, params double[] samples) =>
        new(number, address, samples.Length, samples.Length, samples);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "условие не наступило за отведённое время");
    }

    private sealed class ScriptedIcmp(Func<int, int, IcmpProbeReply> handler) : IIcmpProbe
    {
        private int _calls;

        public bool RequiresAdministratorRights => false;

        public Task<IcmpProbeReply> SendAsync(
            IPAddress target,
            int payloadBytes,
            bool dontFragment,
            int ttl,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            int call = ++_calls;
            return Task.FromResult(handler(ttl, call));
        }
    }

    private sealed class SlowIcmp(TimeSpan delay) : IIcmpProbe
    {
        public bool RequiresAdministratorRights => false;

        public async Task<IcmpProbeReply> SendAsync(
            IPAddress target,
            int payloadBytes,
            bool dontFragment,
            int ttl,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            await Task.Delay(delay, CancellationToken.None);
            return new IcmpProbeReply(IcmpProbeStatus.Timeout, 0, null);
        }
    }

    private sealed class FakeRunner : IServerPathRunner
    {
        public int Calls { get; private set; }

        public Func<CancellationToken, Task<ServerPathResult>> Behavior { get; set; } =
            _ => Task.FromResult(new ServerPathResult([], 0, 0, ServerPathStopReason.Cancelled));

        public Task<ServerPathResult> TraceAsync(
            IPAddress destination,
            Action<ServerPathProbeHop>? onHop = null,
            CancellationToken ct = default)
        {
            Calls++;
            return Behavior(ct);
        }
    }
}
