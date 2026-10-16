using System.Text.RegularExpressions;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Аудит каталога на запрещённое. Список запретов взят из требований и проверяется автотестом,
/// а не остаётся обещанием в документации: если кто-то добавит твик, выключающий Defender,
/// сборка тестов упадёт.
/// </summary>
public static class CatalogSafetyAudit
{
    private static readonly (string Fragment, string ReasonRu)[] ForbiddenFragments =
    {
        ("Windows Defender", "Правила запрещают трогать Windows Defender и антивирусную защиту."),
        ("Defender", "Правила запрещают трогать Windows Defender и антивирусную защиту."),
        ("WinDefend", "Правила запрещают трогать службу антивирусной защиты."),
        ("MpEngine", "Правила запрещают трогать движок антивирусной защиты."),
        ("SecurityHealth", "Правила запрещают трогать «Безопасность Windows»."),
        ("wscsvc", "Правила запрещают отключать центр безопасности Windows."),
        ("FirewallPolicy", "Правила запрещают отключать брандмауэр и его правила."),
        ("EnableLUA", "Правила запрещают трогать контроль учётных записей (UAC)."),
        ("ConsentPromptBehavior", "Правила запрещают менять поведение UAC."),
        ("DriverSigning", "Правила запрещают отключать проверку подписи драйверов."),
        ("DisableAntiSpyware", "Правила запрещают отключать антишпионскую защиту."),
        ("WindowsUpdate", "Правила запрещают трогать обновления безопасности Windows."),
        ("NetworkAddress", "Правила запрещают подмену MAC-адреса."),
        ("MacAddress", "Правила запрещают подмену MAC-адреса."),
        ("PagingFiles", "Правила запрещают менять файл подкачки: это риск для стабильности системы."),
        ("SafeBoot", "Правила запрещают трогать параметры безопасной загрузки."),
        ("TermService", "Правила запрещают отключать системные службы удалённого доступа."),
        ("W32Time", "Правила запрещают отключать службу синхронизации времени.")
    };

    // Важно искать по границам слов: подстрочный поиск ловил «чит» внутри «Отключить».
    private static readonly Regex[] ForbiddenTextPatterns = new[]
    {
        "инжект", "inject", "античит", "anticheat", "чит", "читы", "обход блокировки",
        "обойти бан", "ping 0", "пинг 0"
    }.Select(term => new Regex($@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();

    private static readonly string[] ForbiddenTextTerms =
    {
        "инжект", "inject", "античит", "anticheat", "чит", "читы", "обход блокировки",
        "обойти бан", "ping 0", "пинг 0"
    };

    public static IReadOnlyList<string> AuditCatalog(IEnumerable<ITweak> tweaks)
    {
        ArgumentNullException.ThrowIfNull(tweaks);

        var problems = new List<string>();

        foreach (ITweak tweak in tweaks)
        {
            TweakDescriptor d = tweak.Descriptor;

            string haystack = string.Join(' ', new[]
            {
                d.Code, d.TitleRu, d.TitleEn, d.ResourceLocator, d.WhatItDoesRu, d.GamingJustificationRu
            });

            foreach ((string fragment, string reason) in ForbiddenFragments)
            {
                if (haystack.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{d.Code}: найдено «{fragment}». {reason}");
                }
            }

            for (int i = 0; i < ForbiddenTextPatterns.Length; i++)
            {
                if (ForbiddenTextPatterns[i].IsMatch(haystack))
                {
                    problems.Add($"{d.Code}: в описании есть запрещённое обещание или тема «{ForbiddenTextTerms[i]}».");
                }
            }

            if (d.RequiresManualVerification && !d.EffectUnconfirmedOnModernWindows)
            {
                problems.Add($"{d.Code}: помечен «требует ручной проверки», но не помечен «эффект не подтверждён на современной Windows». " +
                             "Пользователь должен видеть причину, а не просто значок.");
            }

            if (d.Badge == GameRecommendationBadge.Recommended && d.Risk == TweakRiskLevel.High)
            {
                problems.Add($"{d.Code}: бейдж «рекомендую» несовместим с высоким риском.");
            }

            if (d.Risk == TweakRiskLevel.High && (d.IncludedInLowPingPreset || d.IncludedInMaxSpeedPreset))
            {
                problems.Add($"{d.Code}: твик высокого риска не может попадать в автоматические пресеты.");
            }
        }

        return problems;
    }

    /// <summary>Проверка конкретной строки, пришедшей извне (например, из пользовательского пресета).</summary>
    public static bool IsForbiddenTarget(string resourceLocator, out string reasonRu)
    {
        ArgumentNullException.ThrowIfNull(resourceLocator);

        foreach ((string fragment, string reason) in ForbiddenFragments)
        {
            if (resourceLocator.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                reasonRu = reason;
                return true;
            }
        }

        reasonRu = string.Empty;
        return false;
    }

    /// <summary>Ни одно значение из системы не должно попадать в командную строку как есть.</summary>
    public static bool LooksLikeCleanCliToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value, @"[\s""'&|<>^%!;]");
}
