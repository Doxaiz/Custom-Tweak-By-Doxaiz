using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;

namespace PingForge.Service.Protocol;

/// <summary>Команды, которые служба понимает. Список закрытый: «вообще любую строку» служба не выполняет.</summary>
public enum EngineCommandKind
{
    /// <summary>Проверка, что служба жива, и сверка версии протокола.</summary>
    Ping,

    /// <summary>Список команд с описанием.</summary>
    Capabilities,

    /// <summary>Что сейчас происходит: оптимизация идёт или нет, игровой поток, профиль потребления.</summary>
    Status,

    /// <summary>Применить пресет твиков. Нужны права администратора.</summary>
    ApplyPreset,

    /// <summary>Применить список твиков по кодам. Нужны права администратора.</summary>
    ApplyTweaks,

    /// <summary>Вернуть прежние настройки из снапшота. Нужны права администратора.</summary>
    RollbackSnapshot,

    /// <summary>Починить сеть: снять остатки маршрутов, фильтров и политик после аварии. Нужны права администратора.</summary>
    RepairNetwork,

    /// <summary>Последние записи журнала.</summary>
    JournalTail,

    /// <summary>Начать живую оптимизацию. Нужны права администратора.</summary>
    RealtimeStart,

    /// <summary>Остановить живую оптимизацию и снять свои изменения.</summary>
    RealtimeStop,

    /// <summary>Профиль потребления ресурсов (экономно / обычно / максимум реакции).</summary>
    PerfProfile,

    /// <summary>Прочитать текущее состояние Windows и сети: версия, права, адаптер, MTU, DNS, настройки твиков.
    /// Ничего не меняет — поэтому безопасно звать из интерфейса когда угодно.</summary>
    ReadSystemState,
}

/// <summary>Запрос от приложения к службе. <paramref name="Id"/> — сквозной номер, чтобы ответ не перепутать.</summary>
public sealed record EngineRequest(
    string Id,
    EngineCommandKind Command,
    IReadOnlyDictionary<string, string>? Args = null)
{
    /// <summary>Аргумент по имени или <c>null</c>, если его не прислали.</summary>
    public string? Argument(string name) =>
        Args is not null && Args.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>
/// Ответ службы. При ошибке <see cref="Ok"/> = false, а <see cref="MessageRu"/> — готовый текст
/// для пользователя: служба не отвечает кодами, которые интерфейсу пришлось бы переводить самому.
/// </summary>
public sealed record EngineResponse(string Id, bool Ok, string MessageRu, string? DataJson = null)
{
    public static EngineResponse Success(string id, string messageRu, string? dataJson = null) =>
        new(id, true, messageRu, dataJson);

    public static EngineResponse Failure(string id, string messageRu) => new(id, false, messageRu);
}

/// <summary>
/// Кто подключился к каналу — по его собственному процессу, а не по токену службы (C7).
/// Заполняется на стороне службы из <c>PipeClientInspector</c> и едет в ответе <c>ping</c>
/// полями <c>clientName</c>, <c>clientSid</c> и <c>clientIsAdmin</c>: так живая проверка на
/// раннере может сличить то, что видит служба, с токеном самого проверяющего процесса.
/// Ошибка чтения — это «не определён» и «прав нет», а не «наверное администратор».
/// </summary>
public sealed record EnginePeerIdentity(
    string ClientName,
    string ClientSid,
    bool ClientIsAdmin,
    int ClientProcessId = 0,
    string? ProblemRu = null)
{
    /// <summary>Личность не прочитана: имени и SID нет, прав администратора нет.</summary>
    public static EnginePeerIdentity Unknown(string problemRu) =>
        new("не определён", "не определён", false, 0, problemRu);
}

/// <summary>Результат работы самой операции — то же, что ответ, но без номера запроса.</summary>
public sealed record EngineOperationResult(bool Ok, string MessageRu, string? DataJson = null)
{
    public static EngineOperationResult Success(string messageRu, string? dataJson = null) => new(true, messageRu, dataJson);

    public static EngineOperationResult Failure(string messageRu) => new(false, messageRu);
}

/// <summary>
/// Имя команды в JSON — из таблицы команд, а не из имени значения перечисления.
///
/// Так было не всегда, и это стоило ошибки: имя собиралось из перечисления, поэтому приложение писало
/// «read-system-state» и «rollback-snapshot», а служба ждала «system-state» и «rollback». Канал отвечал
/// «команда неизвестна» — то есть кнопки чтения состояния и возврата настроек не работали бы вовсе,
/// хотя обе стороны были из одной сборки. Теперь имя одно на двоих, и его проверяет тест на каждое
/// значение перечисления.
/// </summary>
public sealed class EngineCommandKindJsonConverter : JsonConverter<EngineCommandKind>
{
    public override EngineCommandKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? wireName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        EngineCommandDescriptor? descriptor = EngineCommandCatalog.Find(wireName);

        return descriptor is not null
            ? descriptor.Kind
            : throw new JsonException(
                $"Команда «{wireName}» неизвестна. Известные команды: {EngineCommandCatalog.KnownWireNamesRu()}.");
    }

    public override void Write(Utf8JsonWriter writer, EngineCommandKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EngineCommandCatalog.Get(value).WireName);
}

/// <summary>JSON протокола: имена свойств в camelCase, перечисления строками — чтобы журнал канала читался глазами.</summary>
public static class EngineJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            // Имена команд берутся из таблицы команд — из того же места, откуда их читает служба.
            new EngineCommandKindJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false),
        },
    };

    public static string Encode(EngineRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Encode(EngineResponse response) => JsonSerializer.Serialize(response, Options);

    /// <summary>
    /// Разобрать запрос. Проверки идут по порядку, и на каждой ошибке возвращается текст, который
    /// можно показать пользователю: молча отбрасывать запрос служба не имеет права.
    /// </summary>
    public static bool TryDecodeRequest(string json, out EngineRequest? request, out string errorRu)
    {
        request = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorRu = "Пустой запрос: служба ждёт JSON с полями id и command.";
            return false;
        }

        WireRequest? wire;
        try
        {
            wire = JsonSerializer.Deserialize<WireRequest>(json, Options);
        }
        catch (JsonException ex)
        {
            errorRu = $"Запрос не разобрался как JSON: {ex.Message}";
            return false;
        }

        if (wire is null)
        {
            errorRu = "Запрос не разобрался: вместо объекта пришло что-то другое.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(wire.Id))
        {
            errorRu = "В запросе нет id: без него ответ не с чем сверить.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(wire.Command))
        {
            errorRu = $"В запросе «{wire.Id}» не указана команда. Известные команды: {EngineCommandCatalog.KnownWireNamesRu()}.";
            return false;
        }

        EngineCommandDescriptor? descriptor = EngineCommandCatalog.Find(wire.Command);
        if (descriptor is null)
        {
            errorRu = $"Команда «{wire.Command}» неизвестна. Известные команды: {EngineCommandCatalog.KnownWireNamesRu()}.";
            return false;
        }

        request = new EngineRequest(wire.Id, descriptor.Kind, wire.Args);
        errorRu = string.Empty;
        return true;
    }

    /// <summary>Разобрать ответ (нужно клиенту и тестам протокола).</summary>
    /// <summary>
    /// Разобрать ответ службы. Битую рамку честно называем нарушением протокола: иначе «служба другой
    /// версии» выглядела бы для человека как «служба не отвечает», и искать причину пришлось бы наугад.
    /// </summary>
    public static EngineResponse DecodeResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EngineResponse>(json, Options)
                ?? throw new EngineProtocolViolationException("Ответ службы разобрался в null.");
        }
        catch (JsonException ex)
        {
            string sample = json.Length <= 120 ? json : json[..120] + "…";
            throw new EngineProtocolViolationException($"Ответ не разбирается как JSON: «{sample}».", ex);
        }
    }

    /// <summary>То, что реально приходит по каналу: перечисление строкой, а не числом.</summary>
    private sealed record WireRequest(string? Id, string? Command, Dictionary<string, string>? Args);
}

/// <summary>
/// Слова, которыми приложение и служба называют пресеты и профили. Держатся здесь по одной причине:
/// раньше такие строки писались в двух местах, и «низкий пинг» в интерфейсе мог не совпасть
/// с тем, что понимает служба. Путаницы больше нет — таблица одна.
/// </summary>
public static class EngineNames
{
    private static readonly IReadOnlyDictionary<string, TweakProfilePreset> PresetByWire =
        new Dictionary<string, TweakProfilePreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["low-latency"] = TweakProfilePreset.LowLatencyGaming,
            ["max-speed"] = TweakProfilePreset.MaxThroughput,
            ["fps"] = TweakProfilePreset.FpsAndResponsiveness,
            ["from-snapshot"] = TweakProfilePreset.RestoreSnapshotDefault,
            ["custom"] = TweakProfilePreset.CustomUserPreset,
        };

    private static readonly IReadOnlyDictionary<string, PerfProfile> ProfileByWire =
        new Dictionary<string, PerfProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["economy"] = PerfProfile.Economy,
            ["balanced"] = PerfProfile.Balanced,
            ["max"] = PerfProfile.MaxPerformance,
        };

    /// <summary>Как звучит пресет для человека.</summary>
    public static string PresetTitleRu(TweakProfilePreset preset) => preset switch
    {
        TweakProfilePreset.LowLatencyGaming => "Низкий пинг",
        TweakProfilePreset.MaxThroughput => "Максимальная скорость",
        TweakProfilePreset.FpsAndResponsiveness => "Кадры и отзывчивость",
        TweakProfilePreset.RestoreSnapshotDefault => "Откат к прежним настройкам",
        _ => "Свой набор твиков",
    };

    /// <summary>Как звучит пресет по-английски (для сопоставления витрины и каталога).</summary>
    public static string PresetTitleEn(TweakProfilePreset preset) => preset switch
    {
        TweakProfilePreset.LowLatencyGaming => "Low latency gaming",
        TweakProfilePreset.MaxThroughput => "Max throughput",
        TweakProfilePreset.FpsAndResponsiveness => "Fps and responsiveness",
        TweakProfilePreset.RestoreSnapshotDefault => "Restore snapshot default",
        _ => "Custom user preset",
    };

    public static string PresetWireName(TweakProfilePreset preset) => preset switch
    {
        TweakProfilePreset.LowLatencyGaming => "low-latency",
        TweakProfilePreset.MaxThroughput => "max-speed",
        TweakProfilePreset.FpsAndResponsiveness => "fps",
        TweakProfilePreset.RestoreSnapshotDefault => "from-snapshot",
        _ => "custom",
    };

    public static string ProfileWireName(PerfProfile profile) => profile switch
    {
        PerfProfile.Economy => "economy",
        PerfProfile.Balanced => "balanced",
        _ => "max",
    };

    /// <summary>Список пресетов с расшифровкой — попадает прямо в текст ошибки.</summary>
    public static string PresetsListRu { get; } =
        string.Join(", ", PresetByWire.Select(pair => $"{pair.Key} ({PresetTitleRu(pair.Value)})"));

    /// <summary>Список профилей потребления с расшифровкой.</summary>
    public static string ProfilesListRu { get; } =
        "economy (экономно), balanced (обычно), max (максимум реакции)";

    public static bool TryParsePreset(string? wire, out TweakProfilePreset preset)
    {
        preset = TweakProfilePreset.CustomUserPreset;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        return PresetByWire.TryGetValue(wire.Trim(), out preset);
    }

    public static bool TryParseProfile(string? wire, out PerfProfile profile)
    {
        profile = PerfProfile.Balanced;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        return ProfileByWire.TryGetValue(wire.Trim(), out profile);
    }
}
