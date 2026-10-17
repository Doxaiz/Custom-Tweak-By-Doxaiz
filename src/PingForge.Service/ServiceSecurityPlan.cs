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
/// Каналов два, и это принципиально (C2):
///   • <b>основной</b> — LocalSystem и Администраторы. Он выполняет команды, меняющие Windows, поэтому
///     интерактивный пользователь сюда не подключается вовсе: раньше он подключался на чтение и запись,
///     и любой локальный пользователь мог дёргать кнопки изменений;
///   • <b>канал только для чтения</b> — плюс интерактивный вход, но только на чтение. Команд в нём
///     ровно просмотр (<see cref="Protocol.EngineCommandCatalog.ReadOnlyChannelWireNames"/>), и меняющие
///     команды он не выполняет даже для администратора.
///
/// Плана три уровня, и все обязательны:
///   1) права самого канала — эти таблицы: кто вообще может подключиться;
///   2) набор команд канала: канал чтения не выполняет ничего, что меняет систему;
///   3) проверка прав КЛИЕНТА внутри команды (<see cref="EngineDispatcher"/>): служба идёт от LocalSystem,
///      поэтому её собственные права есть всегда и никого не защищают. Без этого уровня доступ к основному
///      каналу означал бы доступ к настройкам Windows — то есть повышение привилегий для любого
///      пользователя компьютера.
///
/// Доступ к каналу не заменяет прав администратора на изменение сети и реестра.
/// </summary>
public static class ServiceSecurityPlan
{
    public const string LocalSystemSid = "S-1-5-18";

    public const string AdministratorsSid = "S-1-5-32-544";

    public const string InteractiveSid = "S-1-5-4";

    /// <summary>Сколько подключений держит каждый канал: окно, значок в трее, служебные проверки и запас.</summary>
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

    /// <summary>
    /// Права основного канала — тот, что выполняет изменения. Интерактивного пользователя здесь НЕТ
    /// и быть не может: канал, который меняет настройки Windows, доступен только службе и администраторам.
    /// </summary>
    public static IReadOnlyList<PipeAccessGrant> Grants { get; } =
    [
        new(
            "Служба (LocalSystem)",
            LocalSystemSid,
            PipeAccessRight.FullControl,
            "Самой службе нужен полный доступ: она создаёт оба канала и перезапускает их после обновления."),

        new(
            "Администраторы",
            AdministratorsSid,
            PipeAccessRight.FullControl,
            "Установка, починка сети и обслуживание выполняются от администратора. Подключение к каналу " +
            "не отменяет проверку прав: меняющие команды выполняются только для клиента с правами " +
            "администратора в его собственном процессе."),
    ];

    /// <summary>
    /// Права канала только для чтения. Сюда добавлен интерактивный вход — но только на чтение:
    /// обычное окно программы работает без прав и должно видеть состояние, журнал и кнопку «убрать
    /// свои изменения», не зывая администратора. Меняющих команд в этом канале нет вовсе (C2),
    /// поэтому права на запись здесь не нужны даже в принципе.
    /// </summary>
    public static IReadOnlyList<PipeAccessGrant> ReadOnlyGrants { get; } =
    [
        new(
            "Служба (LocalSystem)",
            LocalSystemSid,
            PipeAccessRight.FullControl,
            "Служба создаёт и обслуживает канал."),

        new(
            "Администраторы",
            AdministratorsSid,
            PipeAccessRight.ReadWrite,
            "Администратору доступен просмотр через любой канал; изменения он делает через основной."),

        new(
            "Текущий пользователь (интерактивный вход)",
            InteractiveSid,
            PipeAccessRight.ReadOnly,
            "Окно и значок в трее работают от обычного пользователя: смотреть состояние, читать журнал и " +
            "останавливать свои изменения можно без прав. Меняющих команд в канале чтения нет вовсе, " +
            "а в основном канале служба проверяет права того, кто подключился, в его собственном процессе " +
            "(а не свои — свои у неё от LocalSystem есть всегда), и отказ пишется в журнал с именем клиента."),
    ];

    /// <summary>Разрешён ли канал для указанного SID по таблице основного канала.</summary>
    public static bool IsGranted(string sid) => IsGranted(Grants, sid);

    /// <summary>Разрешён ли канал для указанного SID по своей таблице.</summary>
    public static bool IsGranted(IReadOnlyList<PipeAccessGrant> grants, string sid) =>
        grants.Any(grant => string.Equals(grant.Sid, sid, StringComparison.Ordinal));

    /// <summary>
    /// Есть ли в действующих таблицах запрещённый идентификатор (это ошибка сборки правил).
    /// Проверяются оба канала: запрет один на всё.
    /// </summary>
    public static bool HasForbiddenIdentity() => HasForbiddenIdentity(Grants) || HasForbiddenIdentity(ReadOnlyGrants);

    /// <summary>
    /// Есть ли в таблице запрещённый идентификатор. Отдельная проверка своей таблицы нужна тесту:
    /// по действующей таблице ответ всегда «нет», и без своей таблицы сторож нельзя было бы проверить.
    /// </summary>
    public static bool HasForbiddenIdentity(IReadOnlyList<PipeAccessGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return grants.Any(grant => ForbiddenSids.Contains(grant.Sid));
    }

    /// <summary>Описание правил обычным текстом: идёт в журнал службы и в интерфейс.</summary>
    public static string DescribeRu()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Основной канал: {Protocol.EngineProtocol.PipeName} — команды, меняющие Windows.");
        builder.AppendLine($"Канал только для чтения: {Protocol.EngineProtocol.ReadOnlyPipeName} — " +
            $"{Protocol.EngineCommandCatalog.ReadOnlyChannelWireNamesRu()}.");
        builder.AppendLine($"Одновременных подключений на канал: {MaxConnections}.");
        builder.AppendLine("Кому разрешено и почему:");
        foreach (PipeAccessGrant grant in Grants)
        {
            builder.AppendLine($"  • основной — {grant.WhoRu} ({grant.Sid}) — {RightRu(grant.Right)}: {grant.WhyRu}");
        }

        foreach (PipeAccessGrant grant in ReadOnlyGrants)
        {
            builder.AppendLine($"  • чтение — {grant.WhoRu} ({grant.Sid}) — {RightRu(grant.Right)}: {grant.WhyRu}");
        }

        builder.AppendLine("Кому не разрешено никогда: Everyone, ANONYMOUS LOGON, Guests, вход по сети — " +
            "канал, который меняет настройки Windows, не открывается посторонним.");
        builder.AppendLine("Второй уровень защиты: подключение к каналу не даёт права менять систему. " +
            "Меняющие команды выполняются только для клиента с правами администратора в его собственном процессе; " +
            "отказ пишется в журнал службы вместе с именем и SID клиента. Канал чтения не выполняет меняющих " +
            "команд даже для администратора — изменения идут только через основной канал.");
        return builder.ToString().TrimEnd();
    }

    public static string RightRu(PipeAccessRight right) => right switch
    {
        PipeAccessRight.FullControl => "полный доступ",
        PipeAccessRight.ReadWrite => "чтение и команды",
        _ => "только чтение",
    };
}
