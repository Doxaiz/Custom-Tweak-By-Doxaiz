namespace PingForge.Service.Protocol;

/// <summary>Описание одной команды службы: как называется по каналу, что делает и нужны ли права.</summary>
public sealed record EngineCommandDescriptor(
    EngineCommandKind Kind,
    string WireName,
    string TitleRu,
    string WhatRu,
    bool NeedsElevation,
    IReadOnlyList<string> RequiredArgs,
    IReadOnlyList<string> OptionalArgs,
    string ExampleRu,
    int TimeoutSeconds = 10);

/// <summary>
/// Список команд службы в одном месте. Из него собирается и текст команды <c>capabilities</c>,
/// и проверка аргументов, и таблица «что умеет служба» в интерфейсе. Список закрытый: чего здесь нет,
/// того служба не сделает даже по правильному запросу.
/// </summary>
public static class EngineCommandCatalog
{
    private static readonly EngineCommandDescriptor[] Items =
    [
        new(
            EngineCommandKind.Ping,
            "ping",
            "Проверить связь со службой",
            "Отвечает версией протокола и режимом прав. Ничего не меняет — безопасно звать как угодно часто.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"1","command":"ping"}"""),

        new(
            EngineCommandKind.Capabilities,
            "capabilities",
            "Список команд",
            "Возвращает этот перечень: что служба умеет, какие аргументы нужны и где требуются права администратора.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"2","command":"capabilities"}"""),

        new(
            EngineCommandKind.Status,
            "status",
            "Что сейчас происходит",
            "Идёт ли оптимизация, какая игра найдена, какой профиль потребления включён, сколько изменений висит в журнале.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"3","command":"status"}"""),

        new(
            EngineCommandKind.ApplyPreset,
            "apply-preset",
            "Применить пресет твиков",
            "Снимает снапшот, применяет твики пресета и проверяет каждый. Пресеты: низкий пинг, максимальная скорость, кадры.",
            NeedsElevation: true,
            RequiredArgs: ["preset"],
            OptionalArgs: ["restorePoint"],
            ExampleRu: """{"id":"4","command":"apply-preset","args":{"preset":"low-latency","restorePoint":"true"}}""",
            TimeoutSeconds: 180),

        new(
            EngineCommandKind.ApplyTweaks,
            "apply-tweaks",
            "Применить выбранные твики",
            "То же, но список твиков задан по кодам — так работает ручной выбор в интерфейсе.",
            NeedsElevation: true,
            RequiredArgs: ["tweaks"],
            OptionalArgs: ["restorePoint"],
            ExampleRu: """{"id":"5","command":"apply-tweaks","args":{"tweaks":"TCP_NO_DELAY,GAME_MODE_ON"}}""",
            TimeoutSeconds: 300),

        new(
            EngineCommandKind.RollbackSnapshot,
            "rollback",
            "Вернуть прежние настройки",
            "Возвращает параметры из снапшота. Без аргумента берётся последний снапшот — тот, который делался перед последним применением.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: ["snapshot"],
            ExampleRu: """{"id":"6","command":"rollback","args":{"snapshot":"snap-2026-10-16-001"}}""",
            TimeoutSeconds: 300),

        new(
            EngineCommandKind.RepairNetwork,
            "repair-network",
            "Починить сеть",
            "Снимает остатки после аварии: маршруты, добавленные программой, её политики QoS и незакрытые операции из журнала. Чужие маршруты не трогает.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"7","command":"repair-network"}""",
            TimeoutSeconds: 120),

        new(
            EngineCommandKind.JournalTail,
            "journal-tail",
            "Последние записи журнала",
            "Что программа меняла и что из этого получилось. По умолчанию 20 записей, предел 500.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: ["limit"],
            ExampleRu: """{"id":"8","command":"journal-tail","args":{"limit":"50"}}""",
            TimeoutSeconds: 20),

        new(
            EngineCommandKind.RealtimeStart,
            "realtime-start",
            "Начать живую оптимизацию",
            "Ищет игру, замеряет «до», применяет действия и контролирует результат. Ставит маршрут и поднимает фильтр только на адрес игрового сервера.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"9","command":"realtime-start"}""",
            TimeoutSeconds: 180),

        new(
            EngineCommandKind.RealtimeStop,
            "realtime-stop",
            "Остановить и вернуть как было",
            "Снимает свои изменения и закрывает фильтр. Остановка не оставляет за собой маршрутов.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"10","command":"realtime-stop"}""",
            TimeoutSeconds: 120),

        new(
            EngineCommandKind.PerfProfile,
            "perf-profile",
            "Профиль потребления ресурсов",
            "Сколько программа позволяет себе тратить: экономно, обычно или максимум реакции.",
            NeedsElevation: false,
            RequiredArgs: ["profile"],
            OptionalArgs: [],
            ExampleRu: """{"id":"11","command":"perf-profile","args":{"profile":"economy"}}"""),

        new(
            EngineCommandKind.ReadSystemState,
            "system-state",
            "Прочитать состояние системы",
            "Показывает, что сейчас настроено в Windows и сети: версия, права, активный адаптер, скорость, " +
            "MTU, DNS, состояние сетевых настроек. Ничего не меняет — только чтение.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"12","command":"system-state"}""",
            TimeoutSeconds: 30),

        new(
            EngineCommandKind.RouteDiscover,
            "route-discover",
            "Найти узлы маршрутизации",
            "Смотрит таблицу маршрутов и список адаптеров и называет реальные шлюзы: текущий, второй канал, " +
            "VPN-адаптер — с именем адаптера и честным источником. Плюс узлы, добавленные вручную. Своей сети " +
            "узлов у программы нет, и выдуманных «серверов PingForge» в ответе не бывает. Только чтение.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"13","command":"route-discover"}""",
            TimeoutSeconds: 30),

        new(
            EngineCommandKind.RouteMeasure,
            "route-measure",
            "Измерить пути до игрового сервера",
            "Меряет RTT до адреса сервера запущенной игры напрямую и через каждый сохранённый узел: временный " +
            "маршрут /32 на адрес сервера, тот же замер, снятие в любом случае и сверка таблицы с исходной. " +
            "Побеждает только измеренный выигрыш; без него вердикт «прямой путь не хуже, ничего не меняем».",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"14","command":"route-measure"}""",
            TimeoutSeconds: 240),

        new(
            EngineCommandKind.UninstallCheck,
            "uninstall-check",
            "Проверить следы перед удалением",
            "Считает применённые изменения, незакрытые операции и снапшоты и отвечает, надо ли спрашивать " +
            "«вернуть настройки как было» до удаления программы. Только чтение: ничего не снимает и не удаляет.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"15","command":"uninstall-check"}""",
            TimeoutSeconds: 20),

        new(
            EngineCommandKind.Sessions,
            "sessions",
            "История сессий оптимизации",
            "Список сессий: когда, какая игра и сервер, «до» и «после», действия и пометка «снялось / не снялось». " +
            "Сессия без измеренного «после» честно называется незавершённой. Только чтение, по умолчанию 50 записей, " +
            "предел 500.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: ["limit"],
            ExampleRu: """{"id":"16","command":"sessions","args":{"limit":"50"}}""",
            TimeoutSeconds: 20),

        new(
            EngineCommandKind.SessionsClear,
            "sessions-clear",
            "Очистить историю сессий",
            "Удаляет историю замеров сессий. Журнал изменений системы не трогается: «забыть замеры» не значит " +
            "«забыть, что программа меняла». Действие необратимо, поэтому требует прав администратора.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"17","command":"sessions-clear"}""",
            TimeoutSeconds: 30),
    ];

    /// <summary>Все команды в порядке объявления — он же порядок вывода в интерфейсе.</summary>
    public static IReadOnlyList<EngineCommandDescriptor> All { get; } = Items;

    /// <summary>
    /// Команды, доступные в канале только для чтения (C2). Единственное определение в коде: имена
    /// по каналу считаются отсюда, описание в документах сверяется с этим списком тестом, и диспетчер
    /// проверяет по нему же. Всё, чего здесь нет, канал чтения не выполняет — даже для администратора:
    /// изменения делаются через основной канал, где действуют проверки прав.
    ///
    /// <c>realtime-stop</c> здесь намеренно: он ничего не настраивает, а снимает СВОИ изменения —
    /// «убрать за собой» должно работать без прав.
    /// </summary>
    public static IReadOnlyList<EngineCommandKind> ReadOnlyChannelKinds { get; } =
    [
        EngineCommandKind.Ping,
        EngineCommandKind.Capabilities,
        EngineCommandKind.Status,
        EngineCommandKind.JournalTail,
        EngineCommandKind.ReadSystemState,
        EngineCommandKind.RouteDiscover,
        EngineCommandKind.RealtimeStop,
        EngineCommandKind.UninstallCheck,
        EngineCommandKind.Sessions,
    ];

    /// <summary>Имена тех же команд по каналу — для сверки с документами и текстов отказов.</summary>
    public static IReadOnlyList<string> ReadOnlyChannelWireNames { get; } =
        ReadOnlyChannelKinds.Select(kind => Get(kind).WireName).ToArray();

    /// <summary>Разрешена ли команда в канале только для чтения.</summary>
    public static bool IsAllowedOnReadOnlyChannel(EngineCommandDescriptor descriptor) =>
        ReadOnlyChannelKinds.Contains(descriptor.Kind);

    /// <summary>Список команд канала чтения обычным текстом — для отказов и описания правил.</summary>
    public static string ReadOnlyChannelWireNamesRu() => string.Join(", ", ReadOnlyChannelWireNames);

    public static EngineCommandDescriptor? Find(string? wireName)
    {
        if (string.IsNullOrWhiteSpace(wireName))
        {
            return null;
        }

        string trimmed = wireName.Trim();
        return All.FirstOrDefault(item => string.Equals(item.WireName, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static EngineCommandDescriptor Get(EngineCommandKind kind) =>
        All.First(item => item.Kind == kind);

    /// <summary>Имена команд по каналу — попадают в текст ошибки, чтобы пользователь видел, что доступно.</summary>
    public static string KnownWireNamesRu() => string.Join(", ", All.Select(item => item.WireName));

    /// <summary>Сколько команд требуют прав администратора. Число честное: считается из таблицы, а не пишется руками.</summary>
    public static int ElevatedCommandCount => All.Count(item => item.NeedsElevation);

    /// <summary>Описание всех команд обычным текстом: и для команды <c>capabilities</c>, и для справки в интерфейсе.</summary>
    public static string DescribeRu()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Служба PingForge, версия протокола {EngineProtocol.Version}. Команд: {All.Count}, из них требуют прав администратора: {ElevatedCommandCount}.");
        builder.AppendLine($"Канал: {EngineProtocol.PipeName}. Кадр: 4 байта длины + JSON, предел {EngineProtocol.MaxFrameBytes} байт.");
        builder.AppendLine();

        foreach (EngineCommandDescriptor item in All)
        {
            string elevation = item.NeedsElevation ? "нужны права администратора" : "права не нужны";
            string required = item.RequiredArgs.Count == 0 ? "нет" : string.Join(", ", item.RequiredArgs);
            string optional = item.OptionalArgs.Count == 0 ? "нет" : string.Join(", ", item.OptionalArgs);
            builder.AppendLine($"{item.WireName} — {item.TitleRu} ({elevation})");
            builder.AppendLine($"    что делает: {item.WhatRu}");
            builder.AppendLine($"    обязательные аргументы: {required}; необязательные: {optional}");
            builder.AppendLine($"    пример: {item.ExampleRu}");
        }

        return builder.ToString().TrimEnd();
    }
}
