using System.Globalization;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Tweaks;

namespace PingForge.Analysis;

/// <summary>Результат анализа каталога относительно текущего состояния машины.</summary>
public sealed record TweakAnalysisReport(
    TweakTarget ActiveAdapterTarget,
    bool AdapterResolved,
    IReadOnlyList<TweakRecommendationItem> AlreadyOptimal,
    IReadOnlyList<TweakRecommendationItem> RecommendedToChange,
    IReadOnlyList<TweakRecommendationItem> DoNotTouch,
    IReadOnlyList<string> Notes);

/// <summary>
/// Строит «клетку» твиков для окна анализа: что уже оптимально, что стоит изменить,
/// а что программа трогать не будет. Все три числа берутся из фактического чтения системы,
/// поэтому отчёт не может обещать то, чего не проверил.
/// </summary>
public sealed class TweakAnalyzer
{
    private readonly ITweakCatalog _catalog;
    private readonly TargetValueResolver _resolver;

    public TweakAnalyzer(ITweakCatalog catalog, TargetValueResolver? resolver = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _resolver = resolver ?? new TargetValueResolver();
    }

    public async Task<TweakAnalysisReport> AnalyzeAsync(
        TweakTarget? activeAdapterTarget,
        CancellationToken ct = default)
    {
        var already = new List<TweakRecommendationItem>();
        var change = new List<TweakRecommendationItem>();
        var leave = new List<TweakRecommendationItem>();
        var notes = new List<string>();

        bool adapterResolved = activeAdapterTarget is not null && activeAdapterTarget.IsAdapterBound;
        if (!adapterResolved)
        {
            notes.Add("Активный адаптер не определён. Твики, привязанные к сетевой карте " +
                      "(RSS, ECN, буферы, Interrupt Moderation), пропущены — их нельзя применять «наугад».");
        }

        foreach (ITweak tweak in _catalog.GetAllTweaks())
        {
            ct.ThrowIfCancellationRequested();

            TweakDescriptor d = tweak.Descriptor;
            (TweakTarget target, TweakSupport support) = await tweak
                .ResolveTargetAsync(ct).ConfigureAwait(false);

            if (!support.IsSupported)
            {
                leave.Add(Keep(d, support.RequiresManualVerification, "—", support.ReasonRu));
                continue;
            }

            if (target.IsAdapterBound && !adapterResolved)
            {
                leave.Add(Keep(d, support.RequiresManualVerification, "—",
                    "Твик привязан к конкретной сетевой карте, а активный адаптер не определён."));
                continue;
            }

            CapturedParameterState captured = await tweak.CaptureAsync(target, ct).ConfigureAwait(false);
            if (captured.IsUnparsableOutput)
            {
                leave.Add(Keep(d, false, Display(captured.CapturedRawValue),
                    $"Вывод системы не разобран ({captured.SupportReasonRu}) — менять вслепую нельзя."));
                continue;
            }

            TargetValueResolution resolution = await _resolver
                .ResolveAsync(tweak, target, ct).ConfigureAwait(false);

            if (!resolution.IsAvailable)
            {
                leave.Add(Keep(d, false, Display(captured.ExistedBefore ? captured.CapturedRawValue : null),
                    resolution.ExplanationRu));
                continue;
            }

            string currentText = captured.ExistedBefore ? Display(captured.CapturedRawValue) : "(не задано)";
            string targetText = Display(resolution.Value);
            bool same = captured.ExistedBefore
                        && string.Equals(captured.CapturedRawValue, resolution.Value, StringComparison.OrdinalIgnoreCase);

            if (same)
            {
                already.Add(new TweakRecommendationItem(
                    d.Code, d.TitleRu, d.Badge, support.RequiresManualVerification || d.RequiresManualVerification,
                    currentText, targetText, "Уже стоит рекомендованное значение — менять нечего.", d.Risk));
                continue;
            }

            if (d.Risk == TweakRiskLevel.High)
            {
                leave.Add(Keep(d, true, currentText,
                    "Высокий риск: этот твик применяется только вручную и осознанно, автоподбор его не включает."));
                continue;
            }

            string reason = resolution.ExplanationRu;
            if (captured.ExistedBefore && captured.CapturedRawValue is null)
            {
                reason += " Текущее значение прочитать не удалось — будет зафиксировано как «не задано».";
            }

            change.Add(new TweakRecommendationItem(
                d.Code, d.TitleRu, d.Badge, support.RequiresManualVerification || d.RequiresManualVerification,
                currentText, targetText, reason, d.Risk));
        }

        if (change.Count == 0)
        {
            notes.Add("Изменять нечего: все доступные твики уже в рекомендованном состоянии.");
        }

        return new TweakAnalysisReport(
            ActiveAdapterTarget: activeAdapterTarget ?? TweakTarget.Global("no-adapter"),
            AdapterResolved: adapterResolved,
            AlreadyOptimal: already,
            RecommendedToChange: change,
            DoNotTouch: leave,
            Notes: notes);
    }

    private static TweakRecommendationItem Keep(TweakDescriptor d, bool manual, string currentDisplay, string reason) =>
        new(d.Code, d.TitleRu, d.Badge, manual || d.RequiresManualVerification, currentDisplay, "—", reason, d.Risk);

    private static string Display(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "(не задано)" : raw;

    internal static string FormatDouble(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture);
}
