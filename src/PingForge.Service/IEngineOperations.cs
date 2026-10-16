using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;

namespace PingForge.Service;

/// <summary>
/// Что служба умеет делать по существу — без канала, кадров и прав. Разделение не для красоты:
/// так «мозги» службы (снапшот → применение → проверка → откат) проверяются тестами без Windows,
/// а платформенная часть остаётся тонким слоем поверх этой границы.
/// </summary>
public interface IEngineOperations
{
    /// <summary>Доступны ли операции, меняющие систему: служба под LocalSystem или клиент с правами администратора.</summary>
    bool IsElevationAvailable { get; }

    /// <summary>Проверка связи: версия протокола, режим прав, версия приложения.</summary>
    Task<EngineOperationResult> PingAsync(CancellationToken ct = default);

    /// <summary>Список команд с описанием — тем же, что показывает интерфейс.</summary>
    Task<EngineOperationResult> CapabilitiesAsync(CancellationToken ct = default);

    /// <summary>Состояние: идёт ли оптимизация, какая игра, профиль потребления, сколько изменений висит.</summary>
    Task<EngineOperationResult> StatusAsync(CancellationToken ct = default);

    /// <summary>Применить пресет: снапшот, применение, проверка каждого твика.</summary>
    Task<EngineOperationResult> ApplyPresetAsync(TweakProfilePreset preset, bool requestRestorePoint, CancellationToken ct = default);

    /// <summary>Применить выбранные твики по кодам.</summary>
    Task<EngineOperationResult> ApplyTweaksAsync(IReadOnlyList<string> tweakCodes, bool requestRestorePoint, CancellationToken ct = default);

    /// <summary>Вернуть настройки из снапшота (или из последнего, если номер не задан).</summary>
    Task<EngineOperationResult> RollbackAsync(string? snapshotId, CancellationToken ct = default);

    /// <summary>Починить сеть: снять свои остатки после аварии.</summary>
    Task<EngineOperationResult> RepairNetworkAsync(CancellationToken ct = default);

    /// <summary>Последние записи журнала.</summary>
    Task<EngineOperationResult> JournalTailAsync(int limit, CancellationToken ct = default);

    /// <summary>Запустить или остановить живую оптимизацию.</summary>
    Task<EngineOperationResult> SetRealtimeAsync(bool start, CancellationToken ct = default);

    /// <summary>Сменить профиль потребления ресурсов.</summary>
    Task<EngineOperationResult> SetPerfProfileAsync(PerfProfile profile, CancellationToken ct = default);
}
