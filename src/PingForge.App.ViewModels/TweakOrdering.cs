namespace PingForge.App.ViewModels;

/// <summary>
/// Порядок карточек в разделе «Твики». Три режима отвечают на три разных вопроса человека:
/// «с чего начать», «что включить первым» и «где тот самый твик из статьи».
/// </summary>
public enum TweakOrder
{
    /// <summary>Группы по назначению: пинг и сеть, скорость, кадры, ввод, система. Внутри — по вердикту.</summary>
    ByPurpose = 0,

    /// <summary>Один список: рекомендованное сверху, «не советуем» — в конце, вне зависимости от группы.</summary>
    RecommendedFirst = 1,

    /// <summary>По названию: когда человек ищет конкретный твик, а не выбирает, с чего начать.</summary>
    Alphabetical = 2,
}

/// <summary>
/// Сортировка, группировка и поиск по списку твиков — отдельно от модели представления, чтобы
/// проверялось тестами без окна. Правила простые и намеренно скучные: никакого «умного» ранжирования
/// по популярности, только вердикт каталога, назначение и название.
/// </summary>
public static class TweakOrdering
{
    /// <summary>
    /// Подпись режима для кнопки-пилюли.
    /// </summary>
    public static string TitleRu(TweakOrder order) => order switch
    {
        TweakOrder.RecommendedFirst => "Сначала полезное",
        TweakOrder.Alphabetical => "По алфавиту",
        _ => "По назначению",
    };

    /// <summary>Честное пояснение, что именно делает режим: без него кнопка — загадка.</summary>
    public static string HintRu(TweakOrder order) => order switch
    {
        TweakOrder.RecommendedFirst =>
            "Один список: рекомендованное сверху, «по желанию» и «осторожно» ниже, «не советуем» — в самом конце.",
        TweakOrder.Alphabetical =>
            "По названию. Удобно, когда ищете конкретный твик из статьи или ролика.",
        _ =>
            "Группы по назначению — пинг и сеть, скорость, кадры, ввод, система. Внутри группы сначала рекомендованное.",
    };

    /// <summary>Совпадает ли строка с поисковым запросом: название, описание или код.</summary>
    public static bool MatchesSearch(TweakRowViewModel row, string? query)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string needle = query.Trim();
        return row.TitleRu.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || row.WhatItDoesRu.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || row.Code.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || row.FocusRu.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Порядок строк в режиме «сначала полезное»: вердикт, потом название. Код — разрешение ничьей,
    /// чтобы порядок не зависел от того, в каком порядке каталог отдал твики.
    /// </summary>
    public static IReadOnlyList<TweakRowViewModel> Sort(IEnumerable<TweakRowViewModel> rows, TweakOrder order)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return order switch
        {
            TweakOrder.Alphabetical => rows
                .OrderBy(row => row.TitleRu, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .ToList(),
            TweakOrder.RecommendedFirst => rows
                .OrderBy(row => row.VerdictRank)
                .ThenBy(row => row.TitleRu, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .ToList(),
            _ => rows
                .OrderBy(row => row.TitleRu, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>
    /// Группы для экрана. «По назначению» — группы по подкатегориям в порядке каталога;
    /// остальные режимы — одна группа с честным заголовком. Пустых групп не бывает.
    /// </summary>
    public static IReadOnlyList<TweakGroupViewModel> BuildGroups(IEnumerable<TweakRowViewModel> rows, TweakOrder order)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var list = rows.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        if (order != TweakOrder.ByPurpose)
        {
            return [new TweakGroupViewModel(TitleRu(order), Sort(list, order), HintRu(order))];
        }

        var groups = new List<TweakGroupViewModel>();
        foreach (string focus in TweakRowFactory.FocusGroupsRu)
        {
            var inGroup = list.Where(row => string.Equals(row.FocusRu, focus, StringComparison.Ordinal)).ToList();
            if (inGroup.Count > 0)
            {
                groups.Add(new TweakGroupViewModel(focus, Sort(inGroup, TweakOrder.RecommendedFirst), HintRu(TweakOrder.ByPurpose)));
            }
        }

        // Подкатегория, которой нет в списке каталога, не должна пропасть из окна: она идёт последней,
        // в том порядке, в каком встретилась. Потерять твик из-за новой подкатегории — хуже, чем
        // увидеть лишнюю группу в конце.
        var known = new HashSet<string>(TweakRowFactory.FocusGroupsRu, StringComparer.Ordinal);
        foreach (TweakRowViewModel row in list)
        {
            if (known.Contains(row.FocusRu))
            {
                continue;
            }

            if (groups.Any(group => string.Equals(group.TitleRu, row.FocusRu, StringComparison.Ordinal)))
            {
                continue;
            }

            var rest = list.Where(other => string.Equals(other.FocusRu, row.FocusRu, StringComparison.Ordinal)).ToList();
            groups.Add(new TweakGroupViewModel(row.FocusRu, Sort(rest, TweakOrder.RecommendedFirst), HintRu(TweakOrder.ByPurpose)));
        }

        return groups;
    }

    /// <summary>
    /// Русское склонение по числу: «1 твик», «2 твика», «5 твиков», «11 твиков», «21 твик».
    /// Счётчик в заголовке группы считается, поэтому подпись не может разойтись с числом карточек.
    /// </summary>
    public static string Plural(int count, string one, string few, string many)
    {
        int tail = Math.Abs(count) % 100;
        if (tail is >= 11 and <= 14)
        {
            return many;
        }

        return (tail % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Заголовок группы: число карточек и сколько из них уже отмечено.</summary>
    public static string CountRu(int total, int on)
    {
        string tweaks = Plural(total, "твик", "твика", "твиков");
        return $"{total} {tweaks} · включено {on}";
    }
}
