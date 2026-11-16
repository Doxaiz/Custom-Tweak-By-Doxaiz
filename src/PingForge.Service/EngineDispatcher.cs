using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.Service;

/// <summary>
/// Разбор запроса и вызов нужной операции. Здесь же — единственное место, где проверяются права:
/// если команда меняет систему, а прав нет, дальше она не проходит. Ошибка никогда не «улетает» в канал:
/// клиент обязан получить ответ с текстом, который можно показать пользователю.
///
/// Проверок прав две, и они не заменяют друг друга: права клиента (у того, кто подключился к каналу,
/// в его собственном процессе) и права службы. Без первой проверки канал службы — это готовое повышение
/// привилегий для любого пользователя компьютера: служба работает от LocalSystem и выполнит любую команду.
/// </summary>
public sealed class EngineDispatcher
{
    /// <summary>Предел числа твиков в одном запросе. В каталоге их 47, запас двойной — на «включить все» после роста каталога.</summary>
    public const int MaxTweaksPerRequest = 200;

    /// <summary>Сколько записей журнала отдаётся по умолчанию и максимум.</summary>
    public const int DefaultJournalLimit = 20;

    public const int MaxJournalLimit = 500;

    /// <summary>Сколько сессий отдавать по умолчанию: месяц ежедневных запусков ещё виден целиком.</summary>
    public const int DefaultSessionsLimit = 50;

    private readonly IEngineOperations _operations;

    /// <summary>Какой канал обслуживает этот разборщик: от этого зависит набор доступных команд.</summary>
    private readonly EngineChannelKind _channelKind;

    public EngineDispatcher(IEngineOperations operations, EngineChannelKind channelKind = EngineChannelKind.Main)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _channelKind = channelKind;
    }

    /// <summary>
    /// Разборщик тех же операций для другого канала (C2). Один и тот же набор действий службы,
    /// но разные наборы команд: канал чтения не выполняет ничего, что меняет Windows, — даже для
    /// администратора. Так проверка «что можно через этот канал» не смешивается с проверкой прав.
    /// </summary>
    public EngineDispatcher ForChannel(EngineChannelKind channelKind) => new(_operations, channelKind);

    /// <summary>
    /// Полный путь: JSON по каналу → ответ. Пустой или битый запрос тоже получает ответ с текстом.
    /// </summary>
    /// <param name="requestJson">Кадр запроса, пришедший по каналу.</param>
    /// <param name="clientIsAdministrator">
    /// Есть ли права администратора у того, кто подключился к каналу, — в его собственном процессе.
    /// По умолчанию «да» только ради существующих проверок разбора запросов; настоящий канал обязан
    /// передавать сюда прочитанные права клиента (см. <c>PipeClientInspector</c>).
    /// </param>
    /// <param name="onDenialRu">Куда сообщить об отказе по правам: служба пишет это в журнал вместе с именем клиента.</param>
    /// <param name="ct">Отмена: закрылось окно или останавливается служба.</param>
    /// <param name="peer">
    /// Личность клиента, прочитанная каналом по его процессу (C7). Если задана, флаг прав
    /// берётся из неё, а личность целиком уезжает в ответ ping.
    /// </param>
    public async Task<EngineResponse> HandleJsonAsync(
        string requestJson,
        bool clientIsAdministrator = true,
        Action<string>? onDenialRu = null,
        CancellationToken ct = default,
        EnginePeerIdentity? peer = null)
    {
        if (!EngineJson.TryDecodeRequest(requestJson, out EngineRequest? request, out string errorRu))
        {
            // id неизвестен: отвечаем пустым, чтобы клиент не принял чужой ответ за свой.
            return EngineResponse.Failure(string.Empty, errorRu);
        }

        return await HandleAsync(request!, clientIsAdministrator, onDenialRu, ct, peer).ConfigureAwait(false);
    }

    public async Task<EngineResponse> HandleAsync(
        EngineRequest request,
        bool clientIsAdministrator = true,
        Action<string>? onDenialRu = null,
        CancellationToken ct = default,
        EnginePeerIdentity? peer = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        // C7: если канал передал прочитанную личность клиента, флаг прав берётся ИЗ НЕЁ, а не
        // из параметра по умолчанию. Именно здесь раньше терялся смысл проверки: тесты и канал
        // могли подставить «администратор: да» руками, и диспетчер честно верил подставленному.
        if (peer is not null)
        {
            clientIsAdministrator = peer.ClientIsAdmin;
        }

        EngineCommandDescriptor descriptor = EngineCommandCatalog.Get(request.Command);

        // C2: канал только для чтения не выполняет команды, меняющие систему, — и это отдельная
        // проверка от прав клиента. Даже администратор, подключившийся к каналу чтения, получит
        // отказ и подсказку идти в основной канал: изменения должны проходить через место, где
        // работают все проверки, а не через «удобную лазейку» рядом.
        if (_channelKind == EngineChannelKind.ReadOnly && !EngineCommandCatalog.IsAllowedOnReadOnlyChannel(descriptor))
        {
            string denial =
                $"Команда «{descriptor.WireName}» недоступна: этот канал службы открыт только для чтения. " +
                $"Отсюда доступны {EngineCommandCatalog.ReadOnlyChannelWireNamesRu()} — посмотреть состояние, " +
                "прочитать журнал и убрать свои изменения. Чтобы менять настройки Windows, запустите программу " +
                "от имени администратора: тогда она подключится к основному каналу, где такие команды выполняются.";
            onDenialRu?.Invoke(denial);
            return EngineResponse.Failure(request.Id, denial);
        }

        if (descriptor.NeedsElevation)
        {
            // Две независимые проверки, и первая из них — про КЛИЕНТА, а не про службу.
            //
            // Служба идёт от LocalSystem, поэтому её собственные права есть всегда: если проверять
            // только их, любой пользователь этого компьютера (второй профиль, гость, любой скрипт
            // от имени вошедшего пользователя) мог бы отправить «применить твики» по каналу и получить
            // изменение настроек Windows от имени системы — без единого запроса UAC.
            // Канал при этом открыт для интерактивного входа на чтение и запись: смотреть состояние
            // можно всем, а менять — только тому, у кого права есть в его собственном процессе.
            if (!clientIsAdministrator)
            {
                string denial =
                    $"Команда «{descriptor.WireName}» меняет настройки Windows и выполняется только для администратора этого компьютера. " +
                    "У процесса, который её прислал, прав администратора нет. Права службы здесь не помогают: она работает " +
                    "от имени системы, поэтому проверяется именно ваша учётная запись. Закройте программу и запустите её " +
                    "от имени администратора — тогда изменение настроек станет доступным. Смотреть состояние, журнал и " +
                    "останавливать свои изменения можно и без прав.";
                onDenialRu?.Invoke(denial);
                return EngineResponse.Failure(request.Id, denial);
            }

            if (!_operations.IsElevationAvailable)
            {
                string denial =
                    $"Команда «{descriptor.WireName}» меняет настройки Windows, а у службы PingForge сейчас нет прав администратора. " +
                    "Проверьте, что служба запущена от имени системы, или переустановите её скриптом install-service.ps1 " +
                    "от имени администратора.";
                onDenialRu?.Invoke(denial);
                return EngineResponse.Failure(request.Id, denial);
            }
        }

        var args = new EngineArgs(request.Args, descriptor.WireName);

        try
        {
            EngineOperationResult result = request.Command switch
            {
                EngineCommandKind.Ping => await _operations.PingAsync(peer, ct).ConfigureAwait(false),

                EngineCommandKind.Capabilities => await _operations.CapabilitiesAsync(ct).ConfigureAwait(false),

                EngineCommandKind.Status => await _operations.StatusAsync(ct).ConfigureAwait(false),

                EngineCommandKind.ApplyPreset => await ApplyPresetAsync(args, ct).ConfigureAwait(false),

                EngineCommandKind.ApplyTweaks => await ApplyTweaksAsync(args, ct).ConfigureAwait(false),

                EngineCommandKind.RollbackSnapshot => await _operations.RollbackAsync(args.Get("snapshot")?.Trim(), ct).ConfigureAwait(false),

                EngineCommandKind.RepairNetwork => await _operations.RepairNetworkAsync(ct).ConfigureAwait(false),

                EngineCommandKind.JournalTail => await _operations.JournalTailAsync(
                    args.GetInt("limit", DefaultJournalLimit, 1, MaxJournalLimit), ct).ConfigureAwait(false),

                EngineCommandKind.RealtimeStart => await _operations.SetRealtimeAsync(true, ct).ConfigureAwait(false),

                EngineCommandKind.RealtimeStop => await _operations.SetRealtimeAsync(false, ct).ConfigureAwait(false),

                EngineCommandKind.PerfProfile => await SetPerfProfileAsync(args, ct).ConfigureAwait(false),

                EngineCommandKind.ReadSystemState => await _operations.ReadSystemStateAsync(ct).ConfigureAwait(false),

                EngineCommandKind.RouteDiscover => await _operations.DiscoverRoutingNodesAsync(ct).ConfigureAwait(false),

                EngineCommandKind.RouteMeasure => await _operations.MeasureRoutingPathsAsync(ct).ConfigureAwait(false),

                EngineCommandKind.UninstallCheck => await _operations.UninstallCheckAsync(ct).ConfigureAwait(false),

                EngineCommandKind.Sessions => await _operations.SessionsAsync(
                    args.GetInt("limit", DefaultSessionsLimit, 1, MaxJournalLimit), ct).ConfigureAwait(false),

                EngineCommandKind.SessionsClear => await _operations.ClearSessionsAsync(ct).ConfigureAwait(false),
                EngineCommandKind.RollbackSingleTweak => await RollbackSingleTweakAsync(args, ct).ConfigureAwait(false),
                EngineCommandKind.ReturnAll => await _operations.ReturnAllAsync(ct).ConfigureAwait(false),
                EngineCommandKind.StorageInfo => await _operations.GetStorageInfoAsync(ct).ConfigureAwait(false),
                EngineCommandKind.CleanupSnapshots => await CleanupSnapshotsAsync(args, ct).ConfigureAwait(false),
                EngineCommandKind.DbRestore => await RestoreDatabaseAsync(args, ct).ConfigureAwait(false),

                _ => EngineOperationResult.Failure($"Команда «{descriptor.WireName}» объявлена, но не подключена."),
            };

            return new EngineResponse(request.Id, result.Ok, result.MessageRu, result.DataJson);
        }
        catch (EngineArgumentException ex)
        {
            return EngineResponse.Failure(request.Id, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return EngineResponse.Failure(
                request.Id,
                $"Команда «{descriptor.WireName}» прервана: пришла остановка или закрылось окно. Изменения, которые успели примениться, видны в журнале.");
        }
        catch (Exception ex)
        {
            return EngineResponse.Failure(
                request.Id,
                $"Служба не смогла выполнить команду «{descriptor.WireName}»: {ex.Message}. Это записано в журнал программы.");
        }
    }

    private async Task<EngineOperationResult> ApplyPresetAsync(EngineArgs args, CancellationToken ct)
    {
        string raw = args.Require("preset", """{"preset":"low-latency"}""");
        if (!EngineNames.TryParsePreset(raw, out TweakProfilePreset preset))
        {
            return EngineOperationResult.Failure(
                $"Пресет «{raw}» неизвестен. Доступные: {EngineNames.PresetsListRu}.");
        }

        if (preset is TweakProfilePreset.RestoreSnapshotDefault or TweakProfilePreset.CustomUserPreset)
        {
            return EngineOperationResult.Failure(
                $"Пресет «{raw}» ({EngineNames.PresetTitleRu(preset)}) применяется не через каталог: " +
                "для отката есть команда rollback, для своего набора — apply-tweaks со списком.");
        }

        bool restorePoint = args.GetBool("restorePoint", fallback: true);
        return await _operations.ApplyPresetAsync(preset, restorePoint, ct).ConfigureAwait(false);
    }

    private async Task<EngineOperationResult> ApplyTweaksAsync(EngineArgs args, CancellationToken ct)
    {
        IReadOnlyList<string> codes = args.RequireList(
            "tweaks",
            """{"tweaks":"TCP_NO_DELAY,GAME_MODE_ON"}""",
            MaxTweaksPerRequest);

        bool restorePoint = args.GetBool("restorePoint", fallback: true);
        return await _operations.ApplyTweaksAsync(codes, restorePoint, ct).ConfigureAwait(false);
    }


    private async Task<EngineOperationResult> RollbackSingleTweakAsync(EngineArgs args, CancellationToken ct)
    {
        string? tweak = args.Get("tweak")?.Trim();
        if (string.IsNullOrWhiteSpace(tweak))
        {
            return EngineOperationResult.Failure("Не указан код твика: укажите tweak — код из списка твиков, например TCP_NO_DELAY.");
        }

        string? snapshot = args.Get("snapshot")?.Trim();
        return await _operations.RollbackSingleTweakAsync(tweak, snapshot, ct).ConfigureAwait(false);
    }

    private async Task<EngineOperationResult> CleanupSnapshotsAsync(EngineArgs args, CancellationToken ct)
    {
        int keep = args.GetInt("keep", 10, 1, 1000);
        int days = args.GetInt("olderThanDays", 30, 1, 3650);
        // Явная передача аргументов по отсутствию — не по позиции, иначе keep уйдёт в olderThanDays
        return await _operations.CleanupSnapshotsAsync(keepLast: keep, olderThanDays: days, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Восстановление базы из копии (задача U). Подтверждение читается буквально: «yes», «true» или
    /// «1». Любое другое значение — отказ, а не «ладно, раз просили».
    /// </summary>
    private async Task<EngineOperationResult> RestoreDatabaseAsync(EngineArgs args, CancellationToken ct)
    {
        string? raw = args.Get("confirm")?.Trim();
        bool confirmed = raw is not null
            && (raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw == "1");

        return await _operations.RestoreDatabaseAsync(confirmed, ct).ConfigureAwait(false);
    }

    private async Task<EngineOperationResult> SetPerfProfileAsync(EngineArgs args, CancellationToken ct)
    {
        string raw = args.Require("profile", """{"profile":"economy"}""");
        if (!EngineNames.TryParseProfile(raw, out PerfProfile profile))
        {
            return EngineOperationResult.Failure(
                $"Профиль потребления «{raw}» неизвестен. Доступные: {EngineNames.ProfilesListRu}.");
        }

        return await _operations.SetPerfProfileAsync(profile, ct).ConfigureAwait(false);
    }
}
