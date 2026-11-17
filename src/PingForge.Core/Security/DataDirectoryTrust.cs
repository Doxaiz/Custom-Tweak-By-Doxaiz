namespace PingForge.Core.Security;

/// <summary>
/// Одна запись в правах доступа к папке или файлу — уже переведённая в понятия «кто» и «может писать»,
/// без Windows-типов. Так правило проверки остаётся чистой функцией и проверяется тестами.
/// </summary>
/// <param name="IdentityRu">Кто это по-русски: «Администраторы», «Пользователи», имя учётной записи.</param>
/// <param name="Sid">Идентификатор безопасности: по нему и решается, «широкий» это доступ или нет.</param>
/// <param name="IsDeny">Правило запрещает, а не разрешает. Запрет никому доступа не даёт.</param>
/// <param name="CanWrite">Можно ли по этому правилу менять содержимое (запись, создание, удаление).</param>
public sealed record DataAclEntry(string IdentityRu, string Sid, bool IsDeny, bool CanWrite);

/// <summary>Чем закончилась проверка прав на каталог данных службы.</summary>
public enum DataTrustVerdict
{
    /// <summary>Проверить не удалось: прав не прочитали или система не Windows. Не «всё хорошо».</summary>
    Unknown,

    /// <summary>Писать в каталог могут только система и администраторы. Так и должно быть.</summary>
    Trusted,

    /// <summary>Писать может любой пользователь — файлы службы можно подменить.</summary>
    WritableByUsers,

    /// <summary>Каталог принадлежит не системе и не администраторам: его создал кто-то другой.</summary>
    OwnedByUser,
}

/// <summary>Итог проверки: вердикт и готовый текст для журнала службы.</summary>
public sealed record DataTrustReport(DataTrustVerdict Verdict, string MessageRu)
{
    public bool IsTrusted => Verdict == DataTrustVerdict.Trusted;

    /// <summary>Нужно ли говорить об этом человеку (не «всё хорошо», но и не «работать нельзя»).</summary>
    public bool NeedsAttention => Verdict is DataTrustVerdict.WritableByUsers or DataTrustVerdict.OwnedByUser;
}

/// <summary>
/// Правило «можно ли доверять каталогу данных службы».
///
/// Зачем это вообще нужно. Служба работает от имени системы и кладёт в общий каталог базу со
/// снапшотами. Откат снапшота пишет прежние значения в реестр **от имени системы**. Значит тот, кто
/// может подменить файл базы, задаёт значения, которые система потом применит, — это готовое
/// повышение прав из обычного пользователя. Каталог создаётся первым запуском, и права на него
/// наследуются от <c>%ProgramData%</c>; если его успел создать не тот, кто должен, доверять нельзя.
///
/// Правило нарочно не меняет права само: молча переписать ACL чужого каталога — это способ сломать
/// установку, а не защита. Здесь только честный ответ: «доверяю», «писать может любой» или
/// «каталог создан не администратором» — и текст, который служба пишет в журнал.
/// </summary>
public static class DataDirectoryTrust
{
    /// <summary>Локальная система.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>Встроенная группа администраторов.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// Идентификаторы, которым нельзя писать в каталог службы. Не «список плохих», а именно те,
    /// чей доступ означает «это может сделать любой, кто сидит за компьютером».
    /// </summary>
    private static readonly Dictionary<string, string> BroadSids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-1-0"] = "Все (Everyone)",
        ["S-1-5-32-545"] = "Пользователи",
        ["S-1-5-11"] = "Прошедшие проверку",
        ["S-1-5-32-546"] = "Гости",
        ["S-1-5-7"] = "Анонимный вход",
        ["S-1-5-4"] = "Интерактивный вход",
    };

    /// <summary>Владелец, которому можно доверять: система или администраторы.</summary>
    public static bool IsTrustedOwner(string? ownerSid) =>
        string.Equals(ownerSid, SystemSid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ownerSid, AdministratorsSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Оценить каталог: <paramref name="ownerSid"/> — владелец, <paramref name="entries"/> — его права.
    /// Пустой список прав при известном владельце — это «не прочитали», а не «ничего не разрешено».
    /// </summary>
    public static DataTrustReport Evaluate(string whatRu, string? ownerSid, IReadOnlyList<DataAclEntry>? entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(whatRu);

        if (entries is null || entries.Count == 0)
        {
            return new DataTrustReport(
                DataTrustVerdict.Unknown,
                $"{whatRu}: права прочитать не удалось, поэтому проверить, кто может менять файлы службы, нельзя. " +
                "Это не «всё хорошо», а «неизвестно»: при разборе жалобы проверьте права вручную (icacls).");
        }

        // Владелец проверяется первым: владелец каталога может вернуть себе права в любой момент,
        // поэтому «владелец — обычный пользователь» хуже, чем просто широкая запись в правах.
        if (!string.IsNullOrWhiteSpace(ownerSid) && !IsTrustedOwner(ownerSid))
        {
            string ownerRu = entries
                .Select(entry => entry.Sid)
                .Any(sid => string.Equals(sid, ownerSid, StringComparison.OrdinalIgnoreCase))
                ? entries.First(entry => string.Equals(entry.Sid, ownerSid, StringComparison.OrdinalIgnoreCase)).IdentityRu
                : ownerSid;

            return new DataTrustReport(
                DataTrustVerdict.OwnedByUser,
                $"{whatRu} принадлежит не системе и не администраторам, а «{ownerRu}». Это значит, что каталог " +
                "создал не установщик: владелец может менять в нём файлы, а откат снапшота применяет значения " +
                "из базы от имени системы. Пересоздайте каталог установщиком от имени администратора.");
        }

        List<DataAclEntry> broadWriters = entries
            .Where(entry => !entry.IsDeny && entry.CanWrite)
            .Where(entry => BroadSids.ContainsKey(entry.Sid))
            .ToList();

        if (broadWriters.Count > 0)
        {
            string whoRu = string.Join(", ", broadWriters.Select(entry => entry.IdentityRu).Distinct(StringComparer.OrdinalIgnoreCase));
            return new DataTrustReport(
                DataTrustVerdict.WritableByUsers,
                $"{whatRu}: писать в него могут не только администраторы — {whoRu}. Значит файл базы службы может " +
                "подменить любой пользователь компьютера, а откат снапшота применяет значения из базы от имени " +
                "системы. Оставьте запись только системе и администраторам (icacls) и перезапустите службу.");
        }

        return new DataTrustReport(DataTrustVerdict.Trusted, $"{whatRu}: менять файлы могут только система и администраторы.");
    }
}
