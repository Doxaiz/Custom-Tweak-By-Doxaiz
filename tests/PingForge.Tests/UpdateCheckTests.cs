using System.Net;
using PingForge.App.ViewModels;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Задача I: проверка обновлений по кнопке. Правила, которые держит этот набор:
///   • до нажатия кнопки запросов нет вообще, а статус честно говорит «ещё не проверяли»;
///   • сравнение версий: «v»-префикс не мешает, мусорный тег — отказ, а не «версия 0»;
///   • нет сети / GitHub ответил ошибкой — «проверить не удалось» и ни одного скрытого повтора;
///   • ссылка на страницу релиза предлагается, только если ведёт на github.com напрямую;
///   • SHA-256 берутся из приложенного к релизу SHA256SUMS.txt — и только для показа:
///     программа ничего не скачивает и не устанавливает сама.
/// </summary>
public sealed class UpdateCheckTests
{
    private const string SumsDownloadUrl = "https://github.com/Doxaiz/Custom-Tweak-By-Doxaiz/releases/download/v0.6.0/SHA256SUMS.txt";

    /// <summary>Двойник транспорта: помнит вызовы и отвечает готовыми строками по адреса́м.</summary>
    private sealed class FakeTransport : IUpdateCheckTransport
    {
        public List<Uri> Calls { get; } = [];

        /// <summary>Ответ по адресу; null — «ответ не задан», и вызов честно падает.</summary>
        public Func<Uri, string?>? Responder { get; set; }

        /// <summary>Исключение вместо ответа — так выглядит «сети нет» и «GitHub недоступен».</summary>
        public Exception? Throw { get; set; }

        /// <summary>Затвор для проверки двойного нажатия: ответ приходит только после его открытия.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<string> GetStringAsync(Uri address, CancellationToken ct = default)
        {
            Calls.Add(address);

            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(true);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Responder?.Invoke(address)
                ?? throw new InvalidOperationException($"ответ для {address} не задан");
        }
    }

    private static string ReleaseJson(string tag, string? htmlUrl = null, bool withSumsAsset = false) =>
        $"{{\"tag_name\":\"{tag}\"," +
        $"\"html_url\":\"{htmlUrl ?? $"https://github.com/Doxaiz/Custom-Tweak-By-Doxaiz/releases/tag/{tag}"}\"," +
        "\"assets\":[" +
        "{\"name\":\"CustomTweakByDoxaiz-Setup.exe\",\"browser_download_url\":\"https://github.com/Doxaiz/download/setup\"}" +
        (withSumsAsset ? $",{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"{SumsDownloadUrl}\"}}" : string.Empty) +
        "]}";

    private static UpdateCheckViewModel Create(FakeTransport transport, string currentVersion = "0.5.1")
    {
        Assert.True(ReleaseTagVersion.TryParse(currentVersion, out Version current));
        return new UpdateCheckViewModel(transport, current);
    }

    // ── Состояние до нажатия: «не существует в фоне» ─────────────────────────────────────────

    [Fact]
    public void До_нажатия_кнопки_запросов_нет_и_статус_честный()
    {
        var transport = new FakeTransport();
        UpdateCheckViewModel updates = Create(transport);

        Assert.Empty(transport.Calls);
        Assert.Equal(UpdateCheckState.NotChecked, updates.State);
        Assert.Contains("ещё не проверяли", updates.StatusRu, StringComparison.Ordinal);
        Assert.Contains("только по этой кнопке", updates.StatusRu, StringComparison.Ordinal);
        Assert.False(updates.HasUpdate);
        Assert.False(updates.HasReleasePage);
        Assert.Null(updates.ReleasePageUrl);
        Assert.Contains("0.5.1", updates.CurrentVersionLineRu, StringComparison.Ordinal);
    }

    // ── Сравнение версий ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("v0.4.1", "0.5.0", true)]   // «v»-префикс не мешает числу
    [InlineData("0.5.1", "0.5.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("v2", "1.9.9", true)]
    [InlineData("0.5.0", "v0.5.0", false)]  // та же версия — не обновление
    [InlineData("0.4.1", "0.5.0", false)]   // на GitHub старше установленной
    public void Теги_сравниваются_как_числовые_версии(string firstTag, string secondTag, bool firstIsNewer)
    {
        Assert.True(ReleaseTagVersion.TryParse(firstTag, out Version first));
        Assert.True(ReleaseTagVersion.TryParse(secondTag, out Version second));
        Assert.Equal(firstIsNewer, first.CompareTo(second) > 0);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("latest-build")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.5.x")]
    [InlineData("v")]
    public void Мусорный_тег_не_разбирается(string tag)
    {
        Assert.False(ReleaseTagVersion.TryParse(tag, out _));
        Assert.False(ReleaseTagVersion.TryParse(null, out _));
    }

    // ── Проверка по кнопке: счастливые пути ──────────────────────────────────────────────────

    [Fact]
    public async Task Новая_версия_находится_и_показывается_со_страницей_релиза()
    {
        var transport = new FakeTransport
        {
            Responder = address => address.AbsoluteUri == UpdateCheckViewModel.LatestReleaseApiUrl
                ? ReleaseJson("v0.6.0")
                : null,
        };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpdateAvailable, updates.State);
        Assert.Equal("0.6.0", updates.LatestVersionRu);
        Assert.Contains("Доступна версия 0.6.0", updates.StatusRu, StringComparison.Ordinal);
        Assert.Contains("ничего не скачивает сама", updates.StatusRu, StringComparison.Ordinal);
        Assert.True(updates.HasReleasePage);
        Assert.StartsWith("https://github.com/", updates.ReleasePageUrl, StringComparison.Ordinal);
        // Файла сумм в этом релизе не приложено — об этом сказано, а не промолчано.
        Assert.Contains("не найден", updates.ChecksumsRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Та_же_версия_на_GitHub_значит_обновлений_нет()
    {
        var transport = new FakeTransport { Responder = _ => ReleaseJson("v0.5.1") };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpToDate, updates.State);
        Assert.Contains("Обновлений нет", updates.StatusRu, StringComparison.Ordinal);
        Assert.False(updates.HasReleasePage);
        Assert.Null(updates.ChecksumsRu);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task Версия_на_GitHub_старее_установленной_обновлением_не_считается()
    {
        var transport = new FakeTransport { Responder = _ => ReleaseJson("v0.4.1") };
        UpdateCheckViewModel updates = Create(transport, currentVersion: "0.5.0");

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpToDate, updates.State);
        Assert.False(updates.HasUpdate);
    }

    // ── Честное «не удалось»: сеть, ошибки, мусор — без повторов ─────────────────────────────

    [Fact]
    public async Task Нет_сети_значит_не_удалось_и_ни_одного_скрытого_повтора()
    {
        var transport = new FakeTransport { Throw = new HttpRequestException("нет соединения") };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.Failed, updates.State);
        Assert.Contains("Проверить не удалось", updates.StatusRu, StringComparison.Ordinal);
        Assert.Contains("сама повторять не будет", updates.StatusRu, StringComparison.Ordinal);
        Assert.Single(transport.Calls);   // ровно один запрос: никаких автоповторов
        Assert.Null(updates.ReleasePageUrl);
    }

    [Fact]
    public async Task GitHub_ответил_404_значит_не_удалось_с_подсказкой_про_ручную_проверку()
    {
        var transport = new FakeTransport
        {
            // Именно так выглядит анонимный взгляд на приватный репозиторий.
            Throw = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound),
        };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.Failed, updates.State);
        Assert.Contains("404", updates.StatusRu, StringComparison.Ordinal);
        Assert.Contains(UpdateCheckViewModel.LatestReleasePageUrl, updates.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Мусорный_тег_последнего_релиза_это_честный_отказ()
    {
        var transport = new FakeTransport { Responder = _ => ReleaseJson("nightly") };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.Failed, updates.State);
        Assert.Contains("«nightly»", updates.StatusRu, StringComparison.Ordinal);
        Assert.Contains("не похоже на номер версии", updates.StatusRu, StringComparison.Ordinal);
        Assert.False(updates.HasUpdate);
    }

    [Fact]
    public async Task Ответ_не_JSON_значит_не_удалось_а_не_падение()
    {
        var transport = new FakeTransport { Responder = _ => "<html>proxy</html>" };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.Failed, updates.State);
        Assert.Contains("не читается", updates.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Повторное_нажатие_во_время_проверки_не_делает_второй_запрос()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport { Gate = gate, Responder = _ => ReleaseJson("v0.5.1") };
        UpdateCheckViewModel updates = Create(transport);

        Task first = updates.CheckUpdatesCommand.ExecuteAsync();
        Task second = updates.CheckUpdatesCommand.ExecuteAsync();
        await second;                       // второе нажатие обязано завершиться сразу, без запроса
        Assert.Single(transport.Calls);

        gate.SetResult();
        await first;

        Assert.Single(transport.Calls);
        Assert.Equal(UpdateCheckState.UpToDate, updates.State);
    }

    // ── Ссылки: только github.com напрямую ───────────────────────────────────────────────────

    [Fact]
    public async Task Ссылка_не_на_github_не_предлагается_как_страница_релиза()
    {
        var transport = new FakeTransport
        {
            Responder = _ => ReleaseJson("v0.6.0", htmlUrl: "https://t.co/short-release-link"),
        };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpdateAvailable, updates.State);
        Assert.Null(updates.ReleasePageUrl);
        Assert.False(updates.HasReleasePage);
        Assert.Contains("открывать не будет", updates.StatusRu, StringComparison.Ordinal);
        Assert.Contains(UpdateCheckViewModel.LatestReleasePageUrl, updates.StatusRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Суммы_с_чужого_домена_не_скачиваются()
    {
        string json = ReleaseJson("v0.6.0", withSumsAsset: true)
            .Replace(SumsDownloadUrl, "https://files.example.com/SHA256SUMS.txt", StringComparison.Ordinal);
        var transport = new FakeTransport { Responder = _ => json };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpdateAvailable, updates.State);
        Assert.Single(transport.Calls);   // второй запрос на чужой домен не ушёл
        Assert.Contains("не на github.com", updates.ChecksumsRu, StringComparison.Ordinal);
    }

    // ── SHA256SUMS.txt: показ для ручной сверки ──────────────────────────────────────────────

    [Fact]
    public async Task Контрольные_суммы_показываются_из_файла_релиза()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var transport = new FakeTransport
        {
            Responder = address => address.AbsoluteUri switch
            {
                UpdateCheckViewModel.LatestReleaseApiUrl => ReleaseJson("v0.6.0", withSumsAsset: true),
                SumsDownloadUrl => $"{hash}  CustomTweakByDoxaiz-Setup-0.6.0.exe",
                _ => null,
            },
        };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpdateAvailable, updates.State);
        Assert.Equal(2, transport.Calls.Count);
        Assert.True(updates.HasChecksums);
        Assert.Contains(hash, updates.ChecksumsRu, StringComparison.Ordinal);
        Assert.Contains("CustomTweakByDoxaiz-Setup-0.6.0.exe", updates.ChecksumsRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Суммы_не_скачались_обновление_всё_равно_показано()
    {
        var transport = new FakeTransport
        {
            Responder = address => address.AbsoluteUri == UpdateCheckViewModel.LatestReleaseApiUrl
                ? ReleaseJson("v0.6.0", withSumsAsset: true)
                : null,   // запрос за суммами у двойника ответа нет — падает честным исключением
        };
        UpdateCheckViewModel updates = Create(transport);

        await updates.CheckUpdatesCommand.ExecuteAsync();

        Assert.Equal(UpdateCheckState.UpdateAvailable, updates.State);
        Assert.True(updates.HasReleasePage);
        Assert.Contains("получить не удалось", updates.ChecksumsRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Файл_сумм_разбирается_по_строкам_и_мусор_пропускается()
    {
        const string first = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        IReadOnlyList<Sha256SumsFile.Entry> entries = Sha256SumsFile.Parse(
            $"{first}  one.exe\r\n{second} *two.zip\nне сумма\n{first[..10]} обрезанная\r\n");

        Assert.Equal(2, entries.Count);
        Assert.Equal("one.exe", entries[0].FileName);
        Assert.Equal(first, entries[0].Sha256);
        Assert.Equal("two.zip", entries[1].FileName);
        Assert.Equal(second.ToLowerInvariant(), entries[1].Sha256);
    }

    // ── Сторожи: кнопка на месте, фона нет ───────────────────────────────────────────────────

    private static string ReadChecked(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "checked-" + name);
        Assert.True(File.Exists(path), $"нет файла для проверки: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Сторож_кнопка_обновлений_живёт_в_разделе_о_программе()
    {
        string about = ReadChecked("AboutView.xaml");

        Assert.Contains("Проверить обновления", about, StringComparison.Ordinal);
        Assert.Contains("CheckUpdatesCommand", about, StringComparison.Ordinal);
        Assert.Contains("Открыть страницу релиза", about, StringComparison.Ordinal);
        Assert.Contains("HasReleasePage", about, StringComparison.Ordinal);
        Assert.Contains("Updates", about, StringComparison.Ordinal);
    }

    [Fact]
    public void Сторож_фоновой_проверки_обновлений_нет()
    {
        // Запуск программы не должен проверять обновления: при старте программа вообще ничего
        // не делает. Кто добавит вызов в App.xaml.cs — этот тест покраснеет.
        string app = ReadChecked("App.xaml.cs");
        Assert.DoesNotContain("CheckUpdates", app, StringComparison.Ordinal);

        // А в самой модели проверки нет ни таймеров, ни циклов опроса: запрос уйдёт только по кнопке.
        string model = ReadChecked("UpdateCheckViewModel.cs");
        Assert.DoesNotContain("Timer", model, StringComparison.Ordinal);
        Assert.DoesNotContain("while (true)", model, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", model, StringComparison.Ordinal);  // браузер открывает окно, не модель
        Assert.DoesNotContain("DownloadFile", model, StringComparison.Ordinal);   // программа себя не скачивает
    }
}
