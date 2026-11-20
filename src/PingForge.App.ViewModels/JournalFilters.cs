namespace PingForge.App.ViewModels;

/// <summary>Фильтр журнала по типу изменений (задача K): пользователь выбирает, какие категории смотреть.</summary>
public enum JournalKindFilter
{
    All = 0,
    Tweaks = 1,
    Route = 2,
    Qos = 3,
    Realtime = 4,
    Rollbacks = 5,
}

public enum JournalStatusFilter
{
    All = 0,
    Success = 1,
    Failed = 2,
    RebootNeeded = 3,
}

public enum JournalDateFilter
{
    All = 0,
    Today = 1,
    Last7Days = 2,
    Last30Days = 3,
}

public static class JournalFilters
{
    public static string KindLabelRu(JournalKindFilter filter) => filter switch
    {
        JournalKindFilter.Tweaks => "твики",
        JournalKindFilter.Route => "маршрут",
        JournalKindFilter.Qos => "QoS",
        JournalKindFilter.Realtime => "живая оптимизация",
        JournalKindFilter.Rollbacks => "откаты",
        _ => "всё",
    };

    public static string StatusLabelRu(JournalStatusFilter filter) => filter switch
    {
        JournalStatusFilter.Success => "успех",
        JournalStatusFilter.Failed => "ошибка",
        JournalStatusFilter.RebootNeeded => "нужна перезагрузка",
        _ => "всё",
    };

    public static string DateLabelRu(JournalDateFilter filter) => filter switch
    {
        JournalDateFilter.Today => "сегодня",
        JournalDateFilter.Last7Days => "последние 7 дней",
        JournalDateFilter.Last30Days => "последние 30 дней",
        _ => "за всё время",
    };

    /// <summary>Подходит ли запись под фильтр «тип»: смотрим по коду события.</summary>
    public static bool MatchesKind(JournalRowViewModel row, JournalKindFilter filter)
    {
        if (filter == JournalKindFilter.All) return true;
        string code = row.TweakCode ?? string.Empty;
        string title = row.TweakTitleRu ?? string.Empty;
        // Новая схема (K) будет писать eventKind; пока полагаемся на префиксы кодов и заголовки
        return filter switch
        {
            JournalKindFilter.Tweaks => !IsRoute(code, title) && !IsQos(code, title) && !IsRealtime(code, title) && !IsRollback(code, title),
            JournalKindFilter.Route => IsRoute(code, title),
            JournalKindFilter.Qos => IsQos(code, title),
            JournalKindFilter.Realtime => IsRealtime(code, title),
            JournalKindFilter.Rollbacks => IsRollback(code, title),
            _ => true,
        };
    }

    private static bool IsRoute(string code, string title) =>
        code.StartsWith("ROUTE_", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("GAME_ROUTE_", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("маршрут", StringComparison.OrdinalIgnoreCase);

    private static bool IsQos(string code, string title) =>
        code.StartsWith("QOS_", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("QoS", StringComparison.OrdinalIgnoreCase);

    private static bool IsRealtime(string code, string title) =>
        code.StartsWith("REALTIME_", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("ROUTE_AUTO_", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("живая", StringComparison.OrdinalIgnoreCase);

    private static bool IsRollback(string code, string title) =>
        code.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("откат", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesStatus(JournalRowViewModel row, JournalStatusFilter filter) => filter switch
    {
        JournalStatusFilter.Success => row.Succeeded,
        JournalStatusFilter.Failed => !row.Succeeded,
        JournalStatusFilter.RebootNeeded => row.RebootNeeded,
        _ => true,
    };

    public static bool MatchesDate(JournalRowViewModel row, JournalDateFilter filter)
    {
        if (filter == JournalDateFilter.All) return true;
        if (row.LoggedUtc == DateTime.MinValue) return true; // без даты — показываем
        DateTime now = DateTime.UtcNow;
        TimeSpan age = now - row.LoggedUtc;
        return filter switch
        {
            JournalDateFilter.Today => age.TotalDays < 1 && row.LoggedUtc.Date == now.Date,
            JournalDateFilter.Last7Days => age.TotalDays <= 7,
            JournalDateFilter.Last30Days => age.TotalDays <= 30,
            _ => true,
        };
    }

    public static bool MatchesSearch(JournalRowViewModel row, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return (row.TweakCode?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.TweakTitleRu?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.ExplanationRu?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
