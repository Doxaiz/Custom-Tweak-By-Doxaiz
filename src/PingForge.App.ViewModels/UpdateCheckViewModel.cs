using System.Net;
using System.Text;
using System.Text.Json;

namespace PingForge.App.ViewModels;

/// <summary>
/// Исход проверки обновлений. Отдельного состояния «файл скачан/установлен» нет нарочно:
/// программа ничего не скачивает и не устанавливает сама — она только смотрит и показывает.
/// </summary>
public enum UpdateCheckState
{
    /// <summary>Кнопку ещё не нажимали. В этом состоянии запросов не было и быть не должно.</summary>
    NotChecked,

    /// <summary>Проверка идёт прямо сейчас.</summary>
    Checking,

    /// <summary>Новая версия не новее установленной.</summary>
    UpToDate,

    /// <summary>На GitHub лежит версия новее установленной.</summary>
    UpdateAvailable,

    /// <summary>Проверить не получилось: сеть, ответ GitHub или неразбираемый тег версии.</summary>
    Failed,
}

/// <summary>
/// Номер версии из тега релиза. Тег на GitHub может быть «v0.5.1», «0.5.1» или мусором вроде
/// «nightly» — мусор не превращается в «версию 0», а честно отклоняется: сравнивать не с чем.
/// </summary>
public static class ReleaseTagVersion
{
    /// <summary>
    /// Разобрать тег в версию из трёх чисел (недостающие — нули). Возвращает false для всего,
    /// что не похоже на номер: пусто, буквы, суффиксы вроде «-beta».
    /// </summary>
    public static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        // Одно число («v2») — тоже номер версии; Version требует точку, поэтому дописываем «.0»,
        // но только если в теге одни цифры: всё остальное как было мусором, так им и остаётся.
        // (Наши релизы называются vX.Y.Z, но чужие цифры в ответе GitHub могут быть короче.)
        if (!text.Contains('.', StringComparison.Ordinal) && text.All(char.IsDigit))
        {
            text += ".0";
        }

        if (!Version.TryParse(text, out Version? parsed) || parsed is null)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, parsed.Build < 0 ? 0 : parsed.Build);
        return true;
    }
}

/// <summary>Один файл релиза на GitHub: имя и адрес скачивания — как их назвал сам GitHub.</summary>
public sealed record GitHubReleaseAsset(string Name, string? DownloadUrl);

/// <summary>
/// Ответ GitHub о последнем релизе — ровно те поля, что нужны проверке обновлений:
/// тег версии, страница релиза и список приложенных файлов (ради SHA256SUMS.txt).
/// </summary>
public sealed record GitHubReleaseInfo(string TagName, string? HtmlUrl, IReadOnlyList<GitHubReleaseAsset> Assets)
{
    /// <summary>Имя файла с контрольными суммами — такое же, каким его прикладывает сборка.</summary>
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    /// <summary>Разобрать JSON ответа <c>releases/latest</c>. Не JSON или нет тега — <see cref="JsonException"/>.</summary>
    public static GitHubReleaseInfo Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out JsonElement tagElement)
            || tagElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("В ответе GitHub нет тега версии (tag_name) — сравнивать не с чем.");
        }

        string? htmlUrl = root.TryGetProperty("html_url", out JsonElement html) && html.ValueKind == JsonValueKind.String
            ? html.GetString()
            : null;

        var assets = new List<GitHubReleaseAsset>();
        if (root.TryGetProperty("assets", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? downloadUrl = item.TryGetProperty("browser_download_url", out JsonElement url)
                    && url.ValueKind == JsonValueKind.String
                    ? url.GetString()
                    : null;

                assets.Add(new GitHubReleaseAsset(name.GetString() ?? string.Empty, downloadUrl));
            }
        }

        return new GitHubReleaseInfo(tagElement.GetString() ?? string.Empty, htmlUrl, assets);
    }

    /// <summary>Файл контрольных сумм этого релиза, если сборка его приложила.</summary>
    public GitHubReleaseAsset? FindChecksums() =>
        Assets.FirstOrDefault(asset => string.Equals(asset.Name, ChecksumsFileName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Файл SHA256SUMS.txt: строки «64 шестнадцатеричных знака, пробелы, имя файла» (перед именем
/// может стоять звёздочка — так пишет sha256sum в бинарном режиме). Строки, не похожие на сумму,
/// не угадываются, а пропускаются.
/// </summary>
public static class Sha256SumsFile
{
    /// <summary>Одна контрольная сумма: файл и его SHA-256 нижним регистром.</summary>
    public sealed record Entry(string FileName, string Sha256);

    /// <summary>Разобрать текст файла. Мусорных строк в результате нет — они отброшены, а не исправлены.</summary>
    public static IReadOnlyList<Entry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<Entry>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 66)
            {
                continue;
            }

            string hash = line[..64];
            if (!hash.All(IsHex))
            {
                continue;
            }

            string fileName = line[64..].Trim();
            if (fileName.StartsWith("*", StringComparison.Ordinal))
            {
                fileName = fileName[1..].Trim();
            }

            if (fileName.Length == 0)
            {
                continue;
            }

            entries.Add(new Entry(fileName, hash.ToLowerInvariant()));
        }

        return entries;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}

/// <summary>
/// Транспорт проверки обновлений: один запрос «дай текст по адресу». Вынесен за интерфейс, чтобы
/// тесты проверяли логику на двойнике, а настоящая сеть оставалась в одной маленькой реализации.
/// </summary>
public interface IUpdateCheckTransport
{
    /// <summary>Получить текст по адресу. Любая неудача — исключение: модель превратит её в честные слова.</summary>
    Task<string> GetStringAsync(Uri address, CancellationToken ct = default);
}

/// <summary>
/// Настоящий HTTP-транспорт проверки обновлений. Упёртый в честные рамки: 20 секунд на запрос,
/// ответ не больше мегабайта, обычный браузерный User-Agent не имитируется — GitHub видит, что
/// это проверка обновлений Custom Tweak By Doxaiz, и видит только после нажатия кнопки.
/// </summary>
public sealed class HttpUpdateCheckTransport : IUpdateCheckTransport
{
    private const long MaxResponseBytes = 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();

    public async Task<string> GetStringAsync(Uri address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        using HttpResponseMessage response = await Client
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];

        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxResponseBytes)
            {
                throw new HttpRequestException("Ответ GitHub больше мегабайта — это не похоже на сведения о релизе, читать не будем.");
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { MaxAutomaticRedirections = 3 };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        string version = typeof(HttpUpdateCheckTransport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CustomTweakByDoxaiz/{version} (update check by button)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

/// <summary>
/// Проверка обновлений в разделе «О программе» (задача I). Правила проекта, зашитые сюда:
///
///   • запрос уходит ТОЛЬКО по кнопке — в фоне программа никуда не ходит, опросов по расписанию нет;
///   • программа ничего не скачивает и не устанавливает сама: она показывает номер новой версии,
///     страницу релиза (открывается обычным браузером) и SHA-256 из приложенного к релизу
///     SHA256SUMS.txt — чтобы человек сверил файл после ручного скачивания;
///   • нет сети или GitHub недоступен — честное «проверить не удалось» без повторных попыток:
///     повтор — только ещё одно нажатие человека;
///   • ссылки из ответа открываются, только если ведут на github.com напрямую. Чужие адреса
///     (и адреса-переадресаторы) программа открывать не предлагает.
/// </summary>
public sealed class UpdateCheckViewModel : ObservableObject
{
    /// <summary>Адрес API GitHub: последний релиз репозитория программы.</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/Doxaiz/Custom-Tweak-By-Doxaiz/releases/latest";

    /// <summary>Страница последнего релиза — для ручной проверки, когда API промолчал.</summary>
    public const string LatestReleasePageUrl = "https://github.com/Doxaiz/Custom-Tweak-By-Doxaiz/releases/latest";

    private const string NotCheckedRu =
        "Обновления ещё не проверяли. Проверка — только по этой кнопке: в фоне программа никуда не ходит.";

    private readonly IUpdateCheckTransport _transport;
    private readonly Version _currentVersion;

    private UpdateCheckState _state = UpdateCheckState.NotChecked;
    private string _statusRu = NotCheckedRu;
    private string? _latestVersionRu;
    private string? _releasePageUrl;
    private string? _checksumsRu;

    /// <summary>Боевой конструктор: настоящий HTTP-транспорт и версия установленной программы.</summary>
    public UpdateCheckViewModel()
        : this(new HttpUpdateCheckTransport(), SelfVersion())
    {
    }

    /// <summary>Конструктор для тестов и самопроверки: транспорт и версия задаются снаружи.</summary>
    public UpdateCheckViewModel(IUpdateCheckTransport transport, Version currentVersion)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(currentVersion);
        _currentVersion = Normalize(currentVersion);
        CurrentVersionLineRu = $"Текущая версия: {_currentVersion.ToString(3)}.";

        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
    }

    /// <summary>Кнопка «Проверить обновления». Единственный способ начать проверку.</summary>
    public AsyncRelayCommand CheckUpdatesCommand { get; }

    /// <summary>Исход проверки — по нему интерфейс показывает и прячет части карточки.</summary>
    public UpdateCheckState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy), nameof(HasUpdate), nameof(HasReleasePage));
            }
        }
    }

    /// <summary>Идёт ли проверка прямо сейчас.</summary>
    public bool IsBusy => State == UpdateCheckState.Checking;

    /// <summary>Что сказать человеку прямо сейчас — одной фразой, на странице, а не модальным окном.</summary>
    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>«Текущая версия: X.Y.Z.» — человек видит, с чем сравнивать, ещё до нажатия.</summary>
    public string CurrentVersionLineRu { get; }

    /// <summary>Номер новой версии без «v» — только когда она действительно найдена и новее.</summary>
    public string? LatestVersionRu
    {
        get => _latestVersionRu;
        private set => Set(ref _latestVersionRu, value);
    }

    /// <summary>Нашлась ли версия новее установленной.</summary>
    public bool HasUpdate => State == UpdateCheckState.UpdateAvailable;

    /// <summary>
    /// Страница релиза, которую можно открыть кнопкой. Заполняется только адресом вида
    /// https://github.com/… из ответа GitHub; всё остальное сюда не попадает никогда.
    /// </summary>
    public string? ReleasePageUrl
    {
        get => _releasePageUrl;
        private set
        {
            if (Set(ref _releasePageUrl, value))
            {
                OnPropertyChanged(nameof(HasReleasePage));
            }
        }
    }

    /// <summary>Показывать ли кнопку «Открыть страницу релиза».</summary>
    public bool HasReleasePage => HasUpdate && ReleasePageUrl is { Length: > 0 };

    /// <summary>SHA-256 для ручной сверки — или честная причина, почему их нет. null, пока о суммах речи нет.</summary>
    public string? ChecksumsRu
    {
        get => _checksumsRu;
        private set
        {
            if (Set(ref _checksumsRu, value))
            {
                OnPropertyChanged(nameof(HasChecksums));
            }
        }
    }

    /// <summary>Есть ли что показать в блоке контрольных сумм.</summary>
    public bool HasChecksums => !string.IsNullOrWhiteSpace(ChecksumsRu);

    /// <summary>
    /// Проверить обновления сейчас. Ровно один запрос к GitHub (и, если обновление нашлось,
    /// второй — за файлом контрольных сумм). При неудаче повторов не делается.
    /// </summary>
    public async Task CheckUpdatesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        State = UpdateCheckState.Checking;
        LatestVersionRu = null;
        ReleasePageUrl = null;
        ChecksumsRu = null;
        StatusRu = "Проверяем… Спрашиваем GitHub о последней версии.";

        try
        {
            string json = await _transport
                .GetStringAsync(new Uri(LatestReleaseApiUrl), CancellationToken.None)
                .ConfigureAwait(true);

            GitHubReleaseInfo info;
            try
            {
                info = GitHubReleaseInfo.Parse(json);
            }
            catch (JsonException ex)
            {
                Fail($"GitHub отдал ответ, который не читается: {ex.Message}");
                return;
            }

            if (!ReleaseTagVersion.TryParse(info.TagName, out Version latest))
            {
                Fail($"GitHub назвал последнюю версию «{info.TagName}» — это не похоже на номер версии, " +
                     $"сравнивать с {_currentVersion.ToString(3)} нечего. Посмотрите страницу релизов вручную: {LatestReleasePageUrl}");
                return;
            }

            if (latest.CompareTo(_currentVersion) <= 0)
            {
                State = UpdateCheckState.UpToDate;
                StatusRu = $"Обновлений нет: у вас версия {_currentVersion.ToString(3)}, " +
                           $"а GitHub называет последней {latest.ToString(3)}.";
                return;
            }

            LatestVersionRu = latest.ToString(3);
            ReleasePageUrl = TrustedGitHubUrl(info.HtmlUrl);
            State = UpdateCheckState.UpdateAvailable;
            StatusRu = ReleasePageUrl is not null
                ? $"Доступна версия {LatestVersionRu} — у вас {_currentVersion.ToString(3)}. " +
                  "Программа ничего не скачивает сама: откройте страницу релиза, скачайте файл вручную " +
                  "и сверьте его SHA-256 с суммами ниже."
                : $"Доступна версия {LatestVersionRu} — у вас {_currentVersion.ToString(3)}. " +
                  "Ссылку из ответа программа открывать не будет: она ведёт не на github.com. " +
                  $"Откройте страницу релизов вручную: {LatestReleasePageUrl}";

            ChecksumsRu = await LoadChecksumsRuAsync(info).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Fail("GitHub не успел ответить вовремя.");
        }
        catch (Exception ex)
        {
            Fail(DescribeFailureRu(ex));
        }
    }

    /// <summary>Браузер не открылся: ответ — сюда же, на страницу, с адресом для ручного открытия.</summary>
    public void ReportOpenFailed(string url)
    {
        StatusRu = $"Не удалось открыть браузер. Откройте страницу вручную: {url}";
    }

    /// <summary>
    /// Читать SHA256SUMS.txt из найденного релиза. Не получилось — обновление от этого не пропадает,
    /// просто сверять человеку придётся по файлу на странице релиза, о чём здесь честно сказано.
    /// </summary>
    private async Task<string> LoadChecksumsRuAsync(GitHubReleaseInfo info)
    {
        GitHubReleaseAsset? asset = info.FindChecksums();
        if (asset is null)
        {
            return $"Файл {GitHubReleaseInfo.ChecksumsFileName} в этом релизе не найден — сверять скачанное не с чем, будьте осторожны.";
        }

        string? url = TrustedGitHubUrl(asset.DownloadUrl);
        if (url is null)
        {
            return $"Ссылку на файл контрольных сумм программа открывать не будет: она ведёт не на github.com. " +
                   $"Файл {GitHubReleaseInfo.ChecksumsFileName} можно посмотреть на странице релиза.";
        }

        string text;
        try
        {
            text = await _transport.GetStringAsync(new Uri(url), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return $"Контрольные суммы получить не удалось — откройте {GitHubReleaseInfo.ChecksumsFileName} " +
                   "на странице релиза и сверите скачанный файл с ним вручную.";
        }

        IReadOnlyList<Sha256SumsFile.Entry> entries = Sha256SumsFile.Parse(text);
        if (entries.Count == 0)
        {
            return $"Файл {GitHubReleaseInfo.ChecksumsFileName} прочитался, но контрольных сумм в нём не нашлось.";
        }

        var builder = new StringBuilder(
            $"SHA-256 из файла {GitHubReleaseInfo.ChecksumsFileName} этого релиза — сверьте после ручного скачивания:");
        foreach (Sha256SumsFile.Entry entry in entries)
        {
            builder.Append($"\n{entry.FileName}: {entry.Sha256}");
        }

        return builder.ToString();
    }

    private void Fail(string reasonRu)
    {
        State = UpdateCheckState.Failed;
        StatusRu = $"Проверить не удалось: {reasonRu} Программа сама повторять не будет — " +
                   "нажмите «Проверить обновления» ещё раз, когда будет сеть.";
    }

    private static string DescribeFailureRu(Exception ex)
    {
        if (ex is HttpRequestException http && http.StatusCode is { } code)
        {
            return code == HttpStatusCode.NotFound
                ? $"GitHub не отдал сведения о релизах (404). Если репозиторий программы приватный, " +
                  $"анонимно релизы не видны — проверьте вручную под своим аккаунтом: {LatestReleasePageUrl}"
                : $"GitHub ответил ошибкой {(int)code}.";
        }

        if (ex is HttpRequestException or TaskCanceledException)
        {
            return "связи с GitHub нет (сеть недоступна или GitHub не отвечает).";
        }

        return $"что-то пошло не так при обращении к GitHub ({ex.GetType().Name}).";
    }

    /// <summary>
    /// Адрес, который программе можно открывать: только https и только github.com напрямую.
    /// Всё остальное — чужие домены и переадресаторы — вернётся как null, и кнопка не появится.
    /// </summary>
    private static string? TrustedGitHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static Version SelfVersion() =>
        Normalize(typeof(UpdateCheckViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
}
