using System.Text.RegularExpressions;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Сверка каталога с документацией производителя (волна 52).
///
/// Повод: в описании снятия троттлинга стояло «не более 10 пакетов за прерывание». В материалах
/// поддержки и документации производителей адаптеров речь о **пакетах за миллисекунду** — и только
/// для трафика, не относящегося к мультимедийной задаче. Ошибка была не в цифре, а в сути: из неё
/// следовало, что твик снижает задержку в игре. Не снижает — и теперь это написано прямым текстом,
/// а параметр убран из пресета «Низкий пинг».
///
/// Здесь же проверяются ещё три места, где документация MMCSS говорит прямее, чем популярные советы:
/// значения SystemResponsiveness ниже 10 поднимаются до 20; при Scheduling Category = High приоритет
/// задачи всегда трактуется как 2; SFIO Priority помечен как неиспользуемый.
/// </summary>
public sealed class LatencyClaimsTests
{
    private static IReadOnlyList<ITweak> Catalog() =>
        new TweakCatalog(new TweakPlatformPorts()).GetAllTweaks();

    private static TweakDescriptor Descriptor(string code) =>
        Catalog().Single(t => t.Descriptor.Code == code).Descriptor;

    private static string Read(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"нет копии {name} для проверки: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Снятие_троттлинга_описано_как_в_документации_и_не_обещает_ниже_пинг()
    {
        TweakDescriptor throttle = Descriptor("MMCSS_NETWORK_THROTTLING");
        string text = throttle.WhatItDoesRu + " " + throttle.GamingJustificationRu;

        // Главная фактическая правка: пакеты за миллисекунду, а не за прерывание.
        Assert.Contains("за миллисекунду", text, StringComparison.Ordinal);
        Assert.DoesNotContain("за прерывание", text, StringComparison.Ordinal);

        // И честная рамка: это не про задержку в матче.
        Assert.Contains("не влияет", throttle.GamingJustificationRu, StringComparison.Ordinal);
        Assert.Contains("маршрут", throttle.GamingJustificationRu, StringComparison.Ordinal);

        // Направление влияния на пинг — ноль, и в пресете «Низкий пинг» его больше нет.
        Assert.Equal(0, throttle.PingImpactDirection);
        Assert.False(throttle.IncludedInLowPingPreset);
        Assert.True(throttle.RebootRequired, "документация требует перезагрузку после смены этого значения");
    }

    [Fact]
    public void Отзывчивость_системы_описана_с_правилом_подъёма_до_20()
    {
        TweakDescriptor responsiveness = Descriptor("MMCSS_SYSTEM_RESPONSIVENESS");

        Assert.Contains("до 20", responsiveness.DefaultValueNoteRu, StringComparison.Ordinal);
        Assert.Contains("100", responsiveness.DefaultValueNoteRu, StringComparison.Ordinal);
        Assert.Contains("ниже 10", responsiveness.GamingJustificationRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Приоритет_задачи_честно_называет_правило_категории_High()
    {
        TweakDescriptor priority = Descriptor("GAME_TASK_PRIORITY");

        // Документация MMCSS: при Scheduling Category = High значение приоритета всегда трактуется как 2.
        Assert.Contains("High", priority.GamingJustificationRu, StringComparison.Ordinal);
        Assert.Contains("как 2", priority.GamingJustificationRu, StringComparison.Ordinal);
        Assert.Contains("High", priority.DefaultValueNoteRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Неиспользуемый_параметр_помечен_а_не_выдан_за_работающий()
    {
        TweakDescriptor sfio = Descriptor("GAME_TASK_SFIO_PRIORITY");

        Assert.True(sfio.EffectUnconfirmedOnModernWindows);
        Assert.Contains("неиспользуемый", sfio.GamingJustificationRu, StringComparison.Ordinal);
        // Обещать стабильность от параметра, который Windows не читает, нельзя.
        Assert.Equal(0, sfio.StabilityImpactDirection);
    }

    [Fact]
    public void Разбор_задержки_опирается_на_первоисточники()
    {
        string doc = Read("checked-latency-doc.md");

        string[] links = Regex.Matches(doc, @"https://[^\s\)\]]+")
            .Select(m => m.Value)
            .ToArray();

        Assert.True(links.Length >= 6, $"ссылок мало: {links.Length}");
        Assert.True(links.Count(l => l.Contains("learn.microsoft.com", StringComparison.Ordinal)) >= 3,
            "нужны ссылки на Microsoft Learn, а не пересказ");
        Assert.True(links.Count(l => l.Contains("rfc-editor.org", StringComparison.Ordinal)) >= 2,
            "нужны ссылки на RFC");

        // Разбор обязан прямо сказать, что снятие троттлинга не про задержку, и назвать шум шумом.
        Assert.Contains("не влияет", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("в пределах шума", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void Разбор_задержки_не_обещает_невозможного()
    {
        string doc = Read("checked-latency-doc.md");

        foreach (string promise in new[] { "снизит пинг на", "снижает пинг на", "мгновенн", "гарантированно снизит" })
        {
            Assert.DoesNotContain(promise, doc, StringComparison.OrdinalIgnoreCase);
        }

        // Числа твиков в разборе не вписаны руками: их считает каталог и проверки страницы.
        Assert.DoesNotMatch(new Regex(@"\d+\s+твик"), doc);
    }

    [Fact]
    public void README_ведёт_к_разбору_задержки()
    {
        string readme = Read("checked-README.md");

        Assert.Contains("docs/ЗАДЕРЖКА.md", readme, StringComparison.Ordinal);
        // Число проверок макета в README тоже не должно отставать от жизни.
        Assert.Contains("123 проверки", readme, StringComparison.Ordinal);
    }
}
