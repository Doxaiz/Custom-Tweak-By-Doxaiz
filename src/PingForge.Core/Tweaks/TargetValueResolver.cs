using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Core.Tweaks;

/// <summary>
/// Определяет целевое значение для записи в снапшот: либо статичное из дескриптора,
/// либо вычисленное твиком (MTU, DNS, буферы NIC). Значение фиксируется в снапшоте
/// как planned_value — поэтому откат по-прежнему вернёт исходное состояние.
/// </summary>
public sealed class TargetValueResolver
{
    public async Task<TargetValueResolution> ResolveAsync(ITweak tweak, TweakTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        ArgumentNullException.ThrowIfNull(target);

        TweakDescriptor descriptor = tweak.Descriptor;

        if (tweak is IComputedTargetValueTweak computed)
        {
            ComputedTargetValue value = await computed.ComputeTargetValueAsync(target, ct).ConfigureAwait(false);
            return value.IsAvailable
                ? new TargetValueResolution(true, value.Value, value.DataType, value.ExplanationRu)
                : new TargetValueResolution(false, null, value.DataType, value.ExplanationRu);
        }

        if (string.IsNullOrWhiteSpace(descriptor.RecommendedRawValue))
        {
            return new TargetValueResolution(false, null, descriptor.DataType, "Для твика не задано целевое значение.");
        }

        return new TargetValueResolution(true, descriptor.RecommendedRawValue, descriptor.DataType, "Статичное рекомендуемое значение.");
    }
}

public sealed record TargetValueResolution(bool IsAvailable, string? Value, TweakValueDataType DataType, string ExplanationRu);
