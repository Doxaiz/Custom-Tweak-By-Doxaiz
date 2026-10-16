using PingForge.Core.Abstractions;
using PingForge.Network.Qos;
using PingForge.Orchestration.Realtime;

namespace PingForge.Network.Actions;

/// <summary>
/// Действие «приоритет пакетов игры (DSCP)»: создаёт политику QoS для процесса игры.
///
/// Что здесь честно ограничено:
///   • политика помечает трафик конкретного процесса (не всей системы) и удаляется при остановке;
///   • выигрыш возможен только там, где маршрутизатор уважает DSCP и канал чем-то нагружен — иначе ноль;
///   • факт маркировки на выходе программа подтвердить не может без драйвера, поэтому в отчёте это сказано прямо,
///     а подтверждением считается только запись политики в реестре.
/// </summary>
public sealed class DscpMarkingActionRunner : IOptimizationActionRunner
{
    private readonly QosDscpPolicyWriter _writer;
    private readonly Func<GameFlowInfo?> _currentFlow;
    private readonly int _dscp;
    private readonly bool _markWholeProcess;

    public DscpMarkingActionRunner(
        IRegistryPort? registry,
        Func<GameFlowInfo?> currentFlow,
        int dscp = QosDscpPolicyWriter.ExpeditedForwardingDscp,
        bool markWholeProcess = true)
    {
        _writer = new QosDscpPolicyWriter(registry);
        _currentFlow = currentFlow ?? throw new ArgumentNullException(nameof(currentFlow));
        _dscp = dscp;
        _markWholeProcess = markWholeProcess;
    }

    public async Task<OptimizationActionOutcome> ApplyAsync(
        OptimizationActionDescriptor action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Kind != OptimizationActionKind.DscpMarking)
        {
            return OptimizationActionOutcome.Fail($"Исполнитель QoS не умеет действие «{action.TitleRu}».");
        }

        GameFlowInfo? flow = _currentFlow();
        if (flow is null)
        {
            return OptimizationActionOutcome.Fail("Игровой поток не найден: помечать нечего.");
        }

        QosPolicySpec spec = BuildSpec(flow);
        QosPolicyWriteOutcome outcome = await _writer.ApplyAsync(spec, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return OptimizationActionOutcome.Fail(outcome.MessageRu);
        }

        QosPolicyVerification verification = await _writer.VerifyAsync(spec, ct).ConfigureAwait(false);
        if (!verification.PolicyPresent)
        {
            return OptimizationActionOutcome.Fail(
                "Политика QoS записана, но при перечитывании её нет: политику могла заблокировать групповая политика " +
                "или права. Маркировка не применена.");
        }

        return OptimizationActionOutcome.Ok(
            $"{outcome.MessageRu} Проверено по реестру: DSCP {verification.DscpValueRaw}, " +
            $"процесс {verification.ApplicationNameRaw}. Подтвердить, что маркировка дошла до роутера, без драйвера нельзя — " +
            "в отчёте это будет сказано честно.");
    }

    public async Task<OptimizationActionOutcome> RevertAsync(
        OptimizationActionDescriptor action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        GameFlowInfo? flow = _currentFlow();
        if (flow is null)
        {
            return OptimizationActionOutcome.Fail(
                "Снять политику QoS не удалось: неизвестен процесс игры. Имя политики записано в журнал операций.");
        }

        QosPolicyWriteOutcome outcome = await _writer.RemoveAsync(BuildSpec(flow), ct).ConfigureAwait(false);
        return outcome.Succeeded
            ? OptimizationActionOutcome.Ok(outcome.MessageRu)
            : OptimizationActionOutcome.Fail(outcome.MessageRu);
    }

    /// <summary>Политика на процесс, а не на конкретный адрес: игра может сменить сервер, политика останется рабочей.</summary>
    private QosPolicySpec BuildSpec(GameFlowInfo flow)
    {
        string protocol = flow.Transport.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? "UDP"
            : flow.Transport.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? "TCP"
            : "*";

        return new QosPolicySpec(
            PolicyName: QosDscpPolicyWriter.PolicyNameFor(flow.ProcessName, _dscp),
            ApplicationPath: flow.ProcessName,
            Protocol: protocol,
            RemoteIp: _markWholeProcess ? "*" : flow.RemoteEndpoint,
            DscpValue: _dscp);
    }
}
