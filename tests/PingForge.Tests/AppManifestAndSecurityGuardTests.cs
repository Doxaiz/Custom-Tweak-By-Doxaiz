using PingForge.Service;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Манифест окна программы (C4) и сторож таблицы прав канала (C6).
///
/// Здесь проверяется то, что нельзя проверить поведением в песочнице без Windows, но можно — файлом:
/// объявленные права запуска окна, объявленная поддержка масштаба экрана и наличие защитной проверки
/// там, где канал действительно открывается.
///
/// Файлы для проверки копируются в вывод теста (см. PingForge.Tests.csproj): сборка идёт с
/// <c>--artifacts-path</c> вне папки решения, поэтому искать исходники обходом каталогов вверх нельзя —
/// их там просто нет.
/// </summary>
public sealed class AppManifestAndSecurityGuardTests
{
    private static string CheckedFile(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
        Assert.True(File.Exists(path), $"нет файла для проверки: {path} (он копируется в вывод теста из PingForge.Tests.csproj)");
        return File.ReadAllText(path);
    }

    [Fact]
    public void PipeClientInspector_читает_реальный_production_файл_с_клиентским_PID_и_TOKEN_QUERY()
    {
        string source = CheckedFile("PipeClientInspector.cs");
        Assert.Contains("GetNamedPipeClientProcessId", source);
        Assert.Contains("TokenQuery = 0x0008", source);
        Assert.Contains("OpenProcessToken(process.DangerousGetHandle(), TokenQuery", source);
        Assert.DoesNotContain("GetNamedPipeServerProcessId", source);
        Assert.DoesNotContain("TokenQueryLimited", source);
    }

    // ── манифест окна (C4) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Манифест_окна_подключён_к_проекту_явно()
    {
        string project = CheckedFile("PingForge.App.csproj");

        // Явно, а не «SDK сам подхватит app.manifest»: если связь потеряется, окно молча окажется
        // без объявления прав и без поддержки масштаба — и человек увидит мыльный текст при 150 %.
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project);
    }

    [Fact]
    public void Окно_не_требует_прав_при_запуске()
    {
        string manifest = CheckedFile("app.manifest");

        Assert.Contains("requestedExecutionLevel", manifest);
        Assert.Contains("level=\"asInvoker\"", manifest);
        Assert.Contains("uiAccess=\"false\"", manifest);

        // Сторож на «а давайте попросим админа, чтобы точно сработало»: при запуске программа ничего
        // не меняет, значит и права при запуске ей не нужны. Меняет настройки служба — и только для
        // клиента, у которого права есть в его собственном процессе.
        Assert.DoesNotContain("requireAdministrator", manifest);
        Assert.DoesNotContain("highestAvailable", manifest);
    }

    [Fact]
    public void Окно_объявляет_поддержку_масштаба_экрана()
    {
        string manifest = CheckedFile("app.manifest");

        Assert.Contains("dpiAwareness", manifest);
        Assert.Contains("PerMonitorV2", manifest);

        // Без этого Windows сама растягивает растровую картинку окна при 125/150 % — текст мылится.
        Assert.DoesNotContain("<dpiAware>false</dpiAware>", manifest);
        Assert.DoesNotContain("unaware", manifest);
    }

    [Fact]
    public void Длинные_пути_к_играм_объявлены()
    {
        string manifest = CheckedFile("app.manifest");

        Assert.Contains("longPathAware", manifest);
    }

    // ── сторож таблицы прав канала (C6) ─────────────────────────────────────────────────────────

    [Fact]
    public void Текущая_таблица_прав_проходит_защитную_проверку()
    {
        Assert.False(ServiceSecurityPlan.HasForbiddenIdentity());
        Assert.False(ServiceSecurityPlan.HasForbiddenIdentity(ServiceSecurityPlan.Grants));
    }

    [Fact]
    public void Таблица_с_доступом_для_посторонних_проверку_не_проходит()
    {
        // HasForbiddenIdentity существовала и раньше, но её никто не вызывал: добавь кто-нибудь
        // Everyone «чтобы точно заработало» — ни сборка, ни тесты этого бы не заметили.
        foreach (string sid in ServiceSecurityPlan.ForbiddenSids)
        {
            var grants = new[] { new PipeAccessGrant("Посторонние", sid, PipeAccessRight.ReadWrite, "проверка сторожа") };

            Assert.True(
                ServiceSecurityPlan.HasForbiddenIdentity(grants),
                $"таблица с запрещённым {sid} обязана не проходить проверку");
        }
    }

    [Fact]
    public void Защитная_проверка_вызывается_там_где_канал_открывается()
    {
        string factory = CheckedFile("PipeAclFactory.cs");

        // Единственное место, где канал получает права. Если проверку отсюда уберут, таблица запретов
        // снова станет украшением — поэтому сторож смотрит именно в этот файл.
        Assert.Contains("HasForbiddenIdentity", factory);
        Assert.Contains("InvalidOperationException", factory);
        Assert.Contains("не открыт", factory);
    }
}
