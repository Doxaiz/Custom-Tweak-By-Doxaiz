using PingForge.App.ViewModels;
using PingForge.Service.Protocol;

namespace PingForge.Tests;

/// <summary>
/// Двойник канала службы. Помнит состояние — как настоящая служба: после успешного запуска оптимизации
/// команда «статус» отвечает, что оптимизация идёт. Без этого тест проверял бы ошибку, которой нет.
/// Служит и «исправной службой», и «службой, которой нет» — второе важнее: интерфейс обязан
/// вести себя внятно, когда связи нет.
/// </summary>
public sealed class FakeEngineChannel : IEngineChannel
{
    private readonly Func<EngineRequest, EngineResponse>? _responder;

    public FakeEngineChannel(Func<EngineRequest, EngineResponse>? responder = null) => _responder = responder;

    public List<EngineRequest> Requests { get; } = [];

    /// <summary>Идёт ли оптимизация прямо сейчас.</summary>
    public bool RealtimeRunning { get; set; }

    /// <summary>Есть ли права администратора.</summary>
    public bool IsElevationAvailable { get; set; } = true;

    /// <summary>Числа задержки, которые «служба» называет. null — «измерить не удалось», а не ноль.</summary>
    public double? BaselineRttMs { get; set; } = 120;

    public double? CurrentRttMs { get; set; } = 78;

    public double? ImprovementMs { get; set; } = 42;

    /// <summary>Название игры: null — «игра не найдена».</summary>
    public string? GameTitleRu { get; set; } = "Fortnite";

    /// <summary>Если задано — канал «не отвечает» и бросает это исключение.</summary>
    public Exception? ThrowOnSend { get; set; }

    /// <summary>Подменить номер в ответе: так проверяется защита от чужого ответа.</summary>
    public bool AnswerWithWrongId { get; set; }

    /// <summary>Сколько ждал клиент в каждом запросе: так проверяется, что долгие команды не обрываются.</summary>
    public List<(EngineRequest Request, TimeSpan Timeout)> Calls { get; } = [];

    public Task<EngineResponse> SendAsync(EngineRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        Requests.Add(request);
        Calls.Add((request, timeout));

        if (ThrowOnSend is not null)
        {
            return Task.FromException<EngineResponse>(ThrowOnSend);
        }

        string id = AnswerWithWrongId ? "чужой-номер" : request.Id;

        EngineResponse response = _responder is not null
            ? _responder(request) with { Id = id }
            : DefaultAnswer(request);
        response = response with { Id = id };

        return Task.FromResult(response);
    }

    public void Dispose()
    {
    }

    /// <summary>Ответы исправной службы: состояние, запуск, откат, журнал.</summary>
    public EngineResponse DefaultAnswer(EngineRequest request) => request.Command switch
    {
        EngineCommandKind.Ping => EngineResponse.Success(
            request.Id,
            "Служба жива.",
            """{"product":"Custom Tweak By Doxaiz","appVersion":"0.1.0","protocolVersion":1,"tweakCount":47,"isElevationAvailable":true,"commandCount":11}"""),

        EngineCommandKind.Capabilities => EngineResponse.Success(request.Id, "Список команд.", """{"commands":[]}"""),

        EngineCommandKind.Status => StatusAnswer(request),

        EngineCommandKind.RealtimeStart => StartAnswer(request),

        EngineCommandKind.RealtimeStop => StopAnswer(request),

        EngineCommandKind.ApplyPreset => EngineResponse.Success(
            request.Id,
            "Низкий пинг: применено 19 из 19, без изменений 0, пропущено 0, ошибок 0."),

        EngineCommandKind.ApplyTweaks => EngineResponse.Success(
            request.Id,
            "свой набор: применено 3 из 3, без изменений 0, пропущено 0, ошибок 0."),

        EngineCommandKind.RollbackSnapshot => EngineResponse.Success(request.Id, "Прежние настройки возвращены."),

        EngineCommandKind.RepairNetwork => EngineResponse.Success(request.Id, "Остатков после аварии не найдено."),

        EngineCommandKind.PerfProfile => EngineResponse.Success(request.Id, "Профиль потребления: экономно."),

        EngineCommandKind.JournalTail => EngineResponse.Success(
            request.Id,
            "Последние 2 записей журнала (новые сверху). Не получилось: 1.",
            """
            {"count":2,"records":[
            {"loggedUtc":"2026-10-16T12:10:00Z","eventKind":"TWEAK_APPLY","tweakCode":"TCP_NO_DELAY","tweakTitleRu":"Отключение алгоритма Нейгла","previousValueText":"0","appliedValueText":"1","verifiedActualText":"1","succeeded":true,"rebootNeeded":false,"explanationRu":"VerifiedEffective"},
            {"loggedUtc":"2026-10-16T12:09:00Z","eventKind":"TWEAK_APPLY","tweakCode":"NIC_JUMBO_FRAMES_NOT_RECOMMENDED","tweakTitleRu":"Jumbo frames","previousValueText":"0","appliedValueText":"9014","verifiedActualText":"0","succeeded":false,"rebootNeeded":false,"explanationRu":"политика запретила запись"}
            ]}
            """),

        _ => EngineResponse.Failure(request.Id, $"Команда «{request.Command}» не поддержана двойником."),
    };

    private EngineResponse StatusAnswer(EngineRequest request)
    {
        if (!RealtimeRunning)
        {
            return EngineResponse.Success(
                request.Id,
                "Оптимизация не запущена.",
                $$"""{"realtimeState":"idle","realtimeStateRu":"не запущена","isElevationAvailable":{{Bool(IsElevationAvailable)}},"tweakCount":47,"pendingOperations":0}""");
        }

        return EngineResponse.Success(
            request.Id,
            "Идёт контроль качества.",
            $$"""
            {"realtimeState":"active-monitoring","realtimeStateRu":"идёт контроль качества","isElevationAvailable":{{Bool(IsElevationAvailable)}},"tweakCount":47,"pendingOperations":0,
             "gameTitleRu":{{Text(GameTitleRu)}},"endpoint":"3.4.5.6","baselineRttMs":{{Number(BaselineRttMs)}},"currentRttMs":{{Number(CurrentRttMs)}},"improvementMs":{{Number(ImprovementMs)}},"qualityLabelRu":"хорошо"}
            """);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Отсутствующее число пишется как null: «не измерено» — это не ноль.</summary>
    private static string Number(double? value) =>
        value is null ? "null" : value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Text(string? value) =>
        value is null ? "null" : "\"" + value + "\"";

    private EngineResponse StartAnswer(EngineRequest request)
    {
        if (!IsElevationAvailable)
        {
            RealtimeRunning = true;
            return EngineResponse.Success(
                request.Id,
                "Оптимизация запущена без прав: идут только замеры, менять сеть служба не станет.",
                """{"state":"read-only-no-admin","realtimeState":"read-only-no-admin","isElevationAvailable":false}""");
        }

        RealtimeRunning = true;
        string what = GameTitleRu is null
            ? "Игра не найдена: живая оптимизация ничего не меняет."
            : $"Игра найдена: {GameTitleRu}, задержка {Number(CurrentRttMs)} мс.";

        return EngineResponse.Success(
            request.Id,
            what,
            $$"""
            {"state":"active-monitoring","realtimeState":"active-monitoring","gameTitleRu":{{Text(GameTitleRu)}},"endpoint":"3.4.5.6",
             "baselineRttMs":{{Number(BaselineRttMs)}},"currentRttMs":{{Number(CurrentRttMs)}},"improvementMs":{{Number(ImprovementMs)}},
             "everythingReverted":true,"appliedActionTitles":["Маршрут для игрового сервера"]}
            """);
    }

    private EngineResponse StopAnswer(EngineRequest request)
    {
        RealtimeRunning = false;
        return EngineResponse.Success(
            request.Id,
            "Оптимизация остановлена, свои изменения сняты.",
            $$"""
            {"state":"idle","realtimeState":"idle","baselineRttMs":{{Number(BaselineRttMs)}},"currentRttMs":{{Number(CurrentRttMs)}},
             "improvementMs":{{Number(ImprovementMs)}},"everythingReverted":true,"appliedActionTitles":[]}
            """);
    }
}

/// <summary>
/// Двойник диалогов. По умолчанию пользователь соглашается и просит точку восстановления —
/// тогда тест проверяет основной путь. Отказ задаётся явно.
/// </summary>
public sealed class FakeDialogs : IUserDialogs
{
    public bool ConfirmAnswer { get; set; } = true;

    public bool RestorePointAnswer { get; set; } = true;

    public List<(string Title, string Message, string ConfirmLabel, string CancelLabel)> Confirmations { get; } = [];

    public List<(string Title, string Message)> Information { get; } = [];

    public List<(int TweakCount, string PresetTitle)> RestorePointOffers { get; } = [];

    public Task<bool> ConfirmAsync(string titleRu, string messageRu, string confirmLabelRu, string cancelLabelRu)
    {
        Confirmations.Add((titleRu, messageRu, confirmLabelRu, cancelLabelRu));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task InformAsync(string titleRu, string messageRu)
    {
        Information.Add((titleRu, messageRu));
        return Task.CompletedTask;
    }

    public Task<bool> OfferRestorePointAsync(int tweakCount, string presetTitleRu)
    {
        RestorePointOffers.Add((tweakCount, presetTitleRu));
        return Task.FromResult(RestorePointAnswer);
    }
}
