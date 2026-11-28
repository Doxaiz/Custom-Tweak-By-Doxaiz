using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Вердикт по твику: «стоит включать или нет». Требование к продукту было прямым — у каждого твика
/// должно быть явно написано, стоит ли его включать. Поэтому формулировки живут в одном месте и
/// проверяются тестами, а интерфейс только показывает то, что здесь получилось.
/// </summary>
public static class TweakVerdict
{
    /// <summary>Короткий вердикт для карточки: «включать: да», «по желанию», «осторожно», «не советуем».</summary>
    public static string ShortRu(GameRecommendationBadge badge) => badge switch
    {
        GameRecommendationBadge.Recommended => "включать: да",
        GameRecommendationBadge.Optional => "по желанию",
        GameRecommendationBadge.Caution => "осторожно",
        GameRecommendationBadge.NotRecommended => "не советуем",
        _ => "нет вердикта",
    };

    /// <summary>
    /// Вес вердикта для сортировки в списке: 0 — «включать: да», 1 — «по желанию», 2 — «осторожно»,
    /// 3 — «не советуем», 4 — вердикта нет (такое не должно случаться и видно в конце списка).
    /// Строки те же, что в <see cref="ShortRu"/>: тест сверяет вес с текстом каждого решения, поэтому
    /// переименовать вердикт и забыть про сортировку не получится.
    /// </summary>
    public static int Rank(string? verdictRu) => verdictRu switch
    {
        "включать: да" => 0,
        "по желанию" => 1,
        "осторожно" => 2,
        "не советуем" => 3,
        _ => 4,
    };

    /// <summary>Плашка каталога — то же решение, но словами каталога.</summary>
    public static string BadgeRu(GameRecommendationBadge badge) => badge switch
    {
        GameRecommendationBadge.Recommended => "Рекомендую для игр",
        GameRecommendationBadge.Optional => "По желанию",
        GameRecommendationBadge.Caution => "С осторожностью",
        GameRecommendationBadge.NotRecommended => "Не рекомендую",
        _ => badge.ToString(),
    };

    /// <summary>Почему такой вердикт — одна строка, которую видно под карточкой.</summary>
    public static string WhyRu(GameRecommendationBadge badge) => badge switch
    {
        GameRecommendationBadge.Recommended =>
            "Рекомендуем: для игр это работает и не вредит",
        GameRecommendationBadge.Optional =>
            "По желанию: зависит от вашего железа и роутера",
        GameRecommendationBadge.Caution =>
            "Осторожно: есть побочный эффект — читайте причину в блоке «Зачем для игры»",
        GameRecommendationBadge.NotRecommended =>
            "Не советуем: популярный совет, который вредит — причина внутри карточки",
        _ => "Вердикта нет: смотрите описание",
    };

    /// <summary>
    /// Включать ли твик в план по умолчанию. Да — только рекомендованным: «по желанию» оставляем
    /// на решение человека, а «осторожно» и «не советуем» по умолчанию выключены всегда.
    /// </summary>
    public static bool EnabledByDefault(GameRecommendationBadge badge) => badge == GameRecommendationBadge.Recommended;

    /// <summary>Пометка для интерфейса: почему твик выключен по умолчанию.</summary>
    public static string DefaultStateRu(GameRecommendationBadge badge) => badge switch
    {
        GameRecommendationBadge.Recommended => "включён в план по умолчанию",
        GameRecommendationBadge.Optional => "по умолчанию выключен: решаете вы",
        GameRecommendationBadge.Caution => "по умолчанию выключен: сначала прочитайте причину",
        GameRecommendationBadge.NotRecommended => "по умолчанию выключен: включать не советуем",
        _ => "состояние по умолчанию не задано",
    };

    /// <summary>Все вердикты каталога — для легенды и справки в интерфейсе.</summary>
    public static IReadOnlyList<(GameRecommendationBadge Badge, string ShortRu, string WhyRu)> Legend { get; } = new[]
    {
        (GameRecommendationBadge.Recommended, ShortRu(GameRecommendationBadge.Recommended),
            WhyRu(GameRecommendationBadge.Recommended)),
        (GameRecommendationBadge.Optional, ShortRu(GameRecommendationBadge.Optional),
            WhyRu(GameRecommendationBadge.Optional)),
        (GameRecommendationBadge.Caution, ShortRu(GameRecommendationBadge.Caution),
            WhyRu(GameRecommendationBadge.Caution)),
        (GameRecommendationBadge.NotRecommended, ShortRu(GameRecommendationBadge.NotRecommended),
            WhyRu(GameRecommendationBadge.NotRecommended)),
    };
}
