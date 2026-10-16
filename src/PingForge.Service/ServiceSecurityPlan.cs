namespace PingForge.Service;

/// <summary>Права на канал связи со службой.</summary>
public enum PipeAccessRight
{
    /// <summary>Полный доступ: читать, писать, менять права.</summary>
    FullControl,

    /// <summary>Читать и писать: можно звать команды, но нельзя перенастраивать сам канал.</summary>
    ReadWrite,

    /// <summary>Только читать: смотреть состояние, ничего не менять.</summary>
    ReadOnly,
}

/// <summary>Кому и с какой причиной разрешён доступ к каналу службы.</summary>
public sealed record PipeAccessGrant(string WhoRu, string Sid, PipeAccessRight Right, string WhyRu);

/// <summary>
/// Кто имеет право разговаривать со службой.
///
/// Правило простое и жёсткое: канал, который умеет менять настройки Windows, не открывается «всем».
/// Плана два уровня — сам канал (эта таблица) и проверка прав внутри команды: доступ к каналу
/// не заменяет прав администратора на изменение сети и реестра.
/// </summary>
public static class ServiceSecurityPlan
{
    public const string LocalSystemSid = "S-1-5-18";

    public const string AdministratorsSid = "S-1-5-32-544";

    public const string InteractiveSid = "S-1-5-4";

    /// <summary>Сколько каналов служба держит одновременно: окно, значок в трее, служебные проверки и запас.</summary>
    public const int MaxConnections = 4;

    /// <summary>
    /// Идентификаторы, которым канал не выдаётся никогда. Список нужен тесту: он ловит ситуацию,
    /// когда «чтобы быстрее заработало» в правило добавляют Everyone или ANONYMOUS LOGON.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenSids { get; } =
    [
        "S-1-1-0",      // Everyone
        "S-1-5-7",      // ANONYMOUS LOGON
        "S-1-5-32-546", // Guests
        "S-1-5-2",      // Network (вход по сети)
    ];

    public static IReadOnlyList<PipeAccessGrant> Grants { get; } =
    [
        new(
            "Служба (LocalSystem)",
            LocalSystemSid,
            PipeAccessRight.FullControl,
            "Самой службе нужен полный доступ: она создаёт канал и перезапускает его после обновления."),

        new(
            "Администраторы",
            AdministratorsSid,
            PipeAccessRight.FullControl,
            "Установка, починка сети и обслуживание выполняются от администратора."),

        new(
            "Текущий пользователь (интерактивный вход)",
            InteractiveSid,
            PipeAccessRight.ReadWrite,
            "Окно и значок в трее работают от обычного пользователя: смотреть состояние, читать журнал и " +
            "останавливать свои изменения можно без прав. Применение твиков всё равно проверяется отдельно."),
    ];

    /// <summary>Разрешён ли канал для указанного SID по этой таблице.</summary>
    public static bool IsGranted(string sid) =>
        Grants.Any(grant => string.Equals(grant.Sid, sid, StringComparison.Ordinal));

    /// <summary>Есть ли в таблице запрещённый идентификатор (это ошибка сборки правил).</summary>
    public static bool HasForbiddenIdentity() => Grants.Any(grant => ForbiddenSids.Contains(grant.Sid));

    /// <summary>Описание правил обычным текстом: идёт в журнал службы и в интерфейс.</summary>
    public static string DescribeRu()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Канал: {Protocol.EngineProtocol.PipeName}, одновременных подключений: {MaxConnections}.");
        builder.AppendLine("Кому разрешено и почему:");
        foreach (PipeAccessGrant grant in Grants)
        {
            builder.AppendLine($"  • {grant.WhoRu} ({grant.Sid}) — {RightRu(grant.Right)}: {grant.WhyRu}");
        }

        builder.AppendLine("Кому не разрешено никогда: Everyone, ANONYMOUS LOGON, Guests, вход по сети — " +
            "канал, который меняет настройки Windows, не открывается посторонним.");
        return builder.ToString().TrimEnd();
    }

    public static string RightRu(PipeAccessRight right) => right switch
    {
        PipeAccessRight.FullControl => "полный доступ",
        PipeAccessRight.ReadWrite => "чтение и команды",
        _ => "только чтение",
    };
}
