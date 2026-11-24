using PingForge.Probing.Access;

namespace PingForge.App.ViewModels;

/// <summary>
/// «Блокировки и доступ»: один замер по кнопке и честный вывод.
///
/// Чего здесь сознательно нет:
///   • никакого обхода блокировок — у программы нет ни туннеля, ни релея, ни перехвата пакетов,
///     и она не станет делать вид, что «включила обход»;
///   • никаких автоматических проверок при запуске: до нажатия кнопки не уходит ни один пакет;
///   • никаких обещаний «сайт откроется»: вывод говорит, что видно на этом пути, и что проверить дальше.
/// </summary>
public sealed class AccessCheckViewModel : ObservableObject
{
    /// <summary>Одна и та же честная оговорка и в интерфейсе, и в документации.</summary>
    public const string HonestNoteRu =
        "Программа не обходит блокировки: у неё нет туннеля, релея и перехвата пакетов. " +
        "Она делает один замер по кнопке и говорит, что именно видно на вашем пути до сайта.";

    private const string IdleRu =
        "Проверка не запускалась: нажмите «Проверить доступ» — уйдёт один обычный запрос, как у браузера.";

    private readonly IAccessProbe? _probe;

    private string _hostInput = "www.youtube.com";
    private string _statusRu = IdleRu;
    private string _resultTitleRu = string.Empty;
    private string _resultDetailRu = string.Empty;
    private string _nextStepRu = string.Empty;
    private bool _isBusy;
    private bool _hasResult;
    private bool _isProblem;

    public AccessCheckViewModel(IAccessProbe? probe)
    {
        _probe = probe;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => _probe is not null && !IsBusy);
    }

    /// <summary>Сборка без настоящего замера: честное «не подключена», а не кнопка-обманка.</summary>
    public static AccessCheckViewModel Unavailable()
    {
        var model = new AccessCheckViewModel(null);
        model.StatusRu = "Замер доступа в этой сборке не подключён: кнопка выключена, а не «нажми и ничего».";
        return model;
    }

    public AsyncRelayCommand CheckCommand { get; }

    /// <summary>Куда смотреть человеку. Расширения вроде «https://» здесь допустимы — они снимаются перед замером.</summary>
    public string HostInput
    {
        get => _hostInput;
        set => Set(ref _hostInput, value);
    }

    public string HonestTextRu => HonestNoteRu;

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    public string ResultTitleRu
    {
        get => _resultTitleRu;
        private set => Set(ref _resultTitleRu, value);
    }

    public string ResultDetailRu
    {
        get => _resultDetailRu;
        private set => Set(ref _resultDetailRu, value);
    }

    public string NextStepRu
    {
        get => _nextStepRu;
        private set => Set(ref _nextStepRu, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => Set(ref _hasResult, value);
    }

    /// <summary>Красным подсвечивается только настоящая проблема, а не «не проверяли».</summary>
    public bool IsProblem
    {
        get => _isProblem;
        private set => Set(ref _isProblem, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                CheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task CheckAsync()
    {
        if (_probe is null)
        {
            StatusRu = "Замер доступа в этой сборке не подключён.";
            return;
        }

        string host = LiveAccessProbe.NormalizeHost(HostInput);
        if (host.Length == 0)
        {
            StatusRu = "Впишите имя сайта, например www.youtube.com — без него проверять нечего.";
            return;
        }

        IsBusy = true;
        StatusRu = $"Проверяю {host}: DNS, TCP, TLS и, если понадобится, обычный HTTP…";
        try
        {
            AccessObservation observation = await _probe.ProbeAsync(host).ConfigureAwait(true);
            AccessDiagnosis diagnosis = AccessClassifier.Classify(observation);

            ResultTitleRu = diagnosis.TitleRu;
            ResultDetailRu = diagnosis.DetailRu;
            NextStepRu = diagnosis.NextStepRu;
            HasResult = true;
            IsProblem = diagnosis.IsProblem;
            StatusRu = diagnosis.TitleRu;
        }
        catch (Exception ex)
        {
            // Проба падать не должна, но если упала — это не «сайт заблокирован», а сбой замера.
            ResultTitleRu = "Замер не получился";
            ResultDetailRu = $"Программа не смогла выполнить проверку: {ex.GetType().Name}: {ex.Message}";
            NextStepRu = "Повторите замер. Если ошибка повторяется — приложите журнал из раздела «О программе».";
            HasResult = true;
            IsProblem = true;
            StatusRu = ResultTitleRu;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
