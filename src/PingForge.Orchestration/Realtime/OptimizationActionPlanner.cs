using PingForge.Core.Models;

namespace PingForge.Orchestration.Realtime;

public sealed record OptimizationPlan(
    IReadOnlyList<OptimizationActionDescriptor> Actions,
    bool IsBlocked,
    string? BlockReasonRu,
    IReadOnlyList<string> Notes);

/// <summary>
/// Решает, ЧТО именно делает кнопка «Оптимизировать соединение» для конкретной игры.
/// Правила жёсткие:
///   • без прав администратора ничего не применяется (только замеры);
///   • для игр с ядерным анти-читом рядом с игрой не появляется ничего своего: перехвата пакетов
///     в программе нет ни для одной игры, остаются маршрут и политика QoS (настройки Windows);
///   • твики здесь не трогаются: они применяются отдельной кнопкой со снапшотом и откатом.
/// </summary>
public sealed class OptimizationActionPlanner
{
    public OptimizationPlan Plan(GameFlowInfo flow, RealtimeOptions options, bool isAdministrator)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(options);

        var notes = new List<string>();

        if (!isAdministrator)
        {
            return new OptimizationPlan(
                Array.Empty<OptimizationActionDescriptor>(),
                IsBlocked: true,
                BlockReasonRu:
                    "Программа запущена без прав администратора. Замеры живого потока делать можно, а менять маршруты и " +
                    "политику QoS — нет. Запустите PingForge от имени администратора и нажмите кнопку снова.",
                Notes: notes);
        }

        var actions = new List<OptimizationActionDescriptor>();

        if (options.AllowRoutingChanges)
        {
            actions.Add(new OptimizationActionDescriptor(
                "route:game-server",
                OptimizationActionKind.RouteForGame,
                "Маршрут для игрового сервера",
                $"Маршрут добавляется только для адреса {flow.RemoteEndpoint} и снимается сразу после остановки. " +
                "Это не VPN: чужой трафик идёт как обычно."));
        }

        if (options.AllowDscpMarking)
        {
            actions.Add(new OptimizationActionDescriptor(
                "qos:dscp-46",
                OptimizationActionKind.DscpMarking,
                "Приоритет пакетов игры (DSCP 46)",
                $"Политика QoS помечает пакеты процесса {flow.ProcessName} как «сетевое управление». " +
                "Помогает только если домашний роутер уважает DSCP и канал чем-то нагружен: иначе выигрыша не будет."));
        }

        // Слова про анти-чит — те же, что на карточке игры и в докладе детектора: один источник.
        // Раньше здесь было «перехват пакетов для такой игры запрещён настройками» — настройки не существует,
        // да и перехвата в программе нет вовсе, и обещать «не поднимать фильтр» значило намекать, что он есть.
        if (flow.AntiCheat != AntiCheatStatus.VerifiedSafe)
        {
            notes.Add(AntiCheatNotes.ExplainRu(flow.AntiCheat, flow.GameTitleRu));
        }

        if (actions.Count == 0)
        {
            return new OptimizationPlan(
                actions,
                IsBlocked: true,
                BlockReasonRu: "Все действия оптимизации выключены в настройках. Включите хотя бы «маршрут для игрового сервера».",
                Notes: notes);
        }

        notes.Add("Твики сети этой кнопкой не применяются: для них есть отдельное окно со снапшотом и точкой восстановления.");

        return new OptimizationPlan(actions, IsBlocked: false, BlockReasonRu: null, Notes: notes);
    }
}
