using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сторож инспектора клиента канала (C7).
///
/// Две ошибки жили в одном месте и были невидимы поведенческим тестам: сервер спрашивал PID
/// самого себя вместо клиента, а токен пытался открыть правом на процесс вместо права на токен.
/// На Linux Win32 не вызвать, поэтому здесь — текстовый сторож по образцу теста манифеста:
/// файл инспектора копируется в вывод теста (см. PingForge.Tests.csproj) и проверяется дословно.
/// Живая половина проверки — на раннере: <c>--expect-identity</c> в PingForge.EngineSmoke.
/// </summary>
public sealed class PipeClientInspectorGuardTests
{
    private static string InspectorText()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-PipeClientInspector.cs");
        Assert.True(File.Exists(path), $"нет файла для проверки: {path} (он копируется в вывод теста из PingForge.Tests.csproj)");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Инспектор_спрашивает_процесс_клиента_а_не_самой_службы()
    {
        string text = InspectorText();

        // Сервер обязан спрашивать процесс КЛИЕНТА: обратная функция вернула бы PID службы,
        // и «проверка прав клиента» проверяла бы LocalSystem самой службы — всегда «да».
        Assert.Contains("GetNamedPipeClientProcessId", text);
        Assert.DoesNotContain("GetNamedPipeServerProcessId", text);
    }

    [Fact]
    public void Токен_клиента_открывается_правом_на_токен()
    {
        string text = InspectorText();

        // TOKEN_QUERY = 0x0008 — право спросить сведения о токене. 0x1000 — это право на
        // ПРОЦЕСС (PROCESS_QUERY_LIMITED_INFORMATION); в маску прав токена оно не входит,
        // и с ним OpenProcessToken падает с ERROR_ACCESS_DENIED у любого клиента.
        Assert.Contains("const uint TokenQuery = 0x0008", text);
        Assert.DoesNotContain("TokenQueryLimited", text);
    }

    [Fact]
    public void Право_на_токен_действительно_передаётся_в_открытие_токена()
    {
        // Одна константа в файле мало что значит, если вызов по-прежнему просит другое право:
        // сторож смотрит и в объявление, и в сам вызов OpenProcessToken.
        string text = InspectorText();

        Assert.Contains("OpenProcessToken(process.DangerousGetHandle(), TokenQuery,", text);
    }
}
