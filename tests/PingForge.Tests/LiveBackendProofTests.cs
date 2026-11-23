using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сторожа живых проверок бэкендов: netsh и свойства сетевого адаптера.
///
/// Смысл: «проверено на живой Windows» — это утверждение, а не украшение. Оно держится на трёх местах:
/// шаг самопроверки читает свойство NIC на настоящей машине, сборка кладёт эту строку в аннотацию,
/// а C7-проверка применяет netsh-твик и смотрит на результат своим netsh. Пропадёт любое из трёх —
/// падает тест, а не тихо исчезает доказательство.
/// </summary>
public sealed class LiveBackendProofTests
{
    [Fact]
    public void Самопроверка_читает_свойство_адаптера_и_не_меняет_его()
    {
        string selfCheck = Read("checked-SelfCheck.cs");

        Assert.Contains("ЖИВОЕ СВОЙСТВО NIC", selfCheck, StringComparison.Ordinal);
        Assert.Contains("NIC_INTERRUPT_MODERATION", selfCheck, StringComparison.Ordinal);
        Assert.Contains("только чтение — программа его не меняла", selfCheck, StringComparison.Ordinal);
    }

    [Fact]
    public void Сборка_кладёт_чтение_свойств_NIC_в_аннотацию()
    {
        string workflow = Read("checked-build.yml");

        Assert.Contains("ЖИВОЕ СВОЙСТВО NIC", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void C7_проверка_применяет_netsh_твик_и_возвращает_значение()
    {
        string script = Read("checked-c7-live-checks.ps1");

        Assert.Contains("--tweaks=TCP_AUTOTUNING_NORMAL", script, StringComparison.Ordinal);
        Assert.Contains("autotuninglevel", script, StringComparison.Ordinal);
        // Значение перед опытом меняется вручную: иначе твик был бы «уже таким» и запись не доказана.
        Assert.Contains("autotuninglevel=$prepared", script, StringComparison.Ordinal);
        Assert.Contains("не удалось вернуть исходный autotuninglevel", script, StringComparison.Ordinal);

        // Аннотаций в прогоне не больше пятидесяти, поэтому доказательства обязаны попасть в файл,
        // который сборка и так кладёт в артефакты. Путь в build.yml без этого файла — обещание пустоты.
        Assert.Contains("c7-live-checks.log", script, StringComparison.Ordinal);
    }

    private static string Read(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.True(File.Exists(path), $"нет файла {fileName} рядом с тестами (см. PingForge.Tests.csproj)");
        return File.ReadAllText(path);
    }
}
