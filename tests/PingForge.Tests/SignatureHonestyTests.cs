using System.Text.RegularExpressions;
using PingForge.App.ViewModels;

namespace PingForge.Tests;

/// <summary>
/// Подпись, SmartScreen и Smart App Control.
///
/// Волна 48: программа обещала, что «SmartScreen предупреждает, потому что установщик не подписан
/// сертификатом EV» — и этим намекала, что с EV предупреждения не будет. По документации Microsoft это
/// неправда с 2024 года: тип сертификата на поведение SmartScreen больше не влияет, доверие копится
/// загрузками. Сторожа ниже держат тексты честными и следят, чтобы состояние подписи читалось у файла,
/// а не пересказывалось словами.
/// </summary>
public sealed class SignatureHonestyTests
{
    private static string ReadChecked(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
        Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Неподписанный_файл_описан_честно()
    {
        string text = AppSignature.DescribeRu(AppSignatureState.Unsigned());

        // Что произойдёт — словами самой Windows.
        Assert.Contains("Windows protected your PC", text, StringComparison.Ordinal);
        Assert.Contains("Выполнить в любом случае", text, StringComparison.Ordinal);

        // Главное: никакого «подпишитесь, и предупреждение исчезнет».
        Assert.Contains("EV", text, StringComparison.Ordinal);
        Assert.Contains("не влияет", text, StringComparison.Ordinal);
        Assert.Contains("2024", text, StringComparison.Ordinal);

        // И честное предупреждение про Windows 11.
        Assert.Contains("Smart App Control", text, StringComparison.Ordinal);

        // Обхода защиты программа не советует: ни «отключите», ни «обойдите».
        Assert.DoesNotContain("отключите", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("обойти защиту можно", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Подписанный_файл_не_обещает_отсутствия_предупреждения()
    {
        string text = AppSignature.DescribeRu(AppSignatureState.Signed("CN=Пример, O=Пример"));

        Assert.Contains("CN=Пример", text, StringComparison.Ordinal);
        // Подпись даёт имя издателя и неизменность файла — и всё.
        Assert.Contains("не изменён", text, StringComparison.Ordinal);
        Assert.Contains("не отменяет предупреждение", text, StringComparison.Ordinal);
        Assert.DoesNotContain("без предупрежден", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("доверен", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Непрочитанная_подпись_отличается_от_отсутствующей()
    {
        string unknown = AppSignature.DescribeRu(AppSignatureState.Unreadable("нет доступа"));
        Assert.Contains("не удалось", unknown, StringComparison.Ordinal);
        Assert.Contains("нет доступа", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("не подписан сертификатом. Поэтому", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void Подпись_читается_у_реального_файла()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Читать подпись PE умеет только Windows; на других системах честный ответ — «не прочитано».
            AppSignatureState other = AppSignature.Read(typeof(AppSignature).Assembly.Location);
            Assert.False(other.IsSigned);
            Assert.NotNull(other.ProblemRu);
            return;
        }

        // Файл тестовой сборки не подписан: состояние должно быть «не подписан», а не «ошибка».
        AppSignatureState state = AppSignature.Read(typeof(AppSignature).Assembly.Location);

        Assert.False(state.IsSigned);
        Assert.Null(state.ProblemRu);
    }

    [Fact]
    public void Нечитаемый_путь_называется_проблемой_а_не_подписью()
    {
        AppSignatureState state = AppSignature.Read(Path.Combine(Path.GetTempPath(), "нет-такого-файла-" + Guid.NewGuid().ToString("N")));

        Assert.False(state.IsSigned);
        Assert.NotNull(state.ProblemRu);
    }

    [Fact]
    public void Раздел_о_программе_берёт_подпись_из_файла_а_не_из_слов()
    {
        string about = ReadChecked("AboutView.xaml");

        // Текст читается у файла: привязка к свойству вместо зашитой фразы про EV.
        Assert.Contains("{Binding SignatureNoteRu}", about, StringComparison.Ordinal);
        Assert.DoesNotContain("Сертификатом EV", about, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("подписан сертификатом EV", about, StringComparison.OrdinalIgnoreCase);

        string model = ReadChecked("MainViewModel.cs");
        Assert.Contains("AppSignature.Read", model, StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessPath", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Сторож_нет_обещания_что_EV_убирает_SmartScreen()
    {
        // Ни в одном тексте, который видит человек, не должно быть старого обещания.
        string readme = ReadChecked("README.md");
        string install = ReadChecked("install-doc.md");
        string about = ReadChecked("AboutView.xaml");
        string forbidden = ReadChecked("ForbiddenActionsList.cs");

        foreach ((string name, string text) in new[]
        {
            ("README.md", readme),
            ("docs/УСТАНОВКА.md", install),
            ("AboutView.xaml", about),
            ("ForbiddenActionsList.cs", forbidden),
        })
        {
            Assert.DoesNotContain("не подписан сертификатом EV", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("не подписан EV", text, StringComparison.OrdinalIgnoreCase);
        }

        // А слово «EV» встречается только в объяснении, что оно не помогает.
        Assert.Contains("EV", readme, StringComparison.Ordinal);
        Assert.Contains("не влияет", readme, StringComparison.Ordinal);
        Assert.Contains("Smart App Control", readme, StringComparison.Ordinal);
        Assert.Contains("Smart App Control", install, StringComparison.Ordinal);
        Assert.Contains("CodeIntegrity", install, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", install, StringComparison.Ordinal);
    }

    [Fact]
    public void Сторож_самопроверка_пишет_строку_о_подписи_и_CI_её_требует()
    {
        string selfCheck = ReadChecked("SelfCheck.cs");
        Assert.Contains("ПОДПИСЬ:", selfCheck, StringComparison.Ordinal);
        Assert.Contains("AppSignature.Read", selfCheck, StringComparison.Ordinal);

        string workflow = ReadChecked("build.yml");
        Assert.Contains("ПОДПИСЬ:", workflow, StringComparison.Ordinal);
        Assert.Matches(new Regex("Строки «ПОДПИСЬ:» нет в журнале"), workflow);
    }

    [Fact]
    public void Сторож_реестр_источников_называет_документацию_Microsoft()
    {
        string sources = ReadChecked("sources-doc.md");

        Assert.Contains("smartscreen-reputation", sources, StringComparison.Ordinal);
        Assert.Contains("code-signing-options", sources, StringComparison.Ordinal);
        Assert.Contains("smart-app-control", sources, StringComparison.Ordinal);
        // Ссылка должна вести на learn.microsoft.com, а не на пересказ блога.
        Assert.Contains("https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("http://learn.microsoft.com", sources, StringComparison.Ordinal);
    }

    [Fact]
    public void Сторож_текст_программы_не_учит_обходить_защиту()
    {
        string text = AppSignature.DescribeRu(AppSignatureState.Unsigned()) + AppSignature.DescribeRu(AppSignatureState.Signed("CN=X"));

        foreach (string advice in new[] { "отключите Smart App Control", "обойти SmartScreen", "обход защиты", "DisableSmartScreen" })
        {
            Assert.DoesNotContain(advice, text, StringComparison.OrdinalIgnoreCase);
        }

        // Зато честная инструкция есть.
        Assert.Contains("SHA256SUMS", text, StringComparison.Ordinal);
    }
}
