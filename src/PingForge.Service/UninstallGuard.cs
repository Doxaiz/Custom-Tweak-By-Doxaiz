namespace PingForge.Service;

/// <summary>
/// Что известно о следах программы в системе перед удалением (задача T).
/// Считается по журналу изменений, незакрытым операциям и снапшотов; ни одно поле не выдумано —
/// исполнитель, у которого нет журнала, передаёт нули и получает вердикт «спрашивать не о чём».
/// </summary>
public sealed record UninstallState(
    int AppliedChanges,
    int OpenOperations,
    int Snapshots,
    bool OptimizationRunning)
{
    /// <summary>Есть ли что возвращать: применённые записи журнала или незакрытые операции.</summary>
    public bool HasAnythingToRevert => AppliedChanges > 0 || OpenOperations > 0;

    /// <summary>Есть ли чем возвращать: хоть один снапшот.</summary>
    public bool CanRollback => Snapshots > 0;
}

/// <summary>Вариант, который выбрал человек (или CI-скрипт) при удалении с применёнными изменениями.</summary>
public enum UninstallChoice
{
    /// <summary>Вернуть настройки Windows как было и удалить программу — вариант по умолчанию.</summary>
    RollbackThenRemove,

    /// <summary>Удалить программу, оставив изменения. Требует явного подтверждения.</summary>
    KeepChangesAndRemove,

    /// <summary>Отмена: ничего не удалять.</summary>
    Cancel,
}

/// <summary>
/// Решение «спрашивать или нет» перед удалением — в одном месте, без Windows и без ввода-вывода,
/// чтобы его проверяли тесты. Скрипт <c>uninstall-service.ps1</c> и служба вызывают именно эту логику:
/// два пути удаления не могут разойтись по поведению.
/// </summary>
public static class UninstallGuard
{
    /// <summary>Вопрос и варианты в том порядке, в котором их показывает удаление. Первый — по умолчанию.</summary>
    public static IReadOnlyList<string> OptionsRu { get; } =
    [
        "Вернуть настройки Windows как было и удалить программу",
        "Удалить программу, оставив изменения",
        "Отмена",
    ];

    /// <summary>Порядок шагов отката — как в задаче L: остановить → снять QoS и маршруты → вернуть снапшоты → проверить остатки.</summary>
    public static IReadOnlyList<string> RollbackSequenceWireNames { get; } =
    [
        "realtime-stop",
        "repair-network",
        "rollback",
        "status",
    ];

    /// <summary>Спросить ли человека вообще. Применённых изменений и незакрытых операций нет — лишних вопросов не задаём.</summary>
    public static bool MustAsk(UninstallState state) =>
        state.HasAnythingToRevert || state.OptimizationRunning;

    /// <summary>Текст вопроса со всеми числами. Пустая строка, если спрашивать не о чём.</summary>
    public static string QuestionRu(UninstallState state)
    {
        if (!MustAsk(state))
        {
            return "Применённых изменений нет — можно удалять спокойно, программа ничего за собой не оставила.";
        }

        string changes = state.AppliedChanges > 0
            ? $"в журнале числится применённых изменений: {state.AppliedChanges}"
            : "применённых изменений в журнале нет";
        string open = state.OpenOperations > 0
            ? $", незакрытых операций: {state.OpenOperations} (их снимет ремонт сети)"
            : "";
        string running = state.OptimizationRunning ? ", живая оптимизация идёт — её сначала остановим" : string.Empty;
        string snapnote = state.CanRollback
            ? $"Снапшотов для отката: {state.Snapshots}."
            : "Снапшотов нет — вернуть настройки автоматически нечем, откат будет только снятием своих изменений.";

        return "Программа оставляла след в системе: " + changes + open + running + ". " + snapnote + " ";
    }

    /// <summary>Предупреждение для варианта «оставить изменения» — чтобы выбор был сделан глазами, а не клавишей.</summary>
    public static string KeepChangesWarningRu(UninstallState state) =>
        "Внимание: настройки Windows останутся изменёнными, а откатывать их будет нечем — программа удалится " +
        "вместе со снапшотами" + (state.Snapshots > 0 ? $", которых сейчас {state.Snapshots}" : "") +
        ". Вернуть вид «как до программы» после этого можно только вручную.";

    /// <summary>Вариант «оставить изменения» обязан быть подтверждён явно — случайным нажатиемEnter не принимается.</summary>
    public static bool ChoiceNeedsExplicitConfirmation(UninstallChoice choice) =>
        choice == UninstallChoice.KeepChangesAndRemove;

    /// <summary>
    /// Разрешено ли выбранное. Возвращает null, если выбор легален, и текст отказа — если нет.
    /// «Оставить изменения» без явного подтверждения — отказ; отмена — тоже «разрешено» (ничего не делаем).
    /// </summary>
    public static string? ValidateChoice(UninstallChoice choice, bool explicitlyConfirmed)
    {
        if (ChoiceNeedsExplicitConfirmation(choice) && !explicitlyConfirmed)
        {
            return KeepChangesWarningRu(new UninstallState(0, 0, 0, false)) +
                " Для этого варианта нужен явный флаг подтверждения (в скрипте — -ConfirmKeepChanges).";
        }

        return null;
    }

    /// <summary>
    /// Правило для -RemoveData (требование 2): снапшоты удаляются, а изменения не сняты — отказ,
    /// пока человек не скажет -Force. Отказ описывает, что именно произойдёт, а не просто «нет».
    /// </summary>
    public static string? ValidateRemoveData(UninstallState state, bool rolledBackOrNothingToDo, bool removeData, bool force)
    {
        if (!removeData)
        {
            return null;
        }

        if (!state.HasAnythingToRevert || rolledBackOrNothingToDo)
        {
            return null;
        }

        if (force)
        {
            return null;
        }

        return
            "Удалять данные нельзя: изменения не сняты, а вместе с данными исчезнут снапшоты, " +
            $"которыми их можно вернуть (применённых изменений: {state.AppliedChanges}, незакрытых операций: " +
            $"{state.OpenOperations}, снапшотов: {state.Snapshots}). Сделайте откат (вариант 1 при удалении) " +
            "или удалите с -Force, понимая, что откатывать будет нечем.";
    }

    /// <summary>
    /// Вердикт проверки остатков после отката (последний шаг последовательности).
    ///Null-вход — пропущенный шаг; ненулевой счётчик — называет то, что не снялось.
    /// </summary>
    public static string ResidualCheckRu(int? openOperationsAfter, string? failedStep)
    {
        if (failedStep is not null)
        {
            return $"Откат не выполнен до конца: шаг «{failedStep}» не прошёл. Удаление остановлено, файлы и данные на месте.";
        }

        return openOperationsAfter is > 0
            ? $"После отката остались незакрытые операции: {openOperationsAfter}. Удаление остановлено — сначала снимите их (repair-network)."
            : "Остатков нет: настройки возвращены, удаление можно продолжать.";
    }
}
