using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Network;
using PingForge.Orchestration.Realtime;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Анти-чит называют три экрана: карточка игры, предупреждение планировщика (что сделает кнопка
/// «Оптимизировать») и доклад детектора («нашли игру»). Пока слова жили в трёх местах, они расходились:
/// карточка обещала «маршрут и QoS без фильтра пакетов», доклад говорил «автоматические действия
/// выключены» — при том, что маршрут и QoS для такой игры как раз применяются. Хуже того, обе фразы
/// подразумевали сетевой фильтр, которого в программе нет вовсе.
/// </summary>
public sealed class AntiCheatNotesTests
{
    private static GameFlowInfo Flow(AntiCheatStatus status) => new(
        ProcessId: 4242,
        ProcessName: "game.exe",
        GameTitleRu: "Тестовая игра",
        RemoteEndpoint: "3.4.5.6",
        RemotePort: 9000,
        Transport: "UDP",
        AntiCheat: status);

    [Theory]
    [InlineData(AntiCheatStatus.VerifiedSafe)]
    [InlineData(AntiCheatStatus.Untested)]
    [InlineData(AntiCheatStatus.KnownKernelConflict)]
    public void Слова_об_анти_чите_не_обещают_фильтра_которого_нет(AntiCheatStatus status)
    {
        string text = AntiCheatNotes.ExplainRu(status, "Тестовая игра", "Тестовый анти-чит");

        // Сказано прямо: перехвата пакетов в программе нет. И ни слова «фильтр» — оно намекало,
        // что фильтр существует и его можно «не поднимать» для избранных игр.
        Assert.Contains("перехвата пакетов", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("фильтр", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinDivert", text, StringComparison.OrdinalIgnoreCase);

        // И при этом не отнято то, что программа действительно делает.
        Assert.Contains("маршрут", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AntiCheatStatus.VerifiedSafe)]
    [InlineData(AntiCheatStatus.Untested)]
    [InlineData(AntiCheatStatus.KnownKernelConflict)]
    public void Планировщик_говорит_теми_же_словами_что_и_остальные_экраны(AntiCheatStatus status)
    {
        GameFlowInfo flow = Flow(status);
        OptimizationPlan plan = new OptimizationActionPlanner().Plan(flow, new RealtimeOptions(), isAdministrator: true);

        // Ни одного обещания «мы не поднимем фильтр» и ни одного «действия выключены».
        string all = string.Join(" | ", plan.Notes);
        Assert.DoesNotContain("фильтр", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("выключен", all, StringComparison.OrdinalIgnoreCase);

        if (status == AntiCheatStatus.VerifiedSafe)
        {
            // Проверенной игре предупреждения не нужно: пугать человека нечем.
            Assert.DoesNotContain(plan.Notes, note => note.Contains("анти-чит", StringComparison.OrdinalIgnoreCase));
            return;
        }

        Assert.Contains(plan.Notes, note => string.Equals(
            note, AntiCheatNotes.ExplainRu(status, flow.GameTitleRu), StringComparison.Ordinal));
    }

    [Fact]
    public void Карточка_игры_берёт_слова_из_того_же_места()
    {
        var game = new KnownGame("game.exe", "Тестовая игра", "Тестовый анти-чит", AntiCheatStatus.KnownKernelConflict);
        var card = new GameCardViewModel(game, executablePath: null, open: _ => Task.CompletedTask, choose: _ => Task.CompletedTask);

        Assert.Equal($"Тестовый анти-чит: {AntiCheatNotes.ActionsRu(AntiCheatStatus.KnownKernelConflict)}", card.AntiCheatRu);

        // В карточке тоже ни намёка на фильтр, зато сказано, что анти-чит работает в ядре.
        Assert.DoesNotContain("фильтр", card.AntiCheatRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ядре", card.AntiCheatRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Непроверенное_называется_непроверенным_а_проверенное_проверенным()
    {
        string untested = AntiCheatNotes.ExplainRu(AntiCheatStatus.Untested, "Тестовая игра");
        string verified = AntiCheatNotes.ExplainRu(AntiCheatStatus.VerifiedSafe, "Тестовая игра");

        Assert.Contains("не проверялась", untested, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("не проверялась", verified, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("проверена", verified, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Один_анти_чит_не_называется_по_разному_у_разных_игр()
    {
        // Раньше EAC был «ядерным» только в справочнике маршрутов, а в каталоге процессов стоял
        // «не проверяли» — один и тот же анти-чит, два разных вердикта.
        var catalog = new GameProcessCatalog();

        var eac = catalog.All.Where(g => g.AntiCheatName.Contains("EasyAntiCheat", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(eac);
        Assert.All(eac, g => Assert.Equal(AntiCheatStatus.KnownKernelConflict, g.AntiCheat));

        var vanguard = catalog.All.Where(g => g.AntiCheatName.Contains("Vanguard", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(vanguard);
        Assert.All(vanguard, g => Assert.Equal(AntiCheatStatus.KnownKernelConflict, g.AntiCheat));

        Assert.Contains(eac, g => g.TitleRu == "Rust");
    }

    [Fact]
    public void Rust_подключается_к_выбранному_серверу_и_это_видно_в_каталоге()
    {
        // Факты, а не догадка: у Rust нет матчмейкинга — сервер выбирают в списке; порт сервера по
        // умолчанию 28015 UDP, а 28016 и 28082 — администрирование и приложение Rust+, не задержка игры.
        var catalog = new GameProcessCatalog();

        Assert.True(catalog.TryMatch(@"C:\Steam\steamapps\common\Rust\RustClient.exe", out KnownGame rust));
        Assert.Equal("Rust", rust.TitleRu);
        Assert.Equal(AntiCheatStatus.KnownKernelConflict, rust.AntiCheat);
        Assert.True(rust.MatchesPort(28015));
        Assert.Contains("28015", rust.NoteRu, StringComparison.Ordinal);
        Assert.Contains("28016", rust.NoteRu, StringComparison.Ordinal);
        Assert.Contains("матчмейкинга", rust.NoteRu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EAC", rust.NoteRu, StringComparison.Ordinal);
        Assert.DoesNotContain("фильтр", rust.NoteRu, StringComparison.OrdinalIgnoreCase);
    }
}
