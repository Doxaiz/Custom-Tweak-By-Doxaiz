using PingForge.Analysis;
using PingForge.Core.Models;

namespace PingForge.App.ViewModels;

/// <summary>Отчёт «Проверка настроек»: предупреждения сети + разбор каталога твиков.</summary>
public sealed record SettingsCheckReport(
    NetworkAnalysisReport Network,
    TweakAnalysisReport Tweaks,
    bool DataCollected,
    string? ProblemRu);

/// <summary>
/// Точка входа проверки настроек для интерфейса. Приложение собирает её из готовых анализаторов
/// PingForge.Analysis (<see cref="NetworkAnalyzer"/> для конфликтов окружения и
/// <see cref="TweakAnalyzer"/> для разбора твиков) — код не переписывается, только подключается.
/// </summary>
public interface ISettingsCheckAnalysis
{
    /// <summary>Пройти проверку. Только чтение и замеры по нажатию: система не меняется.</summary>
    Task<SettingsCheckReport> CheckAsync(CancellationToken ct = default);
}

/// <summary>Один пункт результата проверки: что мешает или что стоит изменить, с кодами твиков.</summary>
public sealed class SettingsCheckItemViewModel
{
    public SettingsCheckItemViewModel(
        string titleRu,
        string detailRu,
        string? tweakCode,
        string actionRu)
    {
        TitleRu = titleRu;
        DetailRu = detailRu;
        TweakCode = tweakCode;
        ActionRu = actionRu;
    }

    public string TitleRu { get; }

    public string DetailRu { get; }

    /// <summary>Код твика каталога, если пункт связан с твиком — по нему работает переход «показать в твиках».</summary>
    public string? TweakCode { get; }

    public string ActionRu { get; }

    public string LinkRu => TweakCode is { Length: > 0 } code
        ? $"Код: {code}"
        : "Твика в каталоге нет — действие вручную";
}

/// <summary>
/// Блок «Проверка настроек» в разделе «Настройки» (задача F): что мешает (конфликтующее окружение —
/// активный туннель, два подключения, непрочитанные поля), что стоит изменить (с кодами твиков)
/// и что не проверено. Ничего не применяет — только показывает и предлагает перейти к твику.
///
/// Правило честности: «данные не собраны» — это «не проверено», а не «всё хорошо». Пока проверка
/// не запускалась или упала — блок так и говорит.
/// </summary>
public sealed class SettingsCheckViewModel : ObservableObject
{
    private readonly ISettingsCheckAnalysis _analysis;
    private readonly Action<IReadOnlyList<string>> _showTweaks;
    private List<SettingsCheckItemViewModel> _interference = [];
    private List<SettingsCheckItemViewModel> _recommended = [];
    private IReadOnlyList<string> _skipped = [];
    private string _summaryRu = string.Empty;
    private string _statusRu = "Проверка ещё не запускалась: нажмите «Проверить настройки» — программа прочитает состояние и разберёт каталог. Ничего не меняется.";
    private bool _hasResult;
    private bool _isBusy;
    private bool _dataCollected;

    public SettingsCheckViewModel(ISettingsCheckAnalysis analysis, Action<IReadOnlyList<string>> showTweaks)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _showTweaks = showTweaks ?? throw new ArgumentNullException(nameof(showTweaks));

        CheckCommand = new AsyncRelayCommand(CheckAsync);
        ShowRecommendedTweaksCommand = new RelayCommand(ShowRecommended, () => HasResult && Recommended.Any(item => item.TweakCode is { Length: > 0 }));
    }

    public AsyncRelayCommand CheckCommand { get; }

    /// <summary>Показать в списке твиков те, что рекомендованы этой проверкой.</summary>
    public RelayCommand ShowRecommendedTweaksCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (Set(ref _hasResult, value))
            {
                ShowRecommendedTweaksCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Собраны ли данные: без этого итог не может звучать как «всё хорошо».</summary>
    public bool DataCollected
    {
        get => _dataCollected;
        private set => Set(ref _dataCollected, value);
    }

    /// <summary>Что мешает: конфликтующее окружение с кодами предупреждений.</summary>
    public IReadOnlyList<SettingsCheckItemViewModel> Interference
    {
        get => _interference;
        private set => Set(ref _interference, [.. value]);
    }

    /// <summary>Что стоит изменить: код твика, текущее и рекомендуемое значение.</summary>
    public IReadOnlyList<SettingsCheckItemViewModel> Recommended
    {
        get => _recommended;
        private set
        {
            if (Set(ref _recommended, [.. value]))
            {
                ShowRecommendedTweaksCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Не проверено — с причинами. Это не «пропущено за ненадобностью», это честный пробел.</summary>
    public IReadOnlyList<string> SkippedChecks
    {
        get => _skipped;
        private set => Set(ref _skipped, value);
    }

    public string SummaryRu
    {
        get => _summaryRu;
        private set => Set(ref _summaryRu, value);
    }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>Числа для подписи: всё считается из отчётов, ни одного «примерно».</summary>
    public string CountsRu => HasResult
        ? $"Мешает: {Interference.Count}. Предложено твиков: {Recommended.Count(item => item.TweakCode is { Length: > 0 })}. Не проверено: {SkippedChecks.Count}."
        : "Проверка ещё не запускалась.";

    public async Task CheckAsync()
    {
        IsBusy = true;
        StatusRu = "Читаю состояние сети и разбираю каталог твиков… (советы по MTU и DNS включают замеры: несколько секунд)";
        try
        {
            SettingsCheckReport report = await _analysis.CheckAsync().ConfigureAwait(true);

            DataCollected = report.DataCollected;
            Interference = BuildInterference(report).ToList();
            Recommended = BuildRecommended(report).ToList();
            SkippedChecks = BuildSkipped(report).ToList();
            SummaryRu = BuildSummary(report);
            HasResult = true;
            StatusRu = report.ProblemRu is { Length: > 0 }
                ? $"Проверка закончилась с ограничением: {report.ProblemRu} Ничего не применено."
                : "Проверка закончена. Ничего не применено — переходите к твику и решайте сами.";
            OnPropertyChanged(nameof(CountsRu));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сбой — это «не проверено», а не «всё хорошо» и не «находок нет».
            DataCollected = false;
            HasResult = false;
            StatusRu = $"Проверка не удалась: {ex.Message}. Настройки не менялись — это «не проверено», а не «всё в порядке».";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowRecommended()
    {
        IReadOnlyList<string> codes = Recommended
            .Where(item => item.TweakCode is { Length: > 0 })
            .Select(item => item.TweakCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count > 0)
        {
            _showTweaks(codes);
        }
    }

    private static IEnumerable<SettingsCheckItemViewModel> BuildInterference(SettingsCheckReport report)
    {
        foreach (InterferenceWarning warning in report.Network.Warnings)
        {
            yield return new SettingsCheckItemViewModel(
                $"{warning.TitleRu} (код {warning.Code})",
                $"{warning.DescriptionRu} {warning.RemediationRu}",
                tweakCode: null,
                actionRu: "Проверить вручную по описанию выше");
        }
    }

    private static IEnumerable<SettingsCheckItemViewModel> BuildRecommended(SettingsCheckReport report)
    {
        foreach (TweakRecommendationItem item in report.Tweaks.RecommendedToChange)
        {
            yield return new SettingsCheckItemViewModel(
                $"{item.TitleRu} ({item.TweakCode})",
                $"{item.ReasonRu} {item.CurrentValueDisplay} → рекомендуется: {item.RecommendedValueDisplay}.",
                item.TweakCode,
                actionRu: "Показать в твиках");
        }

        foreach (TweakRecommendationItem item in report.Network.Recommendations)
        {
            yield return new SettingsCheckItemViewModel(
                $"{item.TitleRu} ({item.TweakCode})",
                $"{item.ReasonRu} {item.CurrentValueDisplay} → рекомендуется: {item.RecommendedValueDisplay}.",
                item.TweakCode,
                actionRu: "Показать в твиках");
        }
    }

    private static IReadOnlyList<string> BuildSkipped(SettingsCheckReport report)
    {
        var skipped = new List<string>(report.Network.SkippedChecks);

        if (!report.Tweaks.AdapterResolved)
        {
            skipped.Add("Твики сетевой карты: активный адаптер не определён — проверка этих твиков пропущена, применять «наугад» нельзя.");
        }

        if (!report.DataCollected)
        {
            skipped.Add("Данные о сети собраны не полностью: часть проверок не выполнялась, поэтому итог — «не проверено», а не «всё хорошо».");
        }

        return skipped;
    }

    /// <summary>
    /// Итог без обмана: «всё хорошо» можно сказать только когда данные собраны, помех нет,
    /// рекомендаций нет и ни одна проверка не пропущена. Любое «но» попадает в текст.
    /// </summary>
    private string BuildSummary(SettingsCheckReport report)
    {
        if (!DataCollected)
        {
            return "Проверено не всё: данные о сети собраны не полностью, поэтому вывод «всё хорошо» был бы враньём.";
        }

        int alreadyOptimal = report.Tweaks.AlreadyOptimal.Count;
        int doNotTouch = report.Tweaks.DoNotTouch.Count;
        int skipped = SkippedChecks.Count;

        if (Interference.Count == 0 && Recommended.Count == 0 && skipped == 0)
        {
            return $"Помех не найдено, менять нечего: {alreadyOptimal} твиков уже в рекомендованном состоянии, {doNotTouch} не трогаем по вердикту каталога.";
        }

        var parts = new List<string>();
        if (Interference.Count > 0)
        {
            parts.Add($"мешает: {Interference.Count}");
        }

        if (Recommended.Count > 0)
        {
            parts.Add($"предложено изменений: {Recommended.Count}");
        }

        if (skipped > 0)
        {
            parts.Add($"не проверено: {skipped}");
        }

        parts.Add($"уже оптимально: {alreadyOptimal}");
        parts.Add($"не трогаем: {doNotTouch}");
        return "Итог: " + string.Join(", ", parts) + ".";
    }
}
