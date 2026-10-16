using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

// Источники «умных» значений. Реализации живут в приложении (там, где есть доступ к замерам),
// а каталог только спрашивает совет и обязан показать пользователю объяснение этого совета.

/// <summary>Рекомендованный MTU для конкретного адаптера: считается DF-пробами до игрового сервера.</summary>
public interface IMtuAdviceProvider
{
    Task<ComputedTargetValue> GetRecommendedMtuAsync(TweakTarget target, CancellationToken ct = default);
}

/// <summary>Победители бенчмарка DNS. Возвращает список адресов через «|» вместе с объяснением.</summary>
public interface IDnsAdviceProvider
{
    Task<ComputedTargetValue> GetRecommendedDnsAsync(TweakTarget target, CancellationToken ct = default);
}

/// <summary>Допустимые значения дополнительного свойства NIC — нужно, чтобы выбрать максимум буферов.</summary>
public interface INicPropertyOptionProvider
{
    Task<IReadOnlyList<string>> GetAllowedValuesAsync(TweakTarget target, string keyword, CancellationToken ct = default);
}
