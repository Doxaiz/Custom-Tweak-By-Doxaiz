using System.IO;
using PingForge.Orchestration.Realtime;

namespace PingForge.App.ViewModels;

/// <summary>Для чего компьютер: от этого зависит, нужны ли игровые твики вообще.</summary>
public enum SetupPurpose
{
    Games,
    GamesAndWork,
    Work,
}

/// <summary>Насколько сильный компьютер — от этого зависит профиль потребления.</summary>
public enum SetupPcStrength
{
    Weak,
    Medium,
    Strong,
}

/// <summary>Что больше мешает: задержка, кадры или и то, и другое.</summary>
public enum SetupProblem
{
    Ping,
    Fps,
    Both,
}

/// <summary>
/// Что мастер советует после трёх вопросов. Всё поле — данные, а не текст: по ним видно,
/// что именно будет применено, и это же проверяют тесты.
/// </summary>
public sealed record SetupRecommendation(
    string? PresetWireName,
    string TitleRu,
    string WhyRu,
    PerfProfile Profile);

/// <summary>
/// Мастер первого запуска — «как в BoosterX»: приветствие, три простых вопроса, совет и одно нажатие.
///
/// Правила, которые здесь держатся:
///   • ничего не применяется само: сначала человек видит, что именно будет изменено;
///   • при выборе «для работы» игровые твики не предлагаются вовсе — честнее, чем «на всякий случай»;
///   • применение идёт тем же путём, что и пресет в разделе «Твики»: снапшот → подтверждение → отчёт;
///   • мастер показывается один раз; вернуться к нему можно из «Настроек».
///
/// Совет считается одной чистой функцией (<see cref="Recommend"/>), поэтому его можно проверить
/// таблицей, а не «на глаз» на живой Windows.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly IAppSettingsStore _store;
    private readonly TweaksViewModel _tweaks;
    private readonly SettingsViewModel _settings;
    private int _step;
    private SetupPurpose? _purpose;
    private SetupPcStrength? _strength;
    private SetupProblem? _problem;
    private string _hintRu = string.Empty;
    private string _resultRu = string.Empty;
    private bool _isApplying;

    public OnboardingViewModel(
        IAppSettingsStore store,
        TweaksViewModel tweaks,
        SettingsViewModel settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tweaks = tweaks ?? throw new ArgumentNullException(nameof(tweaks));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        StartCommand = new RelayCommand(Start);
        NextCommand = new RelayCommand(Next);
        BackCommand = new RelayCommand(Back);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsApplying);
        LaterCommand = new RelayCommand(CloseWithoutApplying);

        // Мастер не держит окно в заложниках: если настройки не сохранились, он говорит об этом
        // и даёт закрыть себя. Иначе человек без прав на папку настроек не смог бы работать вовсе.
        DismissProblemCommand = new RelayCommand(() =>
        {
            ResultRu = string.Empty;
            IsVisible = false;
        });

        AppSettings saved = _store.Load();
        IsVisible = !saved.SetupCompleted;
    }

    public RelayCommand StartCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>«Позже»: закрыть мастер, ничего не меняя. Выбор тоже запоминается — второй раз не спросим.</summary>
    public RelayCommand LaterCommand { get; }

    /// <summary>«Понятно»: закрыть мастер после сообщения (например, когда настройки не записались).</summary>
    public RelayCommand DismissProblemCommand { get; }

    /// <summary>Показывать ли мастер сейчас.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    private bool _isVisible;

    /// <summary>Шаг: 0 — приветствие, 1 — вопросы, 2 — совет.</summary>
    public int Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                OnPropertyChanged(nameof(IsWelcomeStep), nameof(IsQuestionsStep), nameof(IsRecommendationStep),
                    nameof(ApplyButtonRu), nameof(CanGoBack));
            }
        }
    }

    public bool IsWelcomeStep => Step == 0;

    public bool IsQuestionsStep => Step == 1;

    public bool IsRecommendationStep => Step == 2;

    public bool CanGoBack => Step > 0;

    public SetupRecommendation Recommended
    {
        get => _recommended;
        private set => Set(ref _recommended, value);
    }

    private SetupRecommendation _recommended =
        Recommend(SetupPurpose.Games, SetupPcStrength.Medium, SetupProblem.Ping);

    /// <summary>Что не так с ответами: пусто — можно идти дальше.</summary>
    public string HintRu
    {
        get => _hintRu;
        private set => Set(ref _hintRu, value);
    }

    /// <summary>Итог применения: «применено» или «не удалось» — словами службы.</summary>
    public string ResultRu
    {
        get => _resultRu;
        private set
        {
            if (Set(ref _resultRu, value))
            {
                OnPropertyChanged(nameof(HasResultRu));
            }
        }
    }

    /// <summary>Есть что сказать человеку прямо в мастере (в том числе «настройки не сохранились»).</summary>
    public bool HasResultRu => _resultRu.Length > 0;

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (Set(ref _isApplying, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Надпись на главной кнопке последнего шага.</summary>
    public string ApplyButtonRu => Recommended.PresetWireName is null ? "Сохранить настройки" : "Применить и закрыть";

    public SetupPurpose? Purpose
    {
        get => _purpose;
        set
        {
            if (Set(ref _purpose, value))
            {
                OnPropertyChanged(nameof(PurposeIsGames), nameof(PurposeIsGamesAndWork), nameof(PurposeIsWork));
            }
        }
    }

    public bool PurposeIsGames
    {
        get => Purpose == SetupPurpose.Games;
        set { if (value) { Purpose = SetupPurpose.Games; } }
    }

    public bool PurposeIsGamesAndWork
    {
        get => Purpose == SetupPurpose.GamesAndWork;
        set { if (value) { Purpose = SetupPurpose.GamesAndWork; } }
    }

    public bool PurposeIsWork
    {
        get => Purpose == SetupPurpose.Work;
        set { if (value) { Purpose = SetupPurpose.Work; } }
    }

    public SetupPcStrength? Strength
    {
        get => _strength;
        set
        {
            if (Set(ref _strength, value))
            {
                OnPropertyChanged(nameof(StrengthIsWeak), nameof(StrengthIsMedium), nameof(StrengthIsStrong));
            }
        }
    }

    public bool StrengthIsWeak
    {
        get => Strength == SetupPcStrength.Weak;
        set { if (value) { Strength = SetupPcStrength.Weak; } }
    }

    public bool StrengthIsMedium
    {
        get => Strength == SetupPcStrength.Medium;
        set { if (value) { Strength = SetupPcStrength.Medium; } }
    }

    public bool StrengthIsStrong
    {
        get => Strength == SetupPcStrength.Strong;
        set { if (value) { Strength = SetupPcStrength.Strong; } }
    }

    public SetupProblem? Problem
    {
        get => _problem;
        set
        {
            if (Set(ref _problem, value))
            {
                OnPropertyChanged(nameof(ProblemIsPing), nameof(ProblemIsFps), nameof(ProblemIsBoth));
            }
        }
    }

    public bool ProblemIsPing
    {
        get => Problem == SetupProblem.Ping;
        set { if (value) { Problem = SetupProblem.Ping; } }
    }

    public bool ProblemIsFps
    {
        get => Problem == SetupProblem.Fps;
        set { if (value) { Problem = SetupProblem.Fps; } }
    }

    public bool ProblemIsBoth
    {
        get => Problem == SetupProblem.Both;
        set { if (value) { Problem = SetupProblem.Both; } }
    }

    /// <summary>
    /// Совет по трём ответам. Одна функция без состояния — её и проверяют тесты.
    ///
    /// Логика нарочно простая, чтобы её можно было объяснить человеку:
    ///   • работа без игр — игровые твики не нужны, только профиль потребления;
    ///   • жалуется на пинг — пресет «Низкий пинг»;
    ///   • жалуется на кадры — пресет «Кадры и отзывчивость»;
    ///   • и то, и другое — на слабом компьютере сначала кадры (фон и питание дают больше),
    ///     на среднем и мощном — низкий пинг, потому что кадры у них обычно уже в порядке.
    /// </summary>
    public static SetupRecommendation Recommend(
        SetupPurpose purpose,
        SetupPcStrength strength,
        SetupProblem problem)
    {
        PerfProfile profile = strength switch
        {
            SetupPcStrength.Weak => PerfProfile.Economy,
            SetupPcStrength.Strong => PerfProfile.MaxPerformance,
            _ => PerfProfile.Balanced,
        };

        if (purpose == SetupPurpose.Work)
        {
            return new SetupRecommendation(
                PresetWireName: null,
                TitleRu: "Игровые твики не нужны",
                WhyRu: "Для работы важнее устойчивость и отсутствие сюрпризов. Мастер не тронет ни одной настройки Windows — " +
                       "только сохранит профиль потребления. Игровые твики всегда можно применить вручную в разделе «Твики».",
                Profile: profile);
        }

        return problem switch
        {
            SetupProblem.Ping => new SetupRecommendation(
                "low-latency",
                "Низкий пинг",
                "Жалоба на задержку — значит, берём пресет, который сокращает задержку и не портит устойчивость соединения. " +
                "Ничего, что мешает загрузке или работе, в него не входит.",
                profile),
            SetupProblem.Fps => new SetupRecommendation(
                "fps",
                "Кадры и отзывчивость",
                "Жалоба на рывки и отклик — значит, берём пресет про питание, фон, планировщик и ввод. " +
                "Сеть он не трогает, поэтому безопасен при любой игре.",
                profile),
            _ => strength == SetupPcStrength.Weak
                ? new SetupRecommendation(
                    "fps",
                    "Кадры и отзывчивость",
                    "На слабом компьютере при жалобах «и пинг, и кадры» больше даёт разгрузка фона и питание. " +
                    "Когда кадры перестанут рваться, пресет «Низкий пинг» можно применить следующим шагом.",
                    profile)
                : new SetupRecommendation(
                    "low-latency",
                    "Низкий пинг",
                    "Компьютер не слабый, поэтому сначала задержка: пресет сокращает её и не мешает ни загрузке, ни работе. " +
                    "Пресет про кадры можно применить следом, если рывки останутся.",
                    profile),
        };
    }

    public void Start()
    {
        Step = 1;
        HintRu = string.Empty;
    }

    public void Back()
    {
        if (Step > 0)
        {
            Step--;
            HintRu = string.Empty;
        }
    }

    public void Next()
    {
        if (Purpose is null || Strength is null || Problem is null)
        {
            HintRu = "Ответьте на все три вопроса — тогда совет будет по делу, а не «на всякий случай».";
            return;
        }

        Recommended = Recommend(Purpose.Value, Strength.Value, Problem.Value);
        Step = 2;
        HintRu = string.Empty;
    }

    /// <summary>
    /// Применить совет: профиль потребления — файлом настроек (это не меняет Windows),
    /// пресет — обычным путём с подтверждением и снапшотом.
    /// </summary>
    public async Task ApplyAsync()
    {
        IsApplying = true;
        ResultRu = string.Empty;

        try
        {
            _settings.Profile = Recommended.Profile;
            await _settings.SaveAsync().ConfigureAwait(true);

            if (Recommended.PresetWireName is { } wire)
            {
                PresetViewModel? preset = _tweaks.Presets.FirstOrDefault(item => item.WireName == wire);
                if (preset is not null)
                {
                    _tweaks.SelectedPreset = preset;
                    await _tweaks.ApplySelectedPresetAsync().ConfigureAwait(true);
                }
            }

            ResultRu = Recommended.PresetWireName is null
                ? "Профиль сохранён. Настройки Windows не менялись."
                : _tweaks.StatusRu;

            // Мастер закрывается, только если выбор действительно запомнен. Иначе он остаётся и
            // показывает причину: иначе человек не узнал бы, что мастер спросит его снова.
            if (MarkCompleted())
            {
                IsVisible = false;
            }
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Закрыть без изменений. Второй раз мастер не появится: выбор запоминается.</summary>
    public void CloseWithoutApplying()
    {
        if (MarkCompleted())
        {
            IsVisible = false;
        }
    }

    /// <summary>Открыть мастер снова — кнопка в «Настройках».</summary>
    public void Reopen()
    {
        Step = 0;
        HintRu = string.Empty;
        ResultRu = string.Empty;
        IsVisible = true;
    }

    /// <summary>Запомнить пройденный мастер. <c>false</c> — записать не удалось, и это сказано человеку.</summary>
    private bool MarkCompleted()
    {
        AppSettings current = _store.Load();
        string answers = $"{Purpose?.ToString() ?? "нет"};{Strength?.ToString() ?? "нет"};{Problem?.ToString() ?? "нет"}";

        try
        {
            _store.Save(current with { SetupCompleted = true, SetupAnswersRu = answers });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Без прав на папку настроек (обычный запуск без администратора) запись не пройдёт.
            // Раньше это исключение улетало из команды кнопки и закрывало программу отчётом о сбое:
            // первый же запуск выглядел как падение. Теперь мастер закрывается и говорит правду.
            ResultRu = $"Выбор не сохранился: {ex.Message}. Это права на папку настроек, а не сбой программы: " +
                       "мастер появится снова при следующем запуске, а применённое останется в журнале. " +
                       "Нажмите «Понятно», чтобы продолжить работу.";
            return false;
        }
    }
}
