using PingForge.App.ViewModels;
using PingForge.Core.Models;
using PingForge.Orchestration.Realtime;
using PingForge.Service.Protocol;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Модели представления окна. Это «интерфейс без интерфейса»: те же решения, которые принимает окно
/// (спрашивать ли подтверждение, что показать при отказе, можно ли нажимать кнопку), проверяются тестами,
/// а не глазами на стенде.
/// </summary>
public class ViewModelTests
{
    /// <summary>Ответы исправной службы для тестов, у которых свой «особый» ответ на одну команду.</summary>
    private static readonly FakeEngineChannel Fallback = new();

    private static (MainViewModel Main, FakeEngineChannel Channel, FakeDialogs Dialogs) BuildMain()
    {
        var channel = new FakeEngineChannel();
        var dialogs = new FakeDialogs();
        return (new MainViewModel(new EngineClient(channel), dialogs), channel, dialogs);
    }

    // ── главный экран: «при запуске программа ничего не делает» ─────────────────────────────────

    [Fact]
    public void При_запуске_программа_ничего_не_делает()
    {
        (MainViewModel main, FakeEngineChannel channel, _) = BuildMain();

        Assert.Empty(channel.Requests);
        Assert.False(main.IsOptimizing);
        Assert.Equal("Оптимизировать соединение", main.ButtonTextRu);
        Assert.Contains("до этого программа ничего не меняет", main.StatusRu);
    }

    [Fact]
    public async Task Кнопка_запускает_оптимизацию_и_сообщает_итог()
    {
        (MainViewModel main, FakeEngineChannel channel, _) = BuildMain();

        await main.OptimizeCommand.ExecuteAsync();

        Assert.True(main.IsOptimizing);
        Assert.Equal("Остановить и вернуть как было", main.ButtonTextRu);
        // Первым уходит проверочный ping (окно сверяет версии перед меняющей командой), затем сама
        // команда, затем обновление состояния — порядок и проверяется, а не «единственный запрос».
        Assert.Equal(EngineCommandKind.Ping, channel.Requests[0].Command);
        Assert.Contains(channel.Requests, request => request.Command == EngineCommandKind.RealtimeStart);
        Assert.Equal("Fortnite", main.GameTitleRu);
        Assert.Equal(120, main.BaselineRttMs);
        Assert.Equal(78, main.CurrentRttMs);
        Assert.Contains("было 120 мс, сейчас 78 мс", main.RttLineRu);
        Assert.Contains("стало лучше на 42 мс", main.RttLineRu);
    }

    [Fact]
    public async Task Повторное_нажатие_останавливает_и_снимает_изменения()
    {
        (MainViewModel main, FakeEngineChannel channel, _) = BuildMain();
        await main.OptimizeCommand.ExecuteAsync();

        await main.OptimizeCommand.ExecuteAsync();

        Assert.False(main.IsOptimizing);
        Assert.Equal("Оптимизировать соединение", main.ButtonTextRu);
        Assert.Contains(channel.Requests, request => request.Command == EngineCommandKind.RealtimeStop);
        Assert.Contains("свои изменения сняты", main.StatusRu);
    }

    [Fact]
    public async Task Выигрыша_нет_так_и_написано()
    {
        // Двойник называет одни и те же числа и в ответе на запуск, и в состоянии — как настоящая служба.
        var channel = new FakeEngineChannel
        {
            BaselineRttMs = 80,
            CurrentRttMs = 80.3,
            ImprovementMs = 0.3,
        };

        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());
        await main.OptimizeCommand.ExecuteAsync();

        Assert.Contains("заметного выигрыша нет", main.RttLineRu);
        Assert.DoesNotContain("лучше", main.RttLineRu);
    }

    [Fact]
    public async Task Задержка_не_измерена_и_не_показывается_как_ноль()
    {
        var channel = new FakeEngineChannel
        {
            GameTitleRu = null,
            BaselineRttMs = null,
            CurrentRttMs = null,
            ImprovementMs = null,
        };

        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());
        await main.OptimizeCommand.ExecuteAsync();

        Assert.Equal("Задержка: не измерена", main.RttLineRu);
        Assert.DoesNotContain("0 мс", main.RttLineRu);
    }

    [Fact]
    public async Task Отказ_службы_показывается_пользователю_а_не_проглатывается()
    {
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.RealtimeStart
            ? EngineResponse.Failure(request.Id, "Нет прав: запустите приложение от администратора.")
            : Fallback.DefaultAnswer(request));

        var dialogs = new FakeDialogs();
        var main = new MainViewModel(new EngineClient(channel), dialogs);

        await main.OptimizeCommand.ExecuteAsync();

        Assert.False(main.IsOptimizing);
        Assert.Contains("Нет прав", main.StatusRu);

        // Ответ виден прямо на экране: карточка с заголовком и текстом. Модального окна нет —
        // на живой Windows именно модальное окно с DefaultDesktopOnly вешало главное окно.
        Assert.True(main.IsNoticeVisible);
        Assert.Equal("Оптимизация не запустилась", main.NoticeTitleRu);
        Assert.Contains("Нет прав", main.NoticeRu);
        Assert.Empty(dialogs.Information);

        main.DismissNoticeCommand.Execute(null);
        Assert.False(main.IsNoticeVisible);
    }

    [Fact]
    public async Task Связь_со_службой_пропала_объясняется_человеческими_словами()
    {
        var channel = new FakeEngineChannel { ThrowOnSend = new IOException("канал закрыт") };
        var dialogs = new FakeDialogs();
        var main = new MainViewModel(new EngineClient(channel), dialogs);

        await main.OptimizeCommand.ExecuteAsync();

        Assert.Contains("служба PingForge не отвечает", main.StatusRu, StringComparison.OrdinalIgnoreCase);
        Assert.False(main.IsOptimizing);
        Assert.Equal("Служба недоступна", main.NoticeTitleRu);
        Assert.True(main.IsNoticeVisible);
    }

    [Fact]
    public void Окно_запущенное_от_администратора_не_пишет_что_прав_нет()
    {
        // Так было на живой Windows: журнал писал «Права администратора: есть», а раздел «О программе» —
        // «Прав администратора нет». Права знает сама Windows, поэтому окно берёт их у неё.
        var channel = new FakeEngineChannel(request => Fallback.DefaultAnswer(request));
        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs(), runsAsAdministrator: true);

        Assert.True(main.HasRights);
        Assert.Contains("можно менять", main.RightsNoteRu);
    }

    [Fact]
    public async Task Нажатие_кнопки_не_открывает_модальных_окон_а_пишет_ответ_в_интерфейс()
    {
        // Правило: окно программы не должно замирать на время ответа. Любой ответ — на экране.
        var channel = new FakeEngineChannel(request => Fallback.DefaultAnswer(request));
        var dialogs = new FakeDialogs();
        var main = new MainViewModel(new EngineClient(channel), dialogs);

        await main.OptimizeCommand.ExecuteAsync();

        Assert.Empty(dialogs.Information);
        Assert.Empty(dialogs.Confirmations);
    }

    [Fact]
    public async Task Чужой_ответ_не_принимается_за_свой()
    {
        var channel = new FakeEngineChannel { AnswerWithWrongId = true };
        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());

        await main.OptimizeCommand.ExecuteAsync();

        Assert.False(main.IsOptimizing);
        Assert.Contains("чужой запрос", main.StatusRu);
    }

    [Fact]
    public async Task Без_прав_сказано_что_будут_только_замеры()
    {
        var channel = new FakeEngineChannel { IsElevationAvailable = false };
        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());
        await main.OptimizeCommand.ExecuteAsync();

        Assert.False(main.HasRights);
        Assert.Contains("только замеры", main.RightsNoteRu);
    }

    [Fact]
    public async Task Починка_сети_без_подтверждения_ничего_не_делает()
    {
        (MainViewModel main, FakeEngineChannel channel, FakeDialogs dialogs) = BuildMain();
        dialogs.ConfirmAnswer = false;

        await main.RepairNetworkCommand.ExecuteAsync();

        Assert.Single(dialogs.Confirmations);
        Assert.Empty(channel.Requests);
        Assert.Contains("Починка отменена", main.StatusRu);
    }

    [Fact]
    public async Task Починка_сети_с_подтверждением_зовёт_службу()
    {
        (MainViewModel main, FakeEngineChannel channel, _) = BuildMain();

        await main.RepairNetworkCommand.ExecuteAsync();

        Assert.Equal(EngineCommandKind.RepairNetwork, channel.Requests[^1].Command);
        Assert.Contains("Остатков после аварии не найдено", main.StatusRu);
    }

    [Fact]
    public async Task Двойное_нажатие_не_запускает_оптимизацию_дважды()
    {
        var gate = new TaskCompletionSource();
        var channel = new SlowChannel(gate);
        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());

        Task first = main.OptimizeCommand.ExecuteAsync();
        Task second = main.OptimizeCommand.ExecuteAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, channel.Requests.Count(request => request.Command == EngineCommandKind.RealtimeStart));
    }

    [Fact]
    public async Task Долгие_команды_ждут_дольше_десяти_секунд_а_быстрые_нет()
    {
        // Так было на живой Windows: канал ждал 10 секунд на любую команду, а применение твиков
        // с точкой восстановления и живой запуск идут дольше. Человек видел «служба не ответила»,
        // хотя служба продолжала работать, и казалось, что кнопка не работает.
        var gate = new TaskCompletionSource();
        var channel = new SlowChannel(gate);
        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());

        Task start = main.OptimizeCommand.ExecuteAsync();
        gate.SetResult();
        await start;

        // Рядом с командой идут проверочный ping и обновление состояния (по 10 с) — предел команды
        // ищется по её запросу, а не «первый/последний в списке».
        int startIndex = channel.Requests.FindIndex(request => request.Command == EngineCommandKind.RealtimeStart);
        Assert.True(startIndex >= 0, "команда запуска не дошла до канала");
        Assert.Equal(TimeSpan.FromSeconds(180), channel.Timeouts[startIndex]);

        var quick = new FakeEngineChannel();
        var quickMain = new MainViewModel(new EngineClient(quick), new FakeDialogs());
        await quickMain.RefreshStatusCommand.ExecuteAsync();

        Assert.Equal(TimeSpan.FromSeconds(10), quick.Calls[0].Timeout);
    }

    [Fact]
    public async Task Обновление_состояния_не_ломает_экран_при_битом_ответе()
    {
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.Status
            ? EngineResponse.Success(request.Id, "ок", "{ это не json")
            : Fallback.DefaultAnswer(request));

        var main = new MainViewModel(new EngineClient(channel), new FakeDialogs());
        await main.RefreshStatusCommand.ExecuteAsync();

        Assert.Contains("неразборчиво", main.StatusRu);
        Assert.Null(main.CurrentRttMs);
    }

    // ── канал связи ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Номера_запросов_идут_по_порядку_и_сверяются_с_ответом()
    {
        var channel = new FakeEngineChannel();
        var client = new EngineClient(channel);

        await client.SendAsync(EngineCommandKind.Status);
        await client.SendAsync(EngineCommandKind.Status);

        Assert.Equal("ui-1", channel.Requests[0].Id);
        Assert.Equal("ui-2", channel.Requests[1].Id);
        Assert.Equal("ui-2", client.LastRequestId);
    }

    [Fact]
    public async Task Описание_команд_спрашивается_один_раз()
    {
        var channel = new FakeEngineChannel();
        var client = new EngineClient(channel);

        await client.GetCapabilitiesAsync();
        await client.GetCapabilitiesAsync();

        Assert.Single(channel.Requests);
        Assert.NotNull(client.CapabilitiesRu);
    }

    [Fact]
    public async Task Версия_протокола_берётся_из_ответа_службы()
    {
        var client = new EngineClient(new FakeEngineChannel());

        (bool ok, string message) = await client.PingAsync();

        Assert.True(ok);
        Assert.Contains("жива", message);
        Assert.Equal(1, client.ProtocolVersion);
    }

    // ── раздел «Твики» ──────────────────────────────────────────────────────────────────────────

    private static (TweaksViewModel Tweaks, FakeEngineChannel Channel, FakeDialogs Dialogs) Tweaks(
        int? recommendedCount = null)
    {
        var channel = new FakeEngineChannel();
        var dialogs = new FakeDialogs();
        var rows = new List<TweakRowViewModel>();
        int recommended = recommendedCount ?? 20;

        for (int i = 0; i < 47; i++)
        {
            bool isRecommended = i < recommended;
            bool dangerous = i >= 44;
            rows.Add(new TweakRowViewModel(
                $"TWEAK_{i}",
                $"Твик {i}",
                "Что делает твик.",
                dangerous ? "не советуем" : isRecommended ? "включать: да" : "по желанию",
                "почему так",
                isRecommended,
                enabledByDefault: isRecommended,
                isInLowPingPreset: i % 3 == 0,
                isInMaxSpeedPreset: i % 7 == 0,
                isInFpsPreset: i % 2 == 0,
                focusRu: "Пинг и сеть",
                isDangerous: dangerous));
        }

        var presets = new List<PresetViewModel>
        {
            new(TweakProfilePreset.LowLatencyGaming, "low-latency", "Низкий пинг", "Только задержка", 19),
            new(TweakProfilePreset.MaxThroughput, "max-speed", "Скорость", "Пропускная способность", 5),
            new(TweakProfilePreset.FpsAndResponsiveness, "fps", "Кадры и отзывчивость", "Питание и фон", 23),
        };

        return (new TweaksViewModel(new EngineClient(channel), dialogs, rows, presets), channel, dialogs);
    }

    [Fact]
    public void Список_твиков_показывает_числа_из_данных_а_не_из_воздуха()
    {
        (TweaksViewModel tweaks, _, _) = Tweaks(recommendedCount: 23);

        Assert.Equal(47, tweaks.Rows.Count);
        Assert.Equal(23, tweaks.RecommendedCount);
        Assert.Equal("Отмечено 23 из 47", tweaks.SelectionLineRu);
    }

    [Fact]
    public void Только_рекомендуемое_выключает_всё_остальное()
    {
        (TweaksViewModel tweaks, _, _) = Tweaks(recommendedCount: 21);
        tweaks.SelectAll();

        tweaks.SelectRecommended();

        Assert.Equal(21, tweaks.CheckedCount);
        Assert.All(tweaks.Rows.Where(row => !row.IsRecommended), row => Assert.False(row.IsOn));
        Assert.Contains("21 твиков из 47", tweaks.StatusRu);
    }

    [Fact]
    public void Включить_всё_не_включает_то_что_каталог_не_советует()
    {
        (TweaksViewModel tweaks, _, _) = Tweaks();

        tweaks.SelectAll();

        Assert.Equal(44, tweaks.CheckedCount);
        Assert.All(tweaks.Rows.Where(row => row.IsDangerous), row => Assert.False(row.IsOn));
        Assert.Contains("не советуем", tweaks.StatusRu);
        Assert.Contains("3 штуки", tweaks.StatusRu);
    }

    [Fact]
    public void Отключить_всё_оставляет_нулевой_отбор_и_кнопка_гаснет()
    {
        (TweaksViewModel tweaks, _, _) = Tweaks();

        tweaks.SelectNone();

        Assert.Equal(0, tweaks.CheckedCount);
        Assert.False(tweaks.ApplySelectedCommand.CanExecute(null));
        Assert.Contains("Все твики выключены", tweaks.StatusRu);
    }

    [Fact]
    public async Task Применение_пресета_спрашивает_подтверждение_и_точку_восстановления()
    {
        (TweaksViewModel tweaks, FakeEngineChannel channel, FakeDialogs dialogs) = Tweaks();
        tweaks.SelectedPreset = tweaks.Presets[0];

        await tweaks.ApplyPresetCommand.ExecuteAsync();

        Assert.Single(dialogs.Confirmations);
        Assert.Contains("Низкий пинг", dialogs.Confirmations[0].Title);
        Assert.Contains("19", dialogs.Confirmations[0].Message);
        Assert.Single(dialogs.RestorePointOffers);
        Assert.Equal((19, "Низкий пинг"), dialogs.RestorePointOffers[0]);

        EngineRequest request = channel.Requests.Single(r => r.Command == EngineCommandKind.ApplyPreset);
        Assert.Equal("low-latency", request.Argument("preset"));
        Assert.Equal("true", request.Argument("restorePoint"));
        Assert.Contains("применено 19 из 19", tweaks.StatusRu);
    }

    [Fact]
    public async Task Отказ_в_подтверждении_означает_что_ничего_не_применялось()
    {
        (TweaksViewModel tweaks, FakeEngineChannel channel, FakeDialogs dialogs) = Tweaks();
        tweaks.SelectedPreset = tweaks.Presets[1];
        dialogs.ConfirmAnswer = false;

        await tweaks.ApplyPresetCommand.ExecuteAsync();

        Assert.Empty(channel.Requests);
        Assert.Contains("отменено", tweaks.StatusRu);
    }

    [Fact]
    public async Task Отказ_от_точки_восстановления_идёт_службе_как_есть()
    {
        (TweaksViewModel tweaks, FakeEngineChannel channel, FakeDialogs dialogs) = Tweaks();
        tweaks.SelectedPreset = tweaks.Presets[2];
        dialogs.RestorePointAnswer = false;

        await tweaks.ApplyPresetCommand.ExecuteAsync();

        Assert.Equal("false", channel.Requests.Single(r => r.Command == EngineCommandKind.ApplyPreset).Argument("restorePoint"));
    }

    [Fact]
    public async Task Свой_набор_идёт_кодами_и_предупреждает_про_опасное()
    {
        (TweaksViewModel tweaks, FakeEngineChannel channel, FakeDialogs dialogs) = Tweaks();
        tweaks.SelectNone();
        tweaks.Rows[0].IsOn = true;
        tweaks.Rows[1].IsOn = true;
        tweaks.Rows[45].IsOn = true; // «не советуем», выбран вручную

        await tweaks.ApplySelectedCommand.ExecuteAsync();

        Assert.Contains("3 твиков", dialogs.Confirmations[0].Title);
        Assert.Contains("1 с вердиктом «не советуем»", dialogs.Confirmations[0].Message);

        EngineRequest request = channel.Requests.Single(r => r.Command == EngineCommandKind.ApplyTweaks);
        Assert.Equal("TWEAK_0,TWEAK_1,TWEAK_45", request.Argument("tweaks"));
    }

    [Fact]
    public async Task Применение_без_выбранного_пресета_объясняет_что_делать()
    {
        (TweaksViewModel tweaks, FakeEngineChannel channel, _) = Tweaks();

        // Кнопка в этом состоянии недоступна, но и прямой вызов метода обязан объяснить, чего не хватает.
        Assert.False(tweaks.ApplyPresetCommand.CanExecute(null));
        await tweaks.ApplySelectedPresetAsync();

        Assert.Empty(channel.Requests);
        Assert.Contains("Пресет не выбран", tweaks.StatusRu);
    }

    [Fact]
    public async Task Возврат_настроек_спрашивает_подтверждение()
    {
        (TweaksViewModel tweaks, FakeEngineChannel channel, FakeDialogs dialogs) = Tweaks();
        dialogs.ConfirmAnswer = false;

        await tweaks.RollbackCommand.ExecuteAsync();
        Assert.Empty(channel.Requests);

        dialogs.ConfirmAnswer = true;
        await tweaks.RollbackCommand.ExecuteAsync();
        Assert.Equal(EngineCommandKind.RollbackSnapshot, channel.Requests[^1].Command);
    }

    [Fact]
    public void Подпись_пресета_собирается_из_данных_каталога()
    {
        (TweaksViewModel tweaks, _, _) = Tweaks();

        Assert.Equal("Низкий пинг · 19", tweaks.Presets[0].ChipRu);
        Assert.Equal("Скорость · 5", tweaks.Presets[1].ChipRu);
        Assert.Equal("Кадры и отзывчивость · 23", tweaks.Presets[2].ChipRu);

        tweaks.SelectedPreset = tweaks.Presets[2];
        Assert.Contains("Применится твиков: 23", tweaks.SelectedPresetNoteRu);
    }

    // ── раздел «Журнал» ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Журнал_читается_и_считает_неудачи()
    {
        var channel = new FakeEngineChannel();
        var journal = new JournalViewModel(new EngineClient(channel), rowsToAsk: 20);

        await journal.RefreshCommand.ExecuteAsync();

        Assert.Equal("20", Assert.Single(channel.Requests).Argument("limit"));
        Assert.Equal(2, journal.Rows.Count);
        Assert.Equal(1, journal.FailedCount);
        Assert.Contains("Записей: 2", journal.SummaryRu);
        Assert.Contains("не получилось: 1", journal.SummaryRu);
        Assert.Contains("политика запретила запись", journal.Rows[1].StatusRu);
    }

    [Fact]
    public async Task Пустой_журнал_говорит_что_программа_ничего_не_меняла()
    {
        var channel = new FakeEngineChannel(request => request.Command == EngineCommandKind.JournalTail
            ? EngineResponse.Success(request.Id, "Журнал пуст: программа ещё ничего не меняла.", """{"count":0,"records":[]}""")
            : Fallback.DefaultAnswer(request));

        var journal = new JournalViewModel(new EngineClient(channel));
        await journal.RefreshCommand.ExecuteAsync();

        Assert.Empty(journal.Rows);
        Assert.Equal("Программа ещё ничего не меняла.", journal.SummaryRu);
    }

    [Fact]
    public async Task Недоступная_служба_не_выдаётся_за_пустой_журнал()
    {
        var channel = new FakeEngineChannel { ThrowOnSend = new IOException("нет связи") };
        var journal = new JournalViewModel(new EngineClient(channel));

        await journal.RefreshCommand.ExecuteAsync();

        Assert.Contains("не отвечает", journal.StatusRu);
        Assert.True(journal.IsUnavailable);
        Assert.DoesNotContain("Программа ещё ничего не меняла", journal.SummaryRu);
        Assert.Contains("Журнал не прочитан", journal.SummaryRu);
    }

    [Fact]
    public void Число_записей_журнала_ограничено_пределом_службы()
    {
        var journal = new JournalViewModel(new EngineClient(new FakeEngineChannel()), rowsToAsk: 5000);

        Assert.Equal(EngineDispatcherLimits.MaxJournalLimit, journal.RowsToAsk);
    }

    // ── раздел «Настройки» ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Настройки_показывают_где_лежит_файл_и_читают_его_значения()
    {
        var store = new InMemorySettingsStore(new AppSettings("economy", OfferRestorePoint: false, CloseToTray: true));
        var settings = new SettingsViewModel(new EngineClient(new FakeEngineChannel()), store, new FakeDialogs());

        Assert.Contains("settings.json", settings.SettingsFilePath);
        Assert.Equal(PerfProfile.Economy, settings.Profile);
        Assert.False(settings.OfferRestorePoint);
        Assert.True(settings.CloseToTray);
    }

    [Fact]
    public async Task Сохранение_настроек_проверяется_по_факту_а_не_на_слово()
    {
        var store = new InMemorySettingsStore(new AppSettings("balanced"));
        var settings = new SettingsViewModel(new EngineClient(new FakeEngineChannel()), store, new FakeDialogs());

        settings.Profile = PerfProfile.MaxPerformance;
        await settings.SaveCommand.ExecuteAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Equal("max", store.Load().PerfProfile);
        Assert.Contains("Настройки сохранены", settings.StatusRu);
    }

    [Fact]
    public async Task Профиль_потребления_отправляется_службе_и_описывается_своими_словами()
    {
        var channel = new FakeEngineChannel();
        var store = new InMemorySettingsStore();
        var settings = new SettingsViewModel(new EngineClient(channel), store, new FakeDialogs())
        {
            Profile = PerfProfile.Economy,
        };

        await settings.ApplyProfileCommand.ExecuteAsync();

        Assert.Equal("economy", channel.Requests.Single(r => r.Command == EngineCommandKind.PerfProfile).Argument("profile"));
        Assert.Contains("Контроль каждые 6 с", settings.ProfileNoteRu);
        Assert.Contains("10 проб в минуту", settings.ProfileNoteRu);
        Assert.Contains("на паузу", settings.ProfileNoteRu);
    }

    [Fact]
    public void Описание_профилей_считается_теми_же_правилами_что_в_движке()
    {
        var settings = new SettingsViewModel(
            new EngineClient(new FakeEngineChannel()),
            new InMemorySettingsStore(),
            new FakeDialogs());

        settings.Profile = PerfProfile.MaxPerformance;
        Assert.Contains("Контроль каждые 1 с", settings.ProfileNoteRu);

        settings.Profile = PerfProfile.Balanced;
        Assert.Contains("Контроль каждые 2 с", settings.ProfileNoteRu);
        Assert.Contains("30 проб в минуту", settings.ProfileNoteRu);
        Assert.Contains("пауза только когда игра свёрнута", settings.ProfileNoteRu);
    }

    // ── сборка строк из настоящего каталога ────────────────────────────────────────────────────

    [Fact]
    public void Строки_твиков_собираются_из_настоящего_каталога_и_сходятся_с_пресетами()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        IReadOnlyList<TweakRowViewModel> rows = TweakRowFactory.BuildAll(catalog);

        Assert.Equal(47, rows.Count);
        Assert.Equal(23, rows.Count(row => row.IsRecommended));
        Assert.Equal(23, rows.Count(row => row.EnabledByDefault));
        Assert.Equal(19, rows.Count(row => row.IsInLowPingPreset));
        Assert.Equal(5, rows.Count(row => row.IsInMaxSpeedPreset));
        Assert.Equal(23, rows.Count(row => row.IsInFpsPreset));
        Assert.Equal(3, rows.Count(row => row.IsDangerous));
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.TitleRu));
            Assert.False(string.IsNullOrWhiteSpace(row.WhatItDoesRu));
            Assert.False(string.IsNullOrWhiteSpace(row.VerdictRu));
            Assert.False(string.IsNullOrWhiteSpace(row.PresetsLineRu));
        });
    }

    [Fact]
    public void Подписи_пресетов_в_окне_совпадают_с_составом_из_каталога()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        IReadOnlyList<PresetViewModel> presets = TweakRowFactory.BuildPresets(catalog);

        Assert.Equal(19, presets[0].Count);
        Assert.Equal(5, presets[1].Count);
        Assert.Equal(23, presets[2].Count);

        // Тот же состав, что применит служба: иначе подпись «19» была бы неправдой.
        Assert.Equal(catalog.GetByPreset(TweakProfilePreset.LowLatencyGaming).Count, presets[0].Count);
    }

    [Fact]
    public void Твики_с_вердиктом_не_советуем_не_входят_ни_в_один_пресет()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        IReadOnlyList<TweakRowViewModel> rows = TweakRowFactory.BuildAll(catalog);

        Assert.All(rows.Where(row => row.IsDangerous), row =>
        {
            Assert.False(row.IsInLowPingPreset);
            Assert.False(row.IsInMaxSpeedPreset);
            Assert.False(row.IsInFpsPreset);
            Assert.False(row.EnabledByDefault);
        });
    }

    [Fact]
    public void Строки_идут_по_подкатегориям_в_порядке_пинг_скорость_кадры_ввод_система()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());
        IReadOnlyList<TweakRowViewModel> rows = TweakRowFactory.BuildAll(catalog);

        string[] order = rows.Select(row => row.FocusRu).Distinct().ToArray();
        Assert.Equal(["Пинг и сеть", "Скорость", "Кадры", "Ввод", "Система"], order);
    }

    /// <summary>Канал, который «думает» до сигнала: нужен, чтобы поймать двойное нажатие.</summary>
    private sealed class SlowChannel : IEngineChannel
    {
        private readonly TaskCompletionSource _gate;

        public SlowChannel(TaskCompletionSource gate) => _gate = gate;

        public List<EngineRequest> Requests { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public async Task<EngineResponse> SendAsync(EngineRequest request, TimeSpan timeout, CancellationToken ct = default)
        {
            Requests.Add(request);
            Timeouts.Add(timeout);
            await _gate.Task.ConfigureAwait(false);
            return Fallback.DefaultAnswer(request);
        }

        public void Dispose()
        {
        }
    }
}
