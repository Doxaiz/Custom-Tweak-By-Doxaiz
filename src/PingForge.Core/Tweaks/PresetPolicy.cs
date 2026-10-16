using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Core.Tweaks;

/// <summary>
/// Инвариант пресетов. Раньше в пресет «Низкий пинг» попадал твик с бейджем Caution,
/// а в «Максимальную скорость» — твик, который эту скорость снижает. Теперь это невозможно.
/// </summary>
public static class PresetPolicy
{
    public static bool IsAllowedInPreset(TweakDescriptor descriptor, TweakProfilePreset preset)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (preset is TweakProfilePreset.RestoreSnapshotDefault or TweakProfilePreset.CustomUserPreset)
        {
            return true;
        }

        if (descriptor.Badge is GameRecommendationBadge.NotRecommended or GameRecommendationBadge.Caution)
        {
            return false;
        }

        return preset switch
        {
            // В пресет «низкий пинг» нельзя класть твик, ухудшающий пинг или стабильность.
            TweakProfilePreset.LowLatencyGaming =>
                descriptor.PingImpactDirection <= 0 && descriptor.StabilityImpactDirection >= 0,

            // В пресет «максимальная скорость» нельзя класть твик, снижающий скорость.
            TweakProfilePreset.MaxThroughput => descriptor.SpeedImpactDirection >= 0,

            // Пресет «отзывчивость и кадры»: питание, фон, планировщик и задержка ввода.
            // Твик, ухудшающий стабильность, сюда не попадает ни при каком бейдже.
            TweakProfilePreset.FpsAndResponsiveness =>
                descriptor.StabilityImpactDirection >= 0
                && descriptor.FocusKind is TweakFocus.Fps or TweakFocus.Input or TweakFocus.System,

            _ => false
        };
    }

    public static IReadOnlyList<ITweak> FilterForPreset(IEnumerable<ITweak> tweaks, TweakProfilePreset preset)
    {
        ArgumentNullException.ThrowIfNull(tweaks);
        return tweaks.Where(t => IsAllowedInPreset(t.Descriptor, preset)).ToList();
    }

    /// <summary>Проверка целостности каталога: используется в тестах и при старте службы.</summary>
    public static IReadOnlyList<string> ValidateCatalog(IEnumerable<ITweak> tweaks)
    {
        ArgumentNullException.ThrowIfNull(tweaks);

        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ITweak tweak in tweaks)
        {
            TweakDescriptor d = tweak.Descriptor;

            if (!seen.Add(d.Code))
            {
                problems.Add($"Дублирующийся код твика: {d.Code}");
            }

            if (d.IncludedInLowPingPreset && !IsAllowedInPreset(d, TweakProfilePreset.LowLatencyGaming))
            {
                problems.Add($"{d.Code}: помечен для пресета «низкий пинг», но не проходит инвариант пресета.");
            }

            if (d.IncludedInMaxSpeedPreset && !IsAllowedInPreset(d, TweakProfilePreset.MaxThroughput))
            {
                problems.Add($"{d.Code}: помечен для пресета «максимальная скорость», но не проходит инвариант пресета.");
            }

            if (d.IncludedInFpsPreset && !IsAllowedInPreset(d, TweakProfilePreset.FpsAndResponsiveness))
            {
                problems.Add($"{d.Code}: помечен для пресета «отзывчивость и кадры», но не проходит инвариант пресета.");
            }

            if (d.Badge == GameRecommendationBadge.NotRecommended && (d.IncludedInLowPingPreset || d.IncludedInMaxSpeedPreset))
            {
                problems.Add($"{d.Code}: бейдж «не рекомендую», но включён в авто-пресет.");
            }

            if (d.HasComputedTarget && !string.IsNullOrWhiteSpace(d.RecommendedRawValue))
            {
                problems.Add($"{d.Code}: значение объявлено вычисляемым, но задан статичный RecommendedRawValue.");
            }

            if (string.IsNullOrWhiteSpace(d.GamingJustificationRu))
            {
                problems.Add($"{d.Code}: отсутствует обоснование «стоит ли применять для игр».");
            }
        }

        return problems;
    }
}
