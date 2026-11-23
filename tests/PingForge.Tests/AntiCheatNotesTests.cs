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

    [Fact]
    public void The_Finals_узнаётся_по_рабочему_имени_и_сборкам_патча_8()
    {
        // Игра зовётся по рабочему имени проекта, а с патча 8.0.0 у неё две сборки с суффиксами -e и -d.
        // Без этой проверки каталог терял игру ровно тогда, когда её переименовали.
        var catalog = new GameProcessCatalog();

        foreach (string exe in new[] { "discovery.exe", "Discovery-e.exe", "DISCOVERY-D.EXE" })
        {
            Assert.True(catalog.TryMatch($@"C:\Games\The Finals\{exe}", out KnownGame game), exe);
            Assert.Equal("The Finals", game.TitleRu);
        }

        Assert.True(catalog.TryMatch("discovery.exe", out KnownGame finals));
        Assert.Equal(AntiCheatStatus.KnownKernelConflict, finals.AntiCheat);
        Assert.Contains("Denuvo", finals.NoteRu, StringComparison.Ordinal);
        Assert.Contains("8.0.0", finals.NoteRu, StringComparison.Ordinal);
        Assert.Contains("перехвата пакетов", finals.NoteRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Marvel_Rivals_называет_свой_анти_чит_а_не_чужой()
    {
        // NetEase ставит Anti-Cheat Expert (Neac Protect), а не Easy Anti-Cheat: путать их значит
        // обещать человеку не тот анти-чит, который у него в системе.
        var catalog = new GameProcessCatalog();

        Assert.True(catalog.TryMatch(@"C:\Games\MarvelRivals\Marvel-Win64-Shipping.exe", out KnownGame marvel));
        Assert.Equal("Marvel Rivals", marvel.TitleRu);
        Assert.Equal(AntiCheatStatus.KnownKernelConflict, marvel.AntiCheat);
        Assert.Contains("Anti-Cheat Expert", marvel.AntiCheatName, StringComparison.Ordinal);
        Assert.DoesNotContain("EasyAntiCheat", marvel.AntiCheatName, StringComparison.Ordinal);
        Assert.Contains("Neac Protect", marvel.NoteRu, StringComparison.Ordinal);
    }

    [Fact]
    public void Порты_Valve_в_каталоге_процессов_не_включают_загрузки_Steam()
    {
        // TCP 27014–27050 в справке Steam — загрузки, а не игра. Если этот диапазон попадёт в игру,
        // оптимизация уйдёт в скачивание обновления.
        var catalog = new GameProcessCatalog();

        Assert.True(catalog.TryMatch("cs2.exe", out KnownGame cs2));
        Assert.True(cs2.MatchesPort(27000));
        Assert.True(cs2.MatchesPort(27030));
        Assert.False(cs2.MatchesPort(27050));
        Assert.Contains("загрузки", cs2.NoteRu, StringComparison.Ordinal);

        Assert.True(catalog.TryMatch("dota2.exe", out KnownGame dota));
        Assert.True(dota.MatchesPort(27015));
        Assert.False(dota.MatchesPort(27040));
    }

    [Fact]
    public void Dead_by_Daylight_описан_как_выделенные_серверы_GameLift()
    {
        var catalog = new GameProcessCatalog();

        Assert.True(catalog.TryMatch("deadbydaylight-win64-shipping.exe", out KnownGame dbd));
        Assert.Equal(AntiCheatStatus.KnownKernelConflict, dbd.AntiCheat);
        Assert.Contains("GameLift", dbd.NoteRu, StringComparison.Ordinal);
        Assert.Contains("2019", dbd.NoteRu, StringComparison.Ordinal);
        Assert.Contains("Франкфурт", dbd.NoteRu, StringComparison.Ordinal);
        Assert.DoesNotContain("peer-to-peer", dbd.NoteRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Разрушитель_не_обещает_VPN_блокировок_а_называет_запрет_манипуляций()
    {
        var catalog = new GameProcessCatalog();

        Assert.True(catalog.TryMatch("destiny2.exe", out KnownGame destiny));
        Assert.Contains("манипуляции сетью", destiny.NoteRu, StringComparison.Ordinal);
        Assert.DoesNotContain("блокировки за VPN", destiny.NoteRu, StringComparison.Ordinal);
        Assert.Contains("Bungie", destiny.NoteRu, StringComparison.Ordinal);
    }
}
