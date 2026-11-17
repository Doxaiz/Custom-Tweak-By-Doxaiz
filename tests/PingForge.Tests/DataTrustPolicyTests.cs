using PingForge.Core.Security;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Правило доверия каталогу данных службы. Откат снапшота пишет прежние значения в реестр от имени
/// системы, а снапшоты лежат в базе в этом каталоге: кто может подменить файл базы, тот задаёт, что
/// система применит. Поэтому «доверяю» здесь — это конкретный ответ, а не отсутствие проверки.
///
/// Тесты проверяют само правило, без Windows: права приходят списком, вердикт — чистая функция.
/// </summary>
public sealed class DataTrustPolicyTests
{
    private static DataAclEntry Allow(string name, string sid, bool canWrite = true) => new(name, sid, false, canWrite);

    private static DataAclEntry Deny(string name, string sid, bool canWrite = true) => new(name, sid, true, canWrite);

    [Fact]
    public void Система_и_администраторы_это_доверие()
    {
        DataTrustReport report = DataDirectoryTrust.Evaluate(
            "Каталог данных",
            DataDirectoryTrust.SystemSid,
            [
                Allow("Система", DataDirectoryTrust.SystemSid),
                Allow("Администраторы", DataDirectoryTrust.AdministratorsSid),
                Allow("Пользователи", "S-1-5-32-545", canWrite: false),
            ]);

        Assert.Equal(DataTrustVerdict.Trusted, report.Verdict);
        Assert.True(report.IsTrusted);
        Assert.False(report.NeedsAttention);
    }

    [Fact]
    public void Запись_для_всех_это_не_доверие_и_в_тексте_есть_кто_именно()
    {
        DataTrustReport report = DataDirectoryTrust.Evaluate(
            "Каталог данных",
            DataDirectoryTrust.SystemSid,
            [
                Allow("Система", DataDirectoryTrust.SystemSid),
                Allow("Все (Everyone)", "S-1-1-0"),
            ]);

        Assert.Equal(DataTrustVerdict.WritableByUsers, report.Verdict);
        Assert.True(report.NeedsAttention);
        Assert.Contains("Все (Everyone)", report.MessageRu, StringComparison.Ordinal);
        Assert.Contains("подменить", report.MessageRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Запись_для_пользователей_тоже_не_доверие()
    {
        DataTrustReport report = DataDirectoryTrust.Evaluate(
            "Файл службы",
            DataDirectoryTrust.SystemSid,
            [
                Allow("Администраторы", DataDirectoryTrust.AdministratorsSid),
                Allow("Пользователи", "S-1-5-32-545"),
            ]);

        Assert.Equal(DataTrustVerdict.WritableByUsers, report.Verdict);
        Assert.Contains("Пользователи", report.MessageRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Запрет_для_пользователей_не_считается_разрешением()
    {
        // Встречается вживую: «Users: deny write» рядом с «Users: allow read». Это правильно,
        // и путать запрет с разрешением нельзя — иначе проверка ругалась бы на безопасную настройку.
        DataTrustReport report = DataDirectoryTrust.Evaluate(
            "Каталог данных",
            DataDirectoryTrust.SystemSid,
            [
                Allow("Система", DataDirectoryTrust.SystemSid),
                Deny("Пользователи", "S-1-5-32-545"),
                Allow("Пользователи", "S-1-5-32-545", canWrite: false),
            ]);

        Assert.Equal(DataTrustVerdict.Trusted, report.Verdict);
    }

    [Fact]
    public void Владелец_обычный_пользователь_хуже_широкой_записи()
    {
        // Владелец всегда может вернуть себе права, поэтому это отдельный и более серьёзный вердикт,
        // чем «широкая запись»: каталог просто создал не тот, кто должен.
        DataTrustReport report = DataDirectoryTrust.Evaluate(
            "Каталог данных",
            "S-1-5-21-1111111111-2222222222-3333333333-1001",
            [
                Allow("Система", DataDirectoryTrust.SystemSid),
                Allow("Все (Everyone)", "S-1-1-0"),
            ]);

        Assert.Equal(DataTrustVerdict.OwnedByUser, report.Verdict);
        Assert.Contains("владелец", report.MessageRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Каталог_администратора_с_широкой_записью_остаётся_широкой_записью()
    {
        // Владелец правильный, но права открыты: вердикт должен остаться «писать может любой».
        DataTrustReport report = DataDirectoryTrust.Evaluate(
            "Каталог данных",
            DataDirectoryTrust.AdministratorsSid,
            [
                Allow("Администраторы", DataDirectoryTrust.AdministratorsSid),
                Allow("Прошедшие проверку", "S-1-5-11"),
            ]);

        Assert.Equal(DataTrustVerdict.WritableByUsers, report.Verdict);
    }

    [Fact]
    public void Пустые_права_это_неизвестно_а_не_доверие()
    {
        // «Не смогли прочитать» никогда не должно выглядеть как «проверено и чисто».
        DataTrustReport empty = DataDirectoryTrust.Evaluate("Каталог данных", DataDirectoryTrust.SystemSid, []);
        DataTrustReport unknown = DataDirectoryTrust.Evaluate("Каталог данных", null, null);

        Assert.Equal(DataTrustVerdict.Unknown, empty.Verdict);
        Assert.Equal(DataTrustVerdict.Unknown, unknown.Verdict);
        Assert.False(empty.IsTrusted);
        Assert.False(empty.NeedsAttention);
        Assert.Contains("неизвестно", empty.MessageRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Доверенные_владельцы_только_система_и_администраторы()
    {
        Assert.True(DataDirectoryTrust.IsTrustedOwner(DataDirectoryTrust.SystemSid));
        Assert.True(DataDirectoryTrust.IsTrustedOwner(DataDirectoryTrust.AdministratorsSid));
        Assert.False(DataDirectoryTrust.IsTrustedOwner("S-1-5-32-545"));
        Assert.False(DataDirectoryTrust.IsTrustedOwner("S-1-5-21-1-2-3-1001"));
        Assert.False(DataDirectoryTrust.IsTrustedOwner(null));
        Assert.False(DataDirectoryTrust.IsTrustedOwner(""));
    }
}
