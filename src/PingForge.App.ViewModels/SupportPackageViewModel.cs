using PingForge.Core.Diagnostics;

namespace PingForge.App.ViewModels;

/// <summary>Строка списка «что попадёт в пакет».</summary>
public sealed class SupportPackageRowViewModel : ObservableObject
{
    public SupportPackageRowViewModel(string nameRu, string descriptionRu, string sizeRu, bool included, string reasonRu, string truncatedNoteRu)
    {
        NameRu = nameRu;
        DescriptionRu = descriptionRu;
        SizeRu = sizeRu;
        Included = included;
        ReasonRu = reasonRu;
        TruncatedNoteRu = truncatedNoteRu;
    }

    public string NameRu { get; }
    public string DescriptionRu { get; }
    public string SizeRu { get; }
    public bool Included { get; }
    public string ReasonRu { get; }
    public string TruncatedNoteRu { get; }
    public string StatusRu => Included ? "войдёт в пакет" : $"не войдёт: {ReasonRu}";
}

/// <summary>
/// Модель «пакета для поддержки» для раздела «О программе».
/// Сначала показывает список, затем собирает архив по показанному плану.
/// </summary>
public sealed class SupportPackageViewModel : ObservableObject
{
    private const string NotPreparedRu = "Пакет собирается только по кнопке: до этого на диск ничего не пишется и никуда не отправляется.";

    private readonly Func<SupportPackagePlan?> _planFactory;
    private readonly Func<SupportPackagePlan, SupportPackageBuildResult> _build;
    private SupportPackagePlan? _plan;
    private IReadOnlyList<SupportPackageRowViewModel> _rows = [];
    private string _statusRu = NotPreparedRu;
    private string? _pathRu;
    private bool _hasPreview;

    /// <summary>Боевой конструктор: план и сборка — настоящие, файлы лежат там, где их кладёт программа.</summary>
    public SupportPackageViewModel()
        : this(SupportPackageFactory.CreatePlan, SupportPackageBuilder.Build)
    {
    }

    /// <summary>Конструктор для тестов и самопроверки.</summary>
    public SupportPackageViewModel(
        Func<SupportPackagePlan?> planFactory,
        Func<SupportPackagePlan, SupportPackageBuildResult> build)
    {
        _planFactory = planFactory ?? throw new ArgumentNullException(nameof(planFactory));
        _build = build ?? throw new ArgumentNullException(nameof(build));

        PrepareCommand = new RelayCommand(Prepare);
        BuildCommand = new RelayCommand(Build, () => CanBuild);
    }

    /// <summary>Первый шаг: прочитать источники и показать список. Ничего не пишет.</summary>
    public RelayCommand PrepareCommand { get; }

    /// <summary>Второй шаг: записать архив по уже показанному плану.</summary>
    public RelayCommand BuildCommand { get; }

    /// <summary>Строки экрана «что попадёт в пакет». Пусто до первого нажатия.</summary>
    public IReadOnlyList<SupportPackageRowViewModel> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    /// <summary>Что сказать человеку прямо сейчас — на странице, а не модальным окном.</summary>
    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>Где лежит готовый пакет. null — ещё не собран.</summary>
    public string? PathRu
    {
        get => _pathRu;
        private set
        {
            if (Set(ref _pathRu, value))
            {
                OnPropertyChanged(nameof(HasBuiltPackage));
            }
        }
    }

    /// <summary>Показан ли список того, что попадёт в архив.</summary>
    public bool HasPreview
    {
        get => _hasPreview;
        private set
        {
            if (Set(ref _hasPreview, value))
            {
                OnPropertyChanged(nameof(CanBuild), nameof(WarningRu), nameof(SummaryRu));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>Можно ли собирать: список показан и в нём есть что класть.</summary>
    public bool CanBuild => HasPreview && _plan is { HasSomething: true, FitsLimit: true };

    /// <summary>Собран ли архив: по нему интерфейс показывает путь.</summary>
    public bool HasBuiltPackage => !string.IsNullOrWhiteSpace(PathRu);

    /// <summary>Предупреждение о содержимом — показывается рядом с кнопкой сборки.</summary>
    public string? WarningRu => HasPreview ? _plan?.WarningRu : null;

    /// <summary>Сводка: сколько файлов и сколько это весит.</summary>
    public string? SummaryRu => HasPreview ? _plan?.SummaryRu : null;

    /// <summary>
    /// Составить план и показать его. Ошибка чтения источника не валит экран: пункт просто
    /// не попадёт в пакет со своей причиной.
    /// </summary>
    public void Prepare()
    {
        PathRu = null;

        SupportPackagePlan? plan;
        try
        {
            plan = _planFactory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _plan = null;
            Rows = [];
            HasPreview = false;
            StatusRu = $"Показать, что попадёт в пакет, не получилось: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (plan is null)
        {
            _plan = null;
            Rows = [];
            HasPreview = false;
            StatusRu = "Список не составился: источники не заданы.";
            return;
        }

        _plan = plan;
        Rows = plan.Items
            .Select(item => new SupportPackageRowViewModel(
                item.NameRu,
                item.DescriptionRu,
                item.SizeRu,
                item.Included,
                item.ReasonRu,
                item.TruncatedNoteRu))
            .ToList();

        HasPreview = true;
        StatusRu = plan.HasSomething
            ? $"{plan.SummaryRu} Проверьте список и нажмите «Собрать пакет» — до этого на диск ничего не пишется."
            : "В пакет нечего положить: ни журналов, ни сведений. Собирать нечего.";
    }

    /// <summary>Собрать архив по показанному плану.</summary>
    public void Build()
    {
        if (_plan is null)
        {
            StatusRu = "Сначала покажите список: пакет собирается по нему, а не наугад.";
            return;
        }

        SupportPackageBuildResult result;
        try
        {
            result = _build(_plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Кнопка не имеет права уронить окно: та же беда, что и «сборка не удалась».
            result = SupportPackageBuildResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }

        if (!result.Built)
        {
            PathRu = null;
            StatusRu = $"Пакет не собрался: {result.ProblemRu}";
            return;
        }

        PathRu = result.PathRu;
        StatusRu = $"Пакет собран: файлов {result.FilesIncluded}. Лежит здесь: {result.PathRu}. " +
                   "Сам он никуда не отправляется — отправлять вам.";
    }

    private void RaiseCommandStates()
    {
        BuildCommand.RaiseCanExecuteChanged();
    }
}
