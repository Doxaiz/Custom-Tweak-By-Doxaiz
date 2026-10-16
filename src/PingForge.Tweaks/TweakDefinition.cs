using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>Где физически живёт параметр. От этого зависит доступ и способ проверки.</summary>
public abstract record TweakValueSource
{
    public sealed record RegistryValue(
        RegistryRootKind Root,
        string SubKeyTemplate,
        string ValueName,
        TweakValueDataType DataType) : TweakValueSource;

    public sealed record NicAdvancedProperty(string Keyword) : TweakValueSource;

    public sealed record NetshTcpGlobal(string SettingKey) : TweakValueSource;

    public sealed record NetshSubinterfaceMtu : TweakValueSource;

    public sealed record NetshDnsServers : TweakValueSource;
}

/// <summary>
/// Описание твика в каталоге: всё, что нужно и интерфейсу, и движку применения.
/// Никакой логики — только данные, поэтому каталог можно проверять целиком автотестами.
/// </summary>
public sealed record TweakDefinition(
    string Code,
    string TitleRu,
    string TitleEn,
    TweakCategory Category,
    TweakRiskLevel Risk,
    GameRecommendationBadge Badge,
    TweakValueSource Source,
    string WhatItDoesRu,
    string GamingJustificationRu,
    string DefaultValueNoteRu,
    string RecommendedRawValue,
    TweakValueDataType DataType,
    int PingImpactDirection,
    int SpeedImpactDirection,
    int StabilityImpactDirection,
    bool IncludedInLowPingPreset,
    bool IncludedInMaxSpeedPreset,
    bool RebootRequired = false,
    bool EffectUnconfirmedOnModernWindows = false,
    bool IsComputedTarget = false,
    TweakFocus? Focus = null,
    bool IncludedInFpsPreset = false)
{
    /// <summary>
    /// Подкатегория для интерфейса. Если её не задали руками, она выводится из влияния твика:
    /// явное поле нужно там, где направление влияния не совпадает с целью («снять троттлинг сети»
    /// лежит в планировщике, но включают его ради пинга).
    /// </summary>
    public TweakFocus EffectiveFocus => Focus ?? Derive();

    private TweakFocus Derive()
    {
        if (PingImpactDirection < 0)
        {
            return TweakFocus.Ping;
        }

        if (Category == TweakCategory.InputLatency)
        {
            return TweakFocus.Input;
        }

        if (SpeedImpactDirection > 0)
        {
            return TweakFocus.Speed;
        }

        if (Category is TweakCategory.WindowsBackground or TweakCategory.PowerAndScheduler)
        {
            return TweakFocus.Fps;
        }

        return TweakFocus.System;
    }

    public string ResourceLocator => Source switch
    {
        TweakValueSource.RegistryValue r =>
            $"{(r.Root == RegistryRootKind.LocalMachine ? @"HKLM" : @"HKCU")}\\{r.SubKeyTemplate}\\{r.ValueName}",
        TweakValueSource.NicAdvancedProperty n => $"NIC∥{n.Keyword}",
        TweakValueSource.NetshTcpGlobal g => $"netsh int tcp global∥{g.SettingKey}",
        TweakValueSource.NetshSubinterfaceMtu => "netsh int ipv4 subinterface∥mtu",
        TweakValueSource.NetshDnsServers => "netsh int ipv4 dnsservers∥static",
        _ => Code
    };

    public TweakDescriptor ToDescriptor() => new(
        Code,
        TitleRu,
        TitleEn,
        Category,
        MechanismOf(Source),
        ResourceLocator,
        WhatItDoesRu,
        Badge,
        GamingJustificationRu,
        Risk,
        PingImpactDirection,
        SpeedImpactDirection,
        StabilityImpactDirection,
        RebootRequired,
        RequiresAdminPrivileges: true,
        RequiresManualVerification: EffectUnconfirmedOnModernWindows,
        DefaultValueNoteRu,
        RecommendedRawValue,
        DataType,
        IncludedInLowPingPreset,
        IncludedInMaxSpeedPreset)
    {
        HasComputedTarget = IsComputedTarget,
        EffectUnconfirmedOnModernWindows = EffectUnconfirmedOnModernWindows,
        IncludedInFpsPreset = IncludedInFpsPreset,
        FocusKind = EffectiveFocus
    };

    public static TweakMechanismKind MechanismOf(TweakValueSource source) => source switch
    {
        TweakValueSource.RegistryValue => TweakMechanismKind.Registry,
        TweakValueSource.NicAdvancedProperty => TweakMechanismKind.WmiNicAdvancedProperty,
        TweakValueSource.NetshTcpGlobal => TweakMechanismKind.NetshCommand,
        TweakValueSource.NetshSubinterfaceMtu => TweakMechanismKind.MtuSubinterface,
        TweakValueSource.NetshDnsServers => TweakMechanismKind.DnsResolverConfig,
        _ => TweakMechanismKind.Registry
    };
}
