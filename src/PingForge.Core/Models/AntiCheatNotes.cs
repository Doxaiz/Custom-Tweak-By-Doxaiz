namespace PingForge.Core.Models;

/// <summary>
/// Слова про анти-чит — в одном месте, потому что их говорят три экрана: планировщик действий (что сделает
/// кнопка «Оптимизировать»), каталог игр (что написано на карточке) и доклад детектора («нашли игру»).
///
/// Пока слова жили в трёх местах, они и расходились: карточка обещала «маршрут и QoS без фильтра пакетов»,
/// а доклад детектора говорил «автоматические действия выключены». Хуже того, обе фразы подразумевали
/// сетевой фильтр, которого в программе нет вовсе: маршрут и QoS ставятся настройками Windows, а перехвата
/// пакетов нет ни для одной игры, ни для одной. Обещать «не поднимать фильтр» — значит намекать, что он есть.
/// </summary>
public static class AntiCheatNotes
{
    /// <summary>
    /// Главное, что нужно сказать рядом с любой игрой: перехвата пакетов в программе нет вовсе, поэтому
    /// «мы не поднимем фильтр в игре с ядерным анти-читом» — это обещание того, чего не существует.
    /// </summary>
    public const string NoPacketInterceptionRu =
        "Перехвата пакетов в программе нет ни для одной игры: маршрут и приоритет пакетов (QoS) — настройки " +
        "Windows, а в процесс игры программа не внедряется.";

    /// <summary>Короткая строка для карточки игры: что можно, а что не проверялось.</summary>
    public static string ActionsRu(AntiCheatStatus status) => status switch
    {
        AntiCheatStatus.VerifiedSafe => "маршрут и QoS — совместимость проверена",
        AntiCheatStatus.KnownKernelConflict => "маршрут и QoS — можно, анти-чит работает в ядре",
        _ => "маршрут и QoS — можно, совместимость не проверялась",
    };

    /// <summary>
    /// Полное объяснение для подсказок и предупреждений. Неизвестное не выдаётся за проверенное,
    /// а отсутствие перехвата называется прямо, а не намёком «фильтр не поднимаем».
    /// </summary>
    public static string ExplainRu(AntiCheatStatus status, string gameTitleRu, string? antiCheatName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitleRu);

        string antiCheat = string.IsNullOrWhiteSpace(antiCheatName) ? "анти-чит" : antiCheatName.Trim();

        return status switch
        {
            AntiCheatStatus.VerifiedSafe =>
                $"Совместимость с анти-читом игры {gameTitleRu} ({antiCheat}) проверена на живых тестах. " +
                NoPacketInterceptionRu,

            AntiCheatStatus.KnownKernelConflict =>
                $"У игры {gameTitleRu} ядерный анти-чит {antiCheat}: он работает в ядре и видит драйверы и " +
                "процессы. Поэтому рядом с такой игрой программа не поднимает ничего своего — ни драйвера, " +
                "ни перехвата. " +
                NoPacketInterceptionRu + " Маршрут и QoS содержимое пакетов не меняют, поэтому остаются доступны.",

            _ =>
                $"Совместимость с анти-читом игры {gameTitleRu} на живых тестах не проверялась, и выдавать её за " +
                "проверенную мы не будем. " +
                NoPacketInterceptionRu + " Маршрут и QoS остаются доступны: это настройки Windows, а не внедрение в игру.",
        };
    }
}
