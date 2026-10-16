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
    string ExampleRu);

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
            ExampleRu: """{"id":"4","command":"apply-preset","args":{"preset":"low-latency","restorePoint":"true"}}"""),

        new(
            EngineCommandKind.ApplyTweaks,
            "apply-tweaks",
            "Применить выбранные твики",
            "То же, но список твиков задан по кодам — так работает ручной выбор в интерфейсе.",
            NeedsElevation: true,
            RequiredArgs: ["tweaks"],
            OptionalArgs: ["restorePoint"],
            ExampleRu: """{"id":"5","command":"apply-tweaks","args":{"tweaks":"TCP_NO_DELAY,GAME_MODE_ON"}}"""),

        new(
            EngineCommandKind.RollbackSnapshot,
            "rollback",
            "Вернуть прежние настройки",
            "Возвращает параметры из снапшота. Без аргумента берётся последний снапшот — тот, который делался перед последним применением.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: ["snapshot"],
            ExampleRu: """{"id":"6","command":"rollback","args":{"snapshot":"snap-2026-10-16-001"}}"""),

        new(
            EngineCommandKind.RepairNetwork,
            "repair-network",
            "Починить сеть",
            "Снимает остатки после аварии: маршруты, добавленные программой, её политики QoS и незакрытые операции из журнала. Чужие маршруты не трогает.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"7","command":"repair-network"}"""),

        new(
            EngineCommandKind.JournalTail,
            "journal-tail",
            "Последние записи журнала",
            "Что программа меняла и что из этого получилось. По умолчанию 20 записей, предел 500.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: ["limit"],
            ExampleRu: """{"id":"8","command":"journal-tail","args":{"limit":"50"}}"""),

        new(
            EngineCommandKind.RealtimeStart,
            "realtime-start",
            "Начать живую оптимизацию",
            "Ищет игру, замеряет «до», применяет действия и контролирует результат. Ставит маршрут и поднимает фильтр только на адрес игрового сервера.",
            NeedsElevation: true,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"9","command":"realtime-start"}"""),

        new(
            EngineCommandKind.RealtimeStop,
            "realtime-stop",
            "Остановить и вернуть как было",
            "Снимает свои изменения и закрывает фильтр. Остановка не оставляет за собой маршрутов.",
            NeedsElevation: false,
            RequiredArgs: [],
            OptionalArgs: [],
            ExampleRu: """{"id":"10","command":"realtime-stop"}"""),

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
            ExampleRu: """{"id":"12","command":"system-state"}"""),
    ];

    /// <summary>Все команды в порядке объявления — он же порядок вывода в интерфейсе.</summary>
    public static IReadOnlyList<EngineCommandDescriptor> All { get; } = Items;

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
