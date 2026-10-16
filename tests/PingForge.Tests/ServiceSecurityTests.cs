using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Права на канал службы. Проверка короткая, но важная: правило вида «добавим Everyone,
/// чтобы точно заработало» открывает доступ к настройкам Windows кому угодно в системе.
/// </summary>
public class ServiceSecurityTests
{
    [Fact]
    public void Служба_и_администраторы_имеют_полный_доступ()
    {
        PipeAccessGrant system = Assert.Single(ServiceSecurityPlan.Grants, g => g.Sid == ServiceSecurityPlan.LocalSystemSid);
        PipeAccessGrant admins = Assert.Single(ServiceSecurityPlan.Grants, g => g.Sid == ServiceSecurityPlan.AdministratorsSid);

        Assert.Equal(PipeAccessRight.FullControl, system.Right);
        Assert.Equal(PipeAccessRight.FullControl, admins.Right);
    }

    [Fact]
    public void В_правилах_нет_доступа_для_посторонних()
    {
        Assert.False(ServiceSecurityPlan.HasForbiddenIdentity());

        Assert.All(ServiceSecurityPlan.ForbiddenSids, sid =>
            Assert.False(ServiceSecurityPlan.IsGranted(sid), $"в правилах оказался запрещённый SID {sid}"));

        // Отдельно: доступ «всем» и «анонимному входу» не выдаётся даже на чтение.
        Assert.DoesNotContain(ServiceSecurityPlan.Grants, g => g.Right == PipeAccessRight.ReadOnly);
    }

    [Fact]
    public void У_каждого_правила_есть_имя_причина_и_разумные_права()
    {
        Assert.All(ServiceSecurityPlan.Grants, grant =>
        {
            Assert.False(string.IsNullOrWhiteSpace(grant.WhoRu));
            Assert.False(string.IsNullOrWhiteSpace(grant.WhyRu));
            Assert.Matches("^S-1-[0-9-]+$", grant.Sid);
            Assert.False(string.IsNullOrWhiteSpace(ServiceSecurityPlan.RightRu(grant.Right)));
        });
    }

    [Fact]
    public void Обычному_пользователю_не_дают_полный_доступ()
    {
        PipeAccessGrant user = Assert.Single(ServiceSecurityPlan.Grants, g => g.Sid == ServiceSecurityPlan.InteractiveSid);

        Assert.Equal(PipeAccessRight.ReadWrite, user.Right);
        Assert.Contains("администратора", user.WhyRu + ServiceSecurityPlan.DescribeRu());
    }

    [Fact]
    public void Описание_правил_называет_канал_предел_подключений_и_запреты()
    {
        string text = ServiceSecurityPlan.DescribeRu();

        Assert.Contains(EngineProtocol.PipeName, text);
        Assert.Contains(ServiceSecurityPlan.MaxConnections.ToString(), text);
        Assert.Contains("Everyone", text);
        Assert.Contains("ANONYMOUS LOGON", text);
        Assert.Contains("по сети", text);
    }

    [Fact]
    public void Предел_подключений_рассчитан_на_окно_трей_и_запас()
    {
        Assert.InRange(ServiceSecurityPlan.MaxConnections, 3, 8);
    }
}
