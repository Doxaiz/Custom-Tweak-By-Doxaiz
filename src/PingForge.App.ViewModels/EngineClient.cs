using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>
/// Разговор со службой на стороне интерфейса.
///
/// Здесь собирается всё, что интерфейс не должен делать сам: сквозные номера запросов (чтобы ответ
/// не перепутался), единый текст «служба недоступна» и одно место, где решается, что считать отказом.
/// </summary>
public sealed class EngineClient
{
    private readonly IEngineChannel _channel;
    private int _lastId;

    public EngineClient(IEngineChannel channel) =>
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    /// <summary>Номер последнего отправленного запроса — для журнала и тестов.</summary>
    public string LastRequestId { get; private set; } = string.Empty;

    /// <summary>Описание команд, полученное от службы. Пусто, пока не спросили.</summary>
    public string? CapabilitiesRu { get; private set; }

    /// <summary>Версия протокола, названная службой (из команды «проверить связь»).</summary>
    public int? ProtocolVersion { get; private set; }

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
        string id = NextId();
        var request = new EngineRequest(id, command, args);

        EngineResponse response;
        try
        {
            response = await _channel.SendAsync(request, ct).ConfigureAwait(false);
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

        if (command == EngineCommandKind.Ping && response.Ok)
        {
            ProtocolVersion = ExtractProtocolVersion(response.DataJson) ?? ProtocolVersion;
        }

        return response;
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

    private static int? ExtractProtocolVersion(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(dataJson);
            return document.RootElement.TryGetProperty("protocolVersion", out System.Text.Json.JsonElement value)
                && value.TryGetInt32(out int version)
                ? version
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
