using PingForge.Service;
using PingForge.Service.Protocol;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Права на каналы службы. Проверка короткая, но важная: правило вида «добавим Everyone,
/// чтобы точно заработало» открывает доступ к настройкам Windows кому угодно в системе,
/// а широкий основной канал — готовое повышение привилегий для любого пользователя компьютера.
///
/// Схема (C2): основной канал — LocalSystem и Администраторы (в интерактивном пользователе там нет
/// нужды: изменения идут от администратора); канал только для чтения — плюс интерактивный вход,
/// но только на чтение и только с командами просмотра.
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

        foreach (IReadOnlyList<PipeAccessGrant> table in new[] { ServiceSecurityPlan.Grants, ServiceSecurityPlan.ReadOnlyGrants })
        {
            Assert.All(ServiceSecurityPlan.ForbiddenSids, sid =>
                Assert.False(ServiceSecurityPlan.IsGranted(table, sid), $"в правилах оказался запрещённый SID {sid}"));
        }

        // Доступ «всем» и «анонимному входу» не выдаётся ни в одном канале, ни на чтение.
        Assert.DoesNotContain(ServiceSecurityPlan.Grants, g => g.Sid is "S-1-1-0" or "S-1-5-7");
        Assert.DoesNotContain(ServiceSecurityPlan.ReadOnlyGrants, g => g.Sid is "S-1-1-0" or "S-1-5-7");
    }

    [Fact]
    public void В_главном_канале_нет_места_интерактивному_пользователю()
    {
        // C2 закрыл главную дыру ACL: раньше интерактивный вход (S-1-5-4 — это ВСЕ залогиненные
        // пользователи) имел в основном канале право записи. Канал, который меняет настройки Windows,
        // не должен открываться каждому вошедшему: изменения идут от администратора, а просмотр
        // переехал в отдельный канал чтения.
        Assert.DoesNotContain(ServiceSecurityPlan.Grants, g => g.Sid == ServiceSecurityPlan.InteractiveSid);
        Assert.Equal(2, ServiceSecurityPlan.Grants.Count);
    }

    [Fact]
    public void Канал_чтения_даёт_интерактиву_байты_в_обе_стороны_а_команды_ограничивает_диспетчер()
    {
        // Живая проверка C7 на раннере (пользователь pfcheck, SID интерактивного входа на месте)
        // показала: права «только чтение» на канале не хватает даже на подключение — клиент
        // открывает соединение сразу на запрос и ответ. Поэтому право канала — чтение и запись
        // байтов, а «только чтение» — это набор команд, и его держит диспетчер (ReadOnlyChannelTests).
        PipeAccessGrant user = Assert.Single(ServiceSecurityPlan.ReadOnlyGrants, g => g.Sid == ServiceSecurityPlan.InteractiveSid);

        Assert.Equal(PipeAccessRight.ReadWrite, user.Right);

        // Основной канал при этом для интерактивного входа по-прежнему закрыт полностью.
        Assert.DoesNotContain(ServiceSecurityPlan.Grants, g => g.Sid == ServiceSecurityPlan.InteractiveSid);
    }

    [Fact]
    public void У_каждого_правила_есть_имя_причина_и_разумные_права()
    {
        foreach (IReadOnlyList<PipeAccessGrant> table in new[] { ServiceSecurityPlan.Grants, ServiceSecurityPlan.ReadOnlyGrants })
        {
            Assert.All(table, grant =>
            {
                Assert.False(string.IsNullOrWhiteSpace(grant.WhoRu));
                Assert.False(string.IsNullOrWhiteSpace(grant.WhyRu));
                Assert.Matches("^S-1-[0-9-]+$", grant.Sid);
                Assert.False(string.IsNullOrWhiteSpace(ServiceSecurityPlan.RightRu(grant.Right)));
            });
        }
    }

    [Fact]
    public void Обычному_пользователю_не_дают_полный_доступ()
    {
        // В основном канале для него нет вообще никакого правила — то есть и двери тоже нет.
        // В канале чтения — байты в обе стороны (без этого соединение не открывается вовсе,
        // доказано живым pfcheck), но не полный доступ: создавать экземпляры канала и менять
        // его права обычному пользователю нельзя, а команды ограничивает диспетчер.
        Assert.DoesNotContain(ServiceSecurityPlan.Grants, g => g.Sid == ServiceSecurityPlan.InteractiveSid);

        PipeAccessGrant user = Assert.Single(ServiceSecurityPlan.ReadOnlyGrants, g => g.Sid == ServiceSecurityPlan.InteractiveSid);
        Assert.NotEqual(PipeAccessRight.FullControl, user.Right);
        Assert.Equal(PipeAccessRight.ReadWrite, user.Right);

        Assert.Contains("администратора", user.WhyRu + ServiceSecurityPlan.DescribeRu());
    }

    [Fact]
    public void Правила_говорят_что_доступ_к_каналу_не_заменяет_прав_клиента()
    {
        // Сторож на слова и на дело: если из кода уберут проверку прав клиента, описание останется
        // обещанием. Поэтому проверяется и текст правил, и поведение разборщика (отдельный тест).
        PipeAccessGrant user = ServiceSecurityPlan.ReadOnlyGrants.Single(g => g.Sid == ServiceSecurityPlan.InteractiveSid);

        Assert.Contains("права того, кто подключился", user.WhyRu);
        Assert.Contains("Второй уровень защиты", ServiceSecurityPlan.DescribeRu());
    }

    [Fact]
    public void Описание_правил_называет_оба_канала_предел_подключений_и_запреты()
    {
        string text = ServiceSecurityPlan.DescribeRu();

        Assert.Contains(EngineProtocol.PipeName, text);
        Assert.Contains(EngineProtocol.ReadOnlyPipeName, text);
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
