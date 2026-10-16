using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PingForge.Service.Windows;

/// <summary>
/// Превращает <see cref="ServiceSecurityPlan"/> в настоящие права канала.
///
/// Две детали, из-за которых такой код обычно и ломается:
///   • наследование прав отключается (<c>SetAccessRuleProtection(protected: true)</c>) — иначе в канал
///     приезжают правила родительской папки, включая «Everyone»;
///   • вход по сети запрещается отдельным запрещающим правилом, а не «надеждой, что никто не подключится»:
///     запрет в Windows проверяется раньше разрешений.
/// </summary>
public static class PipeAclFactory
{
    /// <summary>Собрать права канала по таблице доступа.</summary>
    public static PipeSecurity Build()
    {
        var security = new PipeSecurity();

        // Свои правила вместо унаследованных: канал не должен получить «Everyone» от окружения.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(ServiceSecurityPlan.AdministratorsSid));
        security.SetGroup(new SecurityIdentifier(ServiceSecurityPlan.AdministratorsSid));

        foreach (PipeAccessGrant grant in ServiceSecurityPlan.Grants)
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

    /// <summary>Создать серверный конец канала с этими правами.</summary>
    public static NamedPipeServerStream CreateServerStream() =>
        NamedPipeServerStreamAcl.Create(
            Protocol.EngineProtocol.PipeName,
            PipeDirection.InOut,
            ServiceSecurityPlan.MaxConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: Build());

    private static PipeAccessRights ToPipeRights(PipeAccessRight right) => right switch
    {
        PipeAccessRight.FullControl => PipeAccessRights.FullControl,
        PipeAccessRight.ReadWrite => PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
        _ => PipeAccessRights.ReadData,
    };
}
