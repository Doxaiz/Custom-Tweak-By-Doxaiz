using PingForge.App.ViewModels;
using PingForge.Probing.Access;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// «Блокировки и доступ»: диагностика по кнопке и честность вывода.
///
/// Три вещи, которые здесь держатся на замке:
///   1. вывод различает «сайт недоступен», «порт закрыт», «DNS не дал адреса», «TLS оборван»
///      и «провайдер подменил ответ» — это разные вещи с разными следующими шагами;
///   2. обрыв TLS не выдаётся за доказанную блокировку: там два варианта и совет проверить в другой сети;
///   3. в программе нет и не обещается обход блокировок.
/// </summary>
public sealed class AccessCheckTests
{
    private static string ReadChecked(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
        Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
        return File.ReadAllText(path);
    }

    private sealed class FakeProbe : IAccessProbe
    {
        private readonly AccessObservation _observation;

        public FakeProbe(AccessObservation observation) => _observation = observation;

        public int Calls { get; private set; }

        public string? LastHost { get; private set; }

        public Task<AccessObservation> ProbeAsync(string host, CancellationToken ct = default)
        {
            Calls++;
            LastHost = host;
            return Task.FromResult(_observation with { Host = host });
        }
    }

    [Fact]
    public void Доступ_есть_когда_прошло_рукопожатие()
    {
        var observation = new AccessObservation(
            "example.com",
            DnsMilliseconds: 5,
            AddressCount: 1,
            FirstAddress: "93.184.216.34",
            ConnectMilliseconds: 20,
            TlsMilliseconds: 60);

        AccessDiagnosis diagnosis = AccessClassifier.Classify(observation);

        Assert.Equal(AccessOutcome.Ok, diagnosis.Outcome);
        Assert.Equal("Доступ есть", diagnosis.TitleRu);
        Assert.False(diagnosis.IsProblem);
        Assert.Contains("60", diagnosis.DetailRu, StringComparison.Ordinal);
        // Если сайт не открывается в браузере — виноват браузер, а не «блокировка».
        Assert.Contains("браузер", diagnosis.NextStepRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Обрыв_TLS_не_называется_доказанной_блокировкой()
    {
        var observation = new AccessObservation(
            "example.com",
            DnsMilliseconds: 5,
            AddressCount: 1,
            FirstAddress: "93.184.216.34",
            ConnectMilliseconds: 18,
            TlsFailure: "IOException: соединение сброшено (RST)");

        AccessDiagnosis diagnosis = AccessClassifier.Classify(observation);

        Assert.Equal(AccessOutcome.TlsReset, diagnosis.Outcome);
        Assert.True(diagnosis.IsProblem);

        // Два варианта названы оба, и есть честный следующий шаг.
        Assert.Contains("фильтрация", diagnosis.DetailRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сервер", diagnosis.DetailRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не различаются", diagnosis.DetailRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("другой сети", diagnosis.NextStepRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Молчание_TLS_отличается_от_обрыва()
    {
        var observation = new AccessObservation(
            "example.com",
            AddressCount: 1,
            FirstAddress: "93.184.216.34",
            ConnectMilliseconds: 20,
            TlsFailure: "таймаут TLS: сервер молчит");

        AccessDiagnosis diagnosis = AccessClassifier.Classify(observation);

        Assert.Equal(AccessOutcome.TlsTimeout, diagnosis.Outcome);
        Assert.Equal("TLS-рукопожатие молчит", diagnosis.TitleRu);
    }

    [Fact]
    public void Заглушка_провайдера_называется_заглушкой()
    {
        var observation = new AccessObservation(
            "example.com",
            AddressCount: 1,
            FirstAddress: "10.0.0.1",
            ConnectMilliseconds: 12,
            TlsFailure: "IOException: соединение сброшено (RST)",
            HttpStatus: 451,
            HttpLocation: "http://zapret-info.example/blocked",
            HttpLooksLikeStub: true);

        AccessDiagnosis diagnosis = AccessClassifier.Classify(observation);

        Assert.Equal(AccessOutcome.HttpStub, diagnosis.Outcome);
        Assert.Contains("451", diagnosis.DetailRu, StringComparison.Ordinal);
        // Главное: здесь сказано, что обхода в программе нет, а не обещано «сейчас откроем».
        Assert.Contains("не делает", diagnosis.NextStepRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Отказ_порта_и_таймаут_описаны_по_разному()
    {
        var refused = new AccessObservation(
            "example.com",
            AddressCount: 1,
            FirstAddress: "93.184.216.34",
            ConnectionRefused: true,
            ConnectFailure: "SocketException: в соединении отказано");

        var silent = new AccessObservation(
            "example.com",
            AddressCount: 1,
            FirstAddress: "93.184.216.34",
            ConnectFailure: "SocketException: таймаут");

        Assert.Equal(AccessOutcome.ConnectionRefused, AccessClassifier.Classify(refused).Outcome);
        Assert.Equal(AccessOutcome.Unreachable, AccessClassifier.Classify(silent).Outcome);
        Assert.Contains("не блокировка", AccessClassifier.Classify(refused).DetailRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Неразрешённое_имя_называет_свою_причину()
    {
        var observation = new AccessObservation("не-существует.example", DnsFailure: "SocketException: узел недостижим");

        AccessDiagnosis diagnosis = AccessClassifier.Classify(observation);

        Assert.Equal(AccessOutcome.DnsFailed, diagnosis.Outcome);
        Assert.Contains("DNS", diagnosis.DetailRu, StringComparison.Ordinal);
        Assert.Contains("опечат", diagnosis.DetailRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Замер_которого_не_было_не_считается_проблемой()
    {
        AccessDiagnosis diagnosis = AccessClassifier.Classify(AccessObservation.NotMeasured("example.com"));

        Assert.Equal(AccessOutcome.NotMeasured, diagnosis.Outcome);
        Assert.False(diagnosis.IsProblem);
        Assert.Contains("не выполнялся", diagnosis.TitleRu, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(451, null, null, true)]
    [InlineData(302, "http://zapret-info.example/blocked", null, true)]
    [InlineData(302, "https://blocked.example/", null, true)]
    [InlineData(200, null, "<h1>Доступ ограничен: Роскомнадзор</h1>", true)]
    [InlineData(200, null, "<h1>Добро пожаловать</h1>", false)]
    [InlineData(404, null, "not found", false)]
    public void Признаки_заглушки_распознаются(int status, string? location, string? body, bool expected)
    {
        Assert.Equal(expected, AccessClassifier.LooksLikeStub(status, location, body));
    }

    [Theory]
    [InlineData("www.youtube.com", "www.youtube.com")]
    [InlineData("https://www.youtube.com/watch?v=1", "www.youtube.com")]
    [InlineData("http://discord.com/", "discord.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("  EXAMPLE.com.  ", "EXAMPLE.com")]
    public void Имя_сайта_приводится_к_виду_пригодному_для_DNS(string input, string expected)
    {
        Assert.Equal(expected, LiveAccessProbe.NormalizeHost(input));
    }

    [Fact]
    public async Task Кнопка_проверяет_то_что_вписано_и_показывает_вывод()
    {
        var probe = new FakeProbe(new AccessObservation(
            "example.com",
            AddressCount: 1,
            FirstAddress: "93.184.216.34",
            ConnectMilliseconds: 9,
            TlsMilliseconds: 30));
        var model = new AccessCheckViewModel(probe)
        {
            HostInput = "https://example.com/path",
        };

        Assert.False(model.HasResult);
        await model.CheckCommand.ExecuteAsync();

        Assert.Equal(1, probe.Calls);
        Assert.Equal("example.com", probe.LastHost);
        Assert.True(model.HasResult);
        Assert.Equal("Доступ есть", model.ResultTitleRu);
        Assert.NotEmpty(model.ResultDetailRu);
        Assert.False(model.IsProblem);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Пустое_имя_не_запускает_замер()
    {
        var probe = new FakeProbe(AccessObservation.NotMeasured(""));
        var model = new AccessCheckViewModel(probe) { HostInput = "   " };

        await model.CheckCommand.ExecuteAsync();

        Assert.Equal(0, probe.Calls);
        Assert.False(model.HasResult);
        Assert.Contains("Впишите имя сайта", model.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Сборка_без_замера_честно_выключает_кнопку()
    {
        var model = AccessCheckViewModel.Unavailable();

        Assert.False(model.CheckCommand.CanExecute(null));
        await model.CheckCommand.ExecuteAsync();
        Assert.False(model.HasResult);
        Assert.Contains("не подключён", model.StatusRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Карточка_в_программе_не_обещает_обход()
    {
        string xaml = ReadChecked("OptimizeView.xaml");
        Assert.Contains("Проверить доступ", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AccessCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding HonestTextRu}", xaml, StringComparison.Ordinal);
        // Кнопка, поле и тексты вывода — с именами для диктора и с переносом строк.
        Assert.Contains("AutomationProperties.Name=\"Проверить доступ к сайту\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Сайт для проверки доступа\"", xaml, StringComparison.Ordinal);

        foreach (string promise in new[] { "включить обход", "обойти блокировку в ", "откроет заблокированный" })
        {
            Assert.DoesNotContain(promise, xaml, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Честная_оговорка_есть_и_в_модели_и_в_документе()
    {
        Assert.Contains("не обходит блокировки", AccessCheckViewModel.HonestNoteRu, StringComparison.OrdinalIgnoreCase);

        string doc = ReadChecked("blocking-doc.md");
        Assert.Contains("обхода блокировок в программе нет", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не делает", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Проверить доступ", doc, StringComparison.Ordinal);
        // Ссылки на первоисточники, а не на пересказ.
        Assert.Contains("rfc6066", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GoodbyeDPI", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void Самопроверка_меряет_доступ_а_сборка_требует_строку()
    {
        string selfCheck = ReadChecked("SelfCheck.cs");
        Assert.Contains("ДОСТУП:", selfCheck, StringComparison.Ordinal);
        Assert.Contains("LiveAccessProbe", selfCheck, StringComparison.Ordinal);

        string workflow = ReadChecked("build.yml");
        Assert.Contains("ДОСТУП:", workflow, StringComparison.Ordinal);
        Assert.Contains("Строки «ДОСТУП:» нет в журнале", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Живой_замер_подключён_в_приложении_и_в_главной_модели()
    {
        string app = ReadChecked("App.xaml.cs");
        Assert.Contains("new AccessCheckViewModel(new LiveAccessProbe())", app, StringComparison.Ordinal);

        string model = ReadChecked("MainViewModel.cs");
        Assert.Contains("public AccessCheckViewModel Access", model, StringComparison.Ordinal);
        Assert.Contains("AccessCheckViewModel.Unavailable()", model, StringComparison.Ordinal);
    }
}
