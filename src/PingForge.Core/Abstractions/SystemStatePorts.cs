namespace PingForge.Core.Abstractions;

/// <summary>
/// Порт чтения состояния Windows и сети. Все поля — «может быть неизвестно»:
/// null означает «прочитать не удалось», и это честно показывается пользователю серым,
/// а не подменяется значением по умолчанию.
/// </summary>
public interface ISystemStateReader
{
    Task<SystemStateReading> ReadAsync(CancellationToken ct = default);
}

public sealed record SystemStateReading(
    string WindowsEditionAndBuild,
    bool IsRunningAsAdministrator,
    NicAdapterRef? ActiveAdapter,
    int? LinkSpeedMbps,
    int? CurrentMtuBytes,
    string? PowerSchemeGuid,
    string? PowerSchemeName,
    string? TcpAutoTuningLevel,
    string? CongestionProvider,
    bool? IsRssEnabled,
    bool? IsRscEnabled,
    bool? IsEcnEnabled,
    bool? IsNagleDisabledOnActiveNic,
    bool? IsGameModeEnabled,
    bool? IsDeliveryOptimizationP2pEnabled,
    bool? IsSystemRestoreEnabledOnSystemDrive,
    bool? IsTimerResolutionRaised,
    IReadOnlyList<string> ConfiguredDnsServers,
    IReadOnlyList<string> AttachedNdisFilters,
    IReadOnlyList<string> UnresolvedFields)
{
    public static SystemStateReading Empty(string reason) => new(
        WindowsEditionAndBuild: reason,
        IsRunningAsAdministrator: false,
        ActiveAdapter: null,
        LinkSpeedMbps: null,
        CurrentMtuBytes: null,
        PowerSchemeGuid: null,
        PowerSchemeName: null,
        TcpAutoTuningLevel: null,
        CongestionProvider: null,
        IsRssEnabled: null,
        IsRscEnabled: null,
        IsEcnEnabled: null,
        IsNagleDisabledOnActiveNic: null,
        IsGameModeEnabled: null,
        IsDeliveryOptimizationP2pEnabled: null,
        IsSystemRestoreEnabledOnSystemDrive: null,
        IsTimerResolutionRaised: null,
        ConfiguredDnsServers: Array.Empty<string>(),
        AttachedNdisFilters: Array.Empty<string>(),
        UnresolvedFields: new[] { "Состояние системы не прочитано." });
}
