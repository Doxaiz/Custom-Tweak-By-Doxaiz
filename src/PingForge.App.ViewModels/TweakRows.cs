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
