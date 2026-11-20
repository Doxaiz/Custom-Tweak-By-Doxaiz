using PingForge.Analysis;
using PingForge.Core.Models;

namespace PingForge.App.ViewModels;

/// <summary>
/// Точка входа советника задержки ввода для интерфейса. Реализация в приложении оборачивает
/// готовый <see cref="InputLatencyAdvisor"/> из PingForge.Analysis — тот самый код, который
/// раньше был «мёртвым»: написан, покрыт тестами и не использовался ни окном, ни службой.
/// </summary>
public interface IInputLatencyAnalysis
{
    /// <summary>Пройти проверки задержки ввода. Только чтение: система не меняется.</summary>
    Task<NetworkAnalysisReport> CheckAsync(CancellationToken ct = default);
}

/// <summary>Одна находка карточки «Задержка ввода»: текст, приоритет и связь с твиком.</summary>
public sealed class InputLatencyFindingViewModel
{
    public InputLatencyFindingViewModel(
        string titleRu,
        string priorityRu,
        string explanationRu,
        string? tweakCode,
        string? tweakTitleRu,
        string noTweakReasonRu,
        string actionRu)
    {
        TitleRu = titleRu;
        PriorityRu = priorityRu;
        ExplanationRu = explanationRu;
        TweakCode = tweakCode;
        TweakTitleRu = tweakTitleRu;
        NoTweakReasonRu = noTweakReasonRu;
        ActionRu = actionRu;
    }

    public string TitleRu { get; }

    /// <summary>«высокий / средний / низкий» — из риска находки, а не «на глаз».</summary>
    public string PriorityRu { get; }

    public string ExplanationRu { get; }

    /// <summary>Код связанного твика каталога. null — твика нет, и это написано в <see cref="NoTweakReasonRu"/>.</summary>
    public string? TweakCode { get; }

    public string? TweakTitleRu { get; }

    /// <summary>Почему твика нет: «правится только заменой железа», «настройки роутера — вручную».</summary>
    public string NoTweakReasonRu { get; }

    /// <summary>Что делать: применить твик или действие руками (провод, настройки устройства).</summary>
    public string ActionRu { get; }

    /// <summary>Строка связи с твиком для карточки: «Твик: ИМЯ (КОД)» или честное «твика нет».</summary>
    public string LinkRu => TweakCode is { Length: > 0 } code
        ? $"Твик каталога: {TweakTitleRu} ({code})"
        : $"Твика в каталоге нет: {NoTweakReasonRu}";
}

/// <summary>
/// Карточка «Задержка ввода» в разделе «Твики» (задача F). Ничего не применяет: показывает находки
/// с приоритетом и объяснением, каждая находка связана с твиком каталога — или честно сказано,
/// что твика нет и почему. Кнопка «Показать связанные твики» отфильтровывает список ниже.
/// </summary>
public sealed class InputLatencyViewModel : ObservableObject
{
    private readonly IInputLatencyAnalysis _analysis;
    private readonly Action<IReadOnlyList<string>> _showTweaks;
    private List<InputLatencyFindingViewModel> _findings = [];
    private IReadOnlyList<string> _skipped = [];
    private IReadOnlyList<string> _notes = [];
    private string _statusRu = "Проверка ещё не запускалась: нажмите «Проверить задержку ввода» — программа прочитает устройства и текущие значения. Ничего не меняется.";
    private string _summaryRu = string.Empty;
    private bool _hasResult;
    private bool _isBusy;

    public InputLatencyViewModel(IInputLatencyAnalysis analysis, Action<IReadOnlyList<string>> showTweaks)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _showTweaks = showTweaks ?? throw new ArgumentNullException(nameof(showTweaks));

        CheckCommand = new AsyncRelayCommand(CheckAsync);
        ShowRelatedTweaksCommand = new RelayCommand(ShowRelatedTweaks, () => HasResult && RelatedTweakCodes.Count > 0);
    }

    public AsyncRelayCommand CheckCommand { get; }

    /// <summary>Показать в списке твиков только те, что относятся к задержке ввода.</summary>
    public RelayCommand ShowRelatedTweaksCommand { get; }

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
                ShowRelatedTweaksCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<InputLatencyFindingViewModel> Findings
    {
        get => _findings;
        private set => Set(ref _findings, [.. value]);
    }

    /// <summary>Проверки, которые честно пропущены, с причиной: «не знаем» ≠ «всё хорошо».</summary>
    public IReadOnlyList<string> SkippedChecks
    {
        get => _skipped;
        private set => Set(ref _skipped, value);
    }

    public IReadOnlyList<string> Notes
    {
        get => _notes;
        private set => Set(ref _notes, value);
    }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public string SummaryRu
    {
        get => _summaryRu;
        private set => Set(ref _summaryRu, value);
    }

    /// <summary>Есть ли хоть одна находка с твитом: кнопка «Показать связанные твики» без них не имеет смысла.</summary>
    public bool HasTweakLinks => Findings.Any(finding => finding.TweakCode is { Length: > 0 });

    /// <summary>Коды твиков, упомянутых в находках, — без дублей, в порядке появления.</summary>
    public IReadOnlyList<string> RelatedTweakCodes =>
        Findings
            .Where(finding => finding.TweakCode is { Length: > 0 })
            .Select(finding => finding.TweakCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Числа для подписи карточки: находок N, из них с твиком M. Считается, а не пишется.</summary>
    public string CountsRu => HasResult
        ? $"Находок: {Findings.Count}, из них правятся твиками: {Findings.Count(f => f.TweakCode is { Length: > 0 })}. Пропущено проверок: {SkippedChecks.Count}."
        : "Проверка ещё не запускалась.";

    public async Task CheckAsync()
    {
        IsBusy = true;
        StatusRu = "Читаю устройства ввода и текущие значения Windows…";
        try
        {
            NetworkAnalysisReport report = await _analysis.CheckAsync().ConfigureAwait(true);

            Findings = BuildFindings(report).ToList();
            SkippedChecks = report.SkippedChecks;
            Notes = report.Notes;
            SummaryRu = report.SummaryRu;
            HasResult = true;
            StatusRu = Findings.Count == 0
                ? "Находок нет: всё, что программа умеет проверить, уже в порядке или не применимо."
                : "Проверка закончена. Ничего не применено — переходите к твикам и решайте сами.";
            OnPropertyChanged(nameof(HasTweakLinks), nameof(RelatedTweakCodes), nameof(CountsRu));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сбой проверки — не «находок нет», а «проверить не удалось». Это разные ответы.
            StatusRu = $"Проверка не удалась: {ex.Message}. Настройки не менялись.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowRelatedTweaks()
    {
        IReadOnlyList<string> codes = RelatedTweakCodes;
        if (codes.Count > 0)
        {
            _showTweaks(codes);
        }
    }

    /// <summary>
    /// Находки из отчёта советника: предупреждения (железо и окружение) и предложения (твики).
    /// У каждой — приоритет из риска и связь с твиком либо честное объяснение его отсутствия.
    /// </summary>
    private static IEnumerable<InputLatencyFindingViewModel> BuildFindings(NetworkAnalysisReport report)
    {
        foreach (InterferenceWarning warning in report.Warnings)
        {
            // Предупреждения советника ввода — про железо и окружение: беспроводная мышь,
            // Wi-Fi, роутер. Твика для них в каталоге нет, и это написано прямо.
            yield return new InputLatencyFindingViewModel(
                warning.TitleRu,
                PriorityOf(warning.Severity),
                warning.DescriptionRu,
                tweakCode: null,
                tweakTitleRu: null,
                noTweakReasonRu: "это железо или настройки за пределами Windows — правки реестра здесь не помогают",
                actionRu: warning.RemediationRu);
        }

        foreach (TweakRecommendationItem recommendation in report.Recommendations)
        {
            yield return new InputLatencyFindingViewModel(
                recommendation.TitleRu,
                PriorityOf(recommendation.Risk),
                recommendation.ReasonRu,
                recommendation.TweakCode,
                recommendation.TitleRu,
                noTweakReasonRu: string.Empty,
                actionRu: $"{recommendation.CurrentValueDisplay}. Рекомендуется: {recommendation.RecommendedValueDisplay}.");
        }
    }

    private static string PriorityOf(TweakRiskLevel risk) => risk switch
    {
        TweakRiskLevel.High => "высокий",
        TweakRiskLevel.Medium => "средний",
        _ => "низкий",
    };
}
