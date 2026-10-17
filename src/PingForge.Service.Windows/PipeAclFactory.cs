using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PingForge.Service.Windows;

/// <summary>
/// Превращает <see cref="ServiceSecurityPlan"/> в настоящие права канала.
///
/// Каналов два (C2): основной собирается из <see cref="ServiceSecurityPlan.Grants"/> (LocalSystem и
/// Администраторы — интерактивного пользователя там нет вовсе), канал чтения — из
/// <see cref="ServiceSecurityPlan.ReadOnlyGrants"/> (плюс интерактивный вход, но только на чтение).
///
/// Три детали, из-за которых такой код обычно и ломается:
///   • наследование прав отключается (<c>SetAccessRuleProtection(protected: true)</c>) — иначе в канал
///     приезжают правила родительской папки, включая «Everyone»;
///   • вход по сети запрещается отдельным запрещающим правилом, а не «надеждой, что никто не подключится»:
///     запрет в Windows проверяется раньше разрешений;
///   • таблица перед сборкой проверяется на запрещённые идентификаторы — и канал не открывается вовсе,
///     если кто-то добавил в неё Everyone «чтобы заработало».
/// </summary>
public static class PipeAclFactory
{
    /// <summary>Собрать права основного канала — того, что выполняет изменения.</summary>
    public static PipeSecurity Build() => BuildMain();

    /// <summary>Собрать права основного канала — того, что выполняет изменения.</summary>
    public static PipeSecurity BuildMain() => BuildFrom(ServiceSecurityPlan.Grants, "Основной канал службы");

    /// <summary>Собрать права канала только для чтения.</summary>
    public static PipeSecurity BuildReadOnly() => BuildFrom(ServiceSecurityPlan.ReadOnlyGrants, "Канал службы (только чтение)");

    private static PipeSecurity BuildFrom(IReadOnlyList<PipeAccessGrant> grants, string channelRu)
    {
        // Защитная проверка вызывается именно здесь — в единственном месте, где канал открывается.
        // Объявлена она была и раньше, но её никто не звал: добавь кто-нибудь в таблицу Everyone,
        // Guests или ANONYMOUS LOGON «чтобы точно заработало» — ни сборка, ни тесты этого бы не заметили.
        if (ServiceSecurityPlan.HasForbiddenIdentity())
        {
            string forbidden = string.Join(", ", ServiceSecurityPlan.Grants
                .Concat(ServiceSecurityPlan.ReadOnlyGrants)
                .Where(grant => ServiceSecurityPlan.ForbiddenSids.Contains(grant.Sid))
                .Select(grant => $"{grant.Sid} ({grant.WhoRu})"));
            throw new InvalidOperationException(
                "Канал службы не открыт: в таблице прав есть идентификатор, которому доступ запрещён — " +
                $"{forbidden}. Канал умеет менять настройки Windows, поэтому «открыть всем, чтобы заработало» " +
                "здесь не проходит. Уберите правило из ServiceSecurityPlan.Grants или ReadOnlyGrants.");
        }

        var security = new PipeSecurity();

        // Свои правила вместо унаследованных: канал не должен получить «Everyone» от окружения.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(ServiceSecurityPlan.AdministratorsSid));
        security.SetGroup(new SecurityIdentifier(ServiceSecurityPlan.AdministratorsSid));

        foreach (PipeAccessGrant grant in grants)
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(grant.Sid),
                ToPipeRights(grant.Right),
                AccessControlType.Allow));
        }

        // Вход по сети — запрет. Даже администратор удалённой машины не должен дёргать этот канал.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier("S-1-5-2"),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return security;
    }

    /// <summary>Создать серверный конец канала с правами своего вида.</summary>
    public static NamedPipeServerStream CreateServerStream(Protocol.EngineChannelKind channelKind = Protocol.EngineChannelKind.Main) =>
        NamedPipeServerStreamAcl.Create(
            channelKind == Protocol.EngineChannelKind.Main
                ? Protocol.EngineProtocol.PipeName
                : Protocol.EngineProtocol.ReadOnlyPipeName,
            PipeDirection.InOut,
            ServiceSecurityPlan.MaxConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: channelKind == Protocol.EngineChannelKind.Main ? BuildMain() : BuildReadOnly());

    private static PipeAccessRights ToPipeRights(PipeAccessRight right) => right switch
    {
        PipeAccessRight.FullControl => PipeAccessRights.FullControl,
        PipeAccessRight.ReadWrite => PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
        _ => PipeAccessRights.ReadData,
    };
}
