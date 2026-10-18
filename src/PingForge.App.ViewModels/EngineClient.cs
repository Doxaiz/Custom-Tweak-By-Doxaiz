using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Сверка версий окна и службы (C5). Окно и служба ставятся одной сборкой, но живут отдельно:
/// служба может остаться старой после частичного обновления, и молча работать с такой нельзя.
/// </summary>
public enum EngineVersionState
{
    /// <summary>Служба ещё не называла своих версий — ничего не известно.</summary>
    Unknown,

    /// <summary>Версии совпадают: работаем как обычно.</summary>
    Match,

    /// <summary>Протоколы разные: меняющие команды не отправляются вовсе.</summary>
    ProtocolMismatch,

    /// <summary>Протокол один, а версии программы различаются: работаем, но предупреждаем.</summary>
    AppDiffers,
}

/// <summary>
/// Разговор со службой на стороне интерфейса.
///
/// Здесь собирается всё, что интерфейс не должен делать сам: сквозные номера запросов (чтобы ответ
/// не перепутался), единый текст «служба недоступна» и одно место, где решается, что считать отказом.
/// Здесь же — сверка версий окна и службы: меняющие команды уходят только после того, как версии
/// сошлись или как минимум протокол совпал.
/// </summary>
public sealed class EngineClient
{
    private readonly IEngineChannel _channel;
    private int _lastId;
    private bool _versionProbeAttempted;

    public EngineClient(IEngineChannel channel) =>
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    /// <summary>Номер последнего отправленного запроса — для журнала и тестов.</summary>
    public string LastRequestId { get; private set; } = string.Empty;

    /// <summary>Описание команд, полученное от службы. Пусто, пока не спросили.</summary>
    public string? CapabilitiesRu { get; private set; }

    /// <summary>Версия протокола, названная службой (из команды «проверить связь»).</summary>
    public int? ProtocolVersion { get; private set; }

    /// <summary>Версия программы, названная службой.</summary>
    public string? AppVersion { get; private set; }

    /// <summary>Что показала сверка версий окна и службы.</summary>
    public EngineVersionState VersionState { get; private set; } = EngineVersionState.Unknown;

    /// <summary>Версия протокола этой сборки окна.</summary>
    public static int SelfProtocolVersion => EngineProtocol.Version;

    /// <summary>Версия программы этой сборки окна.</summary>
    public static string SelfAppVersion { get; } =
        typeof(EngineClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Строка о версиях для раздела «О программе» и для предупреждений. Говорит правду:
    /// «не проверено» — тоже честный ответ, а не пустое место.
    /// </summary>
    public string VersionsNoteRu => VersionState switch
    {
        EngineVersionState.Match =>
            $"Окно и служба из одной сборки: версия {SelfAppVersion}, протокол {SelfProtocolVersion}.",
        EngineVersionState.ProtocolMismatch =>
            $"Версии не подходят друг другу: протокол окна {SelfProtocolVersion}, а у службы {ProtocolVersion} " +
            $"(служба версии {AppVersion ?? "не названа"}). Меняющие команды не отправляются. " +
            "Поставьте установщик заново — он обновит и программу, и службу.",
        EngineVersionState.AppDiffers =>
            $"Версии различаются: у окна {SelfAppVersion}, у службы {AppVersion} (протокол {SelfProtocolVersion} совпадает). " +
            "Команды работают, но поставьте установщик заново, чтобы окно и служба снова были из одной сборки.",
        _ =>
            "Версия службы ещё не проверена: программа спросит её при первом обращении.",
    };

    /// <summary>Отвечает ли служба и совпадает ли версия протокола.</summary>
    public async Task<(bool Ok, string MessageRu)> PingAsync(CancellationToken ct = default)
    {
        EngineResponse response = await SendAsync(EngineCommandKind.Ping, null, ct).ConfigureAwait(false);
        return (response.Ok, response.MessageRu);
    }

    /// <summary>Спросить у службы список команд один раз и запомнить.</summary>
    public async Task<string> GetCapabilitiesAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && CapabilitiesRu is not null)
        {
            return CapabilitiesRu;
        }

        EngineResponse response = await SendAsync(EngineCommandKind.Capabilities, null, ct).ConfigureAwait(false);
        CapabilitiesRu = response.Ok
            ? response.DataJson ?? response.MessageRu
            : $"Служба не отдала список команд: {response.MessageRu}";
        return CapabilitiesRu;
    }

    /// <summary>
    /// Отправить команду. Связь пропала — исключение с одним и тем же понятным текстом,
    /// потому что «канал закрыт» и «служба не запущена» выглядят для пользователя одинаково.
    /// </summary>
    public async Task<EngineResponse> SendAsync(
        EngineCommandKind command,
        IReadOnlyDictionary<string, string>? args = null,
        CancellationToken ct = default)
    {
        // C5. Меняющую команду окно не отправляет, пока не знает версий службы: служба могла остаться
        // старой после частичного обновления, и молча выполнить команду в другой мир нельзя.
        // Проверка — один раз за сессию и только перед первой меняющей командой: при запуске программа
        // по-прежнему ничего не делает, проверка идёт по нажатию, вместе с самой командой.
        if (IsMutating(command) && VersionState == EngineVersionState.Unknown && !_versionProbeAttempted)
        {
            _versionProbeAttempted = true;
            try
            {
                await SendAsync(EngineCommandKind.Ping, null, ct).ConfigureAwait(false);
            }
            catch (EngineUnavailableException)
            {
                // Служба молчит — сверять не с чем. Пойдём дальше: у самой команды будет честный текст
                // «служба не отвечает», повторять опрос ради опроса незачем.
            }
        }

        if (IsMutating(command) && VersionState == EngineVersionState.ProtocolMismatch)
        {
            // Отказ собирается здесь и до службы НЕ доходит: отправлять команду в другой протокол —
            // это как стучать на незнакомом языке: ответ всё равно будет непонятным или опасным.
            string denial =
                $"Команда «{EngineCommandCatalog.Get(command).WireName}» не отправлена: версия протокола окна " +
                $"{SelfProtocolVersion}, а у службы {ProtocolVersion}. Окно и служба должны быть из одной сборки — " +
                "поставьте установщик заново, он обновит и программу, и службу. Пока версии не сошлись, " +
                "доступны только просмотр, замеры и остановка своих изменений.";
            return EngineResponse.Failure(NextId(), denial);
        }

        string id = NextId();
        var request = new EngineRequest(id, command, args);

        // Предел ожидания берётся из каталога команд: у «проверить связь» — секунды, у применения
        // твиков с точкой восстановления или живого запуска — минуты. Один общий предел в 10 секунд
        // означал, что долгая команда выглядела как «служба не ответила», хотя служба продолжала работать.
        TimeSpan timeout = TimeSpan.FromSeconds(EngineCommandCatalog.Get(command).TimeoutSeconds);

        EngineResponse response;
        try
        {
            response = await _channel.SendAsync(request, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EngineUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EngineUnavailableException(
                "Служба PingForge не отвечает. Проверьте, что она запущена: без неё менять настройки нельзя, " +
                "а замеры недоступны.", ex);
        }

        if (!string.Equals(response.Id, id, StringComparison.Ordinal))
        {
            throw new EngineUnavailableException(
                $"Служба ответила на чужой запрос (ждали «{id}», пришло «{response.Id}»). Связь закрыта, чтобы " +
                "не принять чужой ответ за свой.");
        }

        if (response.Ok)
        {
            AbsorbVersions(response.DataJson);
        }

        return response;
    }

    /// <summary>Меняет ли команда настройки Windows или программы. Один список с каналом чтения службы:
    /// что не «только просмотр», то и меняющее.</summary>
    private static bool IsMutating(EngineCommandKind command) =>
        !EngineCommandCatalog.IsAllowedOnReadOnlyChannel(EngineCommandCatalog.Get(command));

    /// <summary>
    /// Запомнить версии из ответа службы и решить, что с ними делать. Ответ без версий ничего не меняет:
    /// «неизвестно» не превращается в «совпадает».
    /// </summary>
    private void AbsorbVersions(string? dataJson)
    {
        int? protocol = ExtractInt(dataJson, "protocolVersion");
        string? app = ExtractString(dataJson, "appVersion");

        ProtocolVersion = protocol ?? ProtocolVersion;
        AppVersion = app ?? AppVersion;

        if (ProtocolVersion is null)
        {
            VersionState = EngineVersionState.Unknown;
            return;
        }

        if (ProtocolVersion != SelfProtocolVersion)
        {
            VersionState = EngineVersionState.ProtocolMismatch;
            return;
        }

        VersionState = AppVersion is not null && !string.Equals(AppVersion, SelfAppVersion, StringComparison.Ordinal)
            ? EngineVersionState.AppDiffers
            : EngineVersionState.Match;
    }

    /// <summary>Удобная обёртка: команда с аргументами строкой.</summary>
    public Task<EngineResponse> SendAsync(
        EngineCommandKind command,
        params (string Name, string Value)[] args) =>
        SendAsync(command, args.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private string NextId()
    {
        int next = Interlocked.Increment(ref _lastId);
        LastRequestId = $"ui-{next}";
        return LastRequestId;
    }

    private static int? ExtractInt(string? dataJson, string name)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(dataJson);

            // Ответ службы бывает и массивом (список узлов маршрутизации) — у чисел версии это не
            // берётся, и спрашивать у массива свойства, которую TryGetProperty не любит, незачем.
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.TryGetProperty(name, out System.Text.Json.JsonElement value)
                && value.TryGetInt32(out int number)
                ? number
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (System.InvalidOperationException)
        {
            // Форма ответа не та — «версии не названы», а не падение окна.
            return null;
        }
    }

    private static string? ExtractString(string? dataJson, string name)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.TryGetProperty(name, out System.Text.Json.JsonElement value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (System.InvalidOperationException)
        {
            return null;
        }
    }
}
