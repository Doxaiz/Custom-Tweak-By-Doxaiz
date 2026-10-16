using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.Service;

/// <summary>
/// Разбор запроса и вызов нужной операции. Здесь же — единственное место, где проверяются права:
/// если команда меняет систему, а прав нет, дальше она не проходит. Ошибка никогда не «улетает» в канал:
/// клиент обязан получить ответ с текстом, который можно показать пользователю.
/// </summary>
public sealed class EngineDispatcher
{
    /// <summary>Предел числа твиков в одном запросе. В каталоге их 47, запас двойной — на «включить все» после роста каталога.</summary>
    public const int MaxTweaksPerRequest = 200;

    /// <summary>Сколько записей журнала отдаётся по умолчанию и максимум.</summary>
    public const int DefaultJournalLimit = 20;

    public const int MaxJournalLimit = 500;

    private readonly IEngineOperations _operations;

    public EngineDispatcher(IEngineOperations operations) =>
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    /// <summary>Полный путь: JSON по каналу → ответ. Пустой или битый запрос тоже получает ответ с текстом.</summary>
    public async Task<EngineResponse> HandleJsonAsync(string requestJson, CancellationToken ct = default)
    {
        if (!EngineJson.TryDecodeRequest(requestJson, out EngineRequest? request, out string errorRu))
        {
            // id неизвестен: отвечаем пустым, чтобы клиент не принял чужой ответ за свой.
            return EngineResponse.Failure(string.Empty, errorRu);
        }

        return await HandleAsync(request!, ct).ConfigureAwait(false);
    }

    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EngineCommandDescriptor descriptor = EngineCommandCatalog.Get(request.Command);

        if (descriptor.NeedsElevation && !_operations.IsElevationAvailable)
        {
            return EngineResponse.Failure(
                request.Id,
                $"Команда «{descriptor.WireName}» меняет настройки Windows, а сейчас нет прав администратора. " +
                "Запустите приложение от имени администратора или проверьте, что служба PingForge запущена.");
        }

        var args = new EngineArgs(request.Args, descriptor.WireName);

        try
        {
            EngineOperationResult result = request.Command switch
            {
                EngineCommandKind.Ping => await _operations.PingAsync(ct).ConfigureAwait(false),

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
