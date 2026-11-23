using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сторож ветки пользователя: порт реестра и живая проверка как текст.
///
/// Смысл: договор «настройка пользователя пишется в профиль того, кто попросил» держится на трёх
/// местах сразу — порт реестра открывает `HKEY_USERS\&lt;SID&gt;`, служба кладёт SID в контекст команды,
/// а живая проверка читает применённое значение своим процессом. Если кто-то вернёт простую запись
/// «в свой HKCU» (как было), тест упадёт, а не человек, который не найдёт настройку в Windows.
/// </summary>
public sealed class UserHiveGuardTests
{
    [Fact]
    public void Порт_реестра_берёт_профиль_пользователя_а_не_свой_HKCU()
    {
        string port = Read("checked-WindowsRegistryPort.cs");

        Assert.Contains("RegistryHive.Users", port, StringComparison.Ordinal);
        Assert.Contains("UserHiveContext.CurrentSid", port, StringComparison.Ordinal);
        Assert.Contains("UserHiveProblemRu", port, StringComparison.Ordinal);
        Assert.Contains("DescribeUserHiveProblem", port, StringComparison.Ordinal);

        // Прежняя однозначная подстановка «CurrentUser → RegistryHive.CurrentUser» и есть та ошибка.
        Assert.DoesNotContain("RegistryRootKind.CurrentUser => RegistryHive.CurrentUser", port, StringComparison.Ordinal);
    }

    [Fact]
    public void Живая_проверка_читает_применённое_значение_своим_процессом()
    {
        string script = Read("checked-c7-live-checks.ps1");

        Assert.Contains("--apply-hold", script, StringComparison.Ordinal);
        Assert.Contains("HKCU:\\Control Panel\\Desktop", script, StringComparison.Ordinal);
        Assert.Contains("MenuShowDelay", script, StringComparison.Ordinal);
        Assert.Contains("после отката", script, StringComparison.Ordinal);
    }

    private static string Read(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.True(File.Exists(path), $"нет файла {fileName} рядом с тестами (см. PingForge.Tests.csproj)");
        return File.ReadAllText(path);
    }
}
