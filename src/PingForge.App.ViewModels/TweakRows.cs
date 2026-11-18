using PingForge.Core.Models;
using PingForge.Service.Protocol;

namespace PingForge.App.ViewModels;

/// <summary>Строка списка твиков в интерфейсе: имя, что включает, вердикт и состояние ползунка.</summary>
public sealed class TweakRowViewModel : ObservableObject
{
    private bool _isOn;

    public TweakRowViewModel(
        string code,
        string titleRu,
        string whatItDoesRu,
        string verdictRu,
        string verdictWhyRu,
        bool isRecommended,
        bool enabledByDefault,
        bool isInLowPingPreset,
        bool isInMaxSpeedPreset,
        bool isInFpsPreset,
        string focusRu,
        bool isDangerous)
    {
        Code = code;
        TitleRu = titleRu;
        WhatItDoesRu = whatItDoesRu;
        VerdictRu = verdictRu;
        VerdictWhyRu = verdictWhyRu;
        IsRecommended = isRecommended;
        EnabledByDefault = enabledByDefault;
        IsInLowPingPreset = isInLowPingPreset;
        IsInMaxSpeedPreset = isInMaxSpeedPreset;
        IsInFpsPreset = isInFpsPreset;
        FocusRu = focusRu;
        IsDangerous = isDangerous;
        _isOn = enabledByDefault;
    }

    public string Code { get; }

    public string TitleRu { get; }

    public string WhatItDoesRu { get; }

    /// <summary>Короткий вердикт: «включать: да» / «по желанию» / «осторожно» / «не советуем».</summary>
    public string VerdictRu { get; }

    public string VerdictWhyRu { get; }

    /// <summary>Рекомендовано для игр — по умолчанию включено именно это.</summary>
    public bool IsRecommended { get; }

    public bool EnabledByDefault { get; }

    public bool IsInLowPingPreset { get; }

    public bool IsInMaxSpeedPreset { get; }

    public bool IsInFpsPreset { get; }

    /// <summary>Подкатегория: «Пинг», «Скорость», «Кадры», «Ввод», «Система».</summary>
    public string FocusRu { get; }

    /// <summary>«Не советуем» — такие твики нельзя включить массовым выбором, только вручную и с предупреждением.</summary>
    public bool IsDangerous { get; }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }

    /// <summary>
    /// Вес вердикта для сортировки: 0 — «включать: да», 3 — «не советуем». Считается по тексту вердикта,
    /// а текст приходит из <see cref="PingForge.Tweaks.TweakVerdict"/>, поэтому окно не может завести
    /// свою шкалу полезности.
    /// </summary>
    public int VerdictRank => PingForge.Tweaks.TweakVerdict.Rank(VerdictRu);

    /// <summary>Названия пресетов, в которые входит твик: их видно прямо в карточке.</summary>
    public string PresetsLineRu
    {
        get
        {
            var names = new List<string>();
            if (IsInLowPingPreset)
            {
                names.Add("низкий пинг");
            }

            if (IsInMaxSpeedPreset)
            {
                names.Add("скорость");
            }

            if (IsInFpsPreset)
            {
                names.Add("кадры");
            }

            return names.Count == 0 ? "в авто-пресеты не входит" : "пресет: " + string.Join(", ", names);
        }
    }
}

/// <summary>
/// Кнопка-пилюля выбора порядка: подпись и пояснение берутся из <see cref="TweakOrdering"/>,
/// а нажатие уходит в модель представления через обратный вызов — выбранный режим видно на экране.
/// </summary>
public sealed class TweakOrderOptionViewModel : ObservableObject
{
    private readonly Action<TweakOrder> _onSelected;
    private bool _isSelected;

    public TweakOrderOptionViewModel(TweakOrder order, Action<TweakOrder> onSelected)
    {
        Order = order;
        _onSelected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
    }

    public TweakOrder Order { get; }

    public string TitleRu => TweakOrdering.TitleRu(Order);

    public string HintRu => TweakOrdering.HintRu(Order);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
            {
                _onSelected(Order);
            }
        }
    }

    /// <summary>Отметить режим выбранным без нажатия: так модель приводит пилюли в согласие с порядком.</summary>
    internal void MarkSelected(bool selected)
    {
        if (_isSelected != selected)
        {
            Set(ref _isSelected, selected, nameof(IsSelected));
        }
    }
}

/// <summary>
/// Пилюля быстрого фильтра: нажатие переключает условие отбора, а не выбирает одно из многих.
/// Поэтому это не «радиокнопка»: фильтров можно включить сразу несколько.
/// </summary>
public sealed class TweakFilterOptionViewModel : ObservableObject
{
    private readonly Action _onToggled;
    private bool _isOn;

    public TweakFilterOptionViewModel(TweakQuickFilter filter, Action onToggled)
    {
        Filter = filter;
        _onToggled = onToggled ?? throw new ArgumentNullException(nameof(onToggled));
    }

    public TweakQuickFilter Filter { get; }

    public string TitleRu => TweakQuickFilters.TitleRu(Filter);

    public string HintRu => TweakQuickFilters.HintRu(Filter);

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (Set(ref _isOn, value))
            {
                _onToggled();
            }
        }
    }
}

/// <summary>
/// Группа карточек в разделе «Твики»: заголовок, пояснение и сами строки. Число карточек и число
/// отмеченных считаются по строкам — подпись не может разойтись с ползунками.
/// </summary>
public sealed class TweakGroupViewModel : ObservableObject
{
    public TweakGroupViewModel(string titleRu, IReadOnlyList<TweakRowViewModel> rows, string hintRu = "")
    {
        TitleRu = titleRu;
        HintRu = hintRu;
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
    }

    public string TitleRu { get; }

    /// <summary>Что означает порядок внутри группы — одной строкой под заголовком.</summary>
    public string HintRu { get; }

    public IReadOnlyList<TweakRowViewModel> Rows { get; }

    public string CountRu => TweakOrdering.CountRu(Rows.Count, Rows.Count(row => row.IsOn));

    /// <summary>Пересчитать подпись, когда человек переключил ползунок в этой группе.</summary>
    public void RefreshCounts() => OnPropertyChanged(nameof(CountRu));
}

/// <summary>Строка журнала: что меняли, что было, что стало и подтвердила ли система.</summary>
public sealed record JournalRowViewModel(
    DateTime LoggedUtc,
    string TweakTitleRu,
    string TweakCode,
    string PreviousValueText,
    string AppliedValueText,
    string VerifiedActualText,
    bool Succeeded,
    bool RebootNeeded,
    string ExplanationRu)
{
    public string LineRu =>
        $"{LoggedUtc.ToLocalTime():dd.MM HH:mm} · {TweakTitleRu} ({TweakCode}): было «{PreviousValueText}» → " +
        $"стало «{AppliedValueText}», система подтвердила «{VerifiedActualText}»";

    public string StatusRu => Succeeded
        ? RebootNeeded ? "применено, нужна перезагрузка" : "применено и проверено"
        : $"не получилось: {ExplanationRu}";
}

/// <summary>Модель представления пресета: состав и подпись собираются из данных, а не пишутся руками.</summary>
public sealed record PresetViewModel(
    TweakProfilePreset Preset,
    string WireName,
    string TitleRu,
    string PurposeRu,
    int Count)
{
    public string ChipRu => $"{TitleRu} · {Count}";
}
