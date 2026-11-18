using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PingForge.App.ViewModels;
using PingForge.App.Views;
using PingForge.Core.Models;
using PingForge.Core.Layout;
using PingForge.Probing;
using PingForge.Routing;

namespace PingForge.App;

/// <summary>
/// Самопроверка запуска — то, чего не хватало раньше: до этого окно программы не проверялось
/// ни на одной машине, и «запустил — ничего не произошло» нельзя было отличить от «упало молча».
///
/// Режим <c>--self-check</c> проходит весь путь старта без показа окна и человека:
///     каталог твиков → модели представления → окно (разбор XAML и раскладка) → значок в трее.
/// Каждая ступень попадает в журнал запуска и в консоль (когда её есть), а код возврата говорит,
/// что именно не получилось. Сборочная машина запускает эту же проверку на Windows — на том самом
/// одиночном .exe, который скачивает человек.
///
/// Ничего в системе самопроверка не меняет: она только строит объекты и читает. Команд, меняющих
/// настройки, здесь нет нарочно.
/// </summary>
internal static class SelfCheck
{
    /// <summary>Коды возврата: их читает и сборочная машина, и человек.</summary>
    private const int Ok = 0;
    private const int CatalogFailed = 21;
    private const int ViewModelsFailed = 22;
    private const int WindowFailed = 23;
    private const int TimedOut = 4;

    /// <summary>Ответила ли служба. Нужно сборочной машине: строку про службу она ищет в журнале.</summary>
    internal static bool ServiceAnswered { get; private set; }

    internal static int Run(App app)
    {
        StartupLog.Step("Самопроверка: дальше окно не показывается, система не меняется");

        var problems = new List<string>();

        int catalogCode = CheckCatalog(app, problems);
        if (catalogCode != Ok)
        {
            return Finish(problems, catalogCode);
        }

        int modelCode = CheckViewModels(app, problems);
        if (modelCode != Ok)
        {
            return Finish(problems, modelCode);
        }

        int windowCode = CheckWindow(app, problems);

        CheckBindings(app, problems);
        CheckNotice(app, problems);
        CheckLauncherAndSetup(app, problems);
        CheckCustomGames(app, problems);
        CheckRoutingServers(app, problems);
        CheckAnalysisIntegration(app, problems);
        CheckUpdateChecker(app, problems);
        CheckNetworkSelfCheck(app, problems);
        CheckServerPath(app, problems);
        CheckSessionHistory(app, problems);
        CheckGameRegionsAndDns(app, problems);
        CheckSupportPackage(app, problems);
        CheckLayout(app, problems);
        CheckNothingIsCutOff(app, problems);
        CheckParagraphWrapping(app, problems);
        CheckTray(app, problems);
        CheckService(problems);

        // Если окно построилось, но раскладка или привязки плохи — это тот же отказ, что и «окно не собралось»:
        // пользователь видит не работающую программу.
        if (windowCode == Ok && problems.Any(problem => problem.Contains("сжаты", StringComparison.Ordinal)
            || problem.Contains("нет карточки", StringComparison.Ordinal)
            || problem.Contains("потеряла данные", StringComparison.Ordinal)
            || problem.Contains("нет надписей", StringComparison.Ordinal)
            || problem.Contains("нет карточки ответа", StringComparison.Ordinal)
            || problem.Contains("выходят за край окна", StringComparison.Ordinal)
            || problem.Contains("не переносятся", StringComparison.Ordinal)
            || problem.Contains("история сессий", StringComparison.Ordinal)
            || problem.Contains("пакет для поддержки", StringComparison.Ordinal)))
        {
            windowCode = WindowFailed;
        }

        return Finish(problems, windowCode);
    }

    /// <summary>Каталог: без него в окне нечего показывать.</summary>
    private static int CheckCatalog(App app, List<string> problems)
    {
        try
        {
            int tweaks = app.Catalog.GetAllTweaks().Count;
            int presets = app.Catalog.GetByPreset(Core.Models.TweakProfilePreset.LowLatencyGaming).Count;
            StartupLog.Note($"Каталог твиков: {tweaks} {TweakOrdering.Plural(tweaks, "твик", "твика", "твиков")}, " +
                $"в пресете «низкий пинг» — {presets}");

            if (tweaks < 47)
            {
                problems.Add($"в каталоге {tweaks} твиков, а ожидалось хотя бы 47");
                return CatalogFailed;
            }

            return Ok;
        }
        catch (Exception ex)
        {
            StartupLog.Problem("Каталог твиков не собрался", ex);
            problems.Add($"каталог твиков: {ex.GetType().Name}: {ex.Message}");
            return CatalogFailed;
        }
    }

    /// <summary>Модели представления: разделы окна строятся из них.</summary>
    private static int CheckViewModels(App app, List<string> problems)
    {
        try
        {
            StartupLog.Note($"Карточек твиков: {app.Tweaks.Rows.Count}, пресетов: {app.Tweaks.Presets.Count}");
            StartupLog.Note($"Главный экран: состояние «{app.MainScreen.StatusRu}», прав администратора: {(app.MainScreen.HasRights ? "есть" : "нет")}");

            if (app.Tweaks.Rows.Count != app.Catalog.GetAllTweaks().Count)
            {
                problems.Add($"карточек твиков {app.Tweaks.Rows.Count}, а твиков в каталоге {app.Catalog.GetAllTweaks().Count}");
                return ViewModelsFailed;
            }

            if (app.Tweaks.Presets.Count == 0)
            {
                problems.Add("в разделе «Твики» нет ни одного пресета");
                return ViewModelsFailed;
            }

            return Ok;
        }
        catch (Exception ex)
        {
            StartupLog.Problem("Модели представления не собрались", ex);
            problems.Add($"модели представления: {ex.GetType().Name}: {ex.Message}");
            return ViewModelsFailed;
        }
    }

    /// <summary>
    /// Окно: самое важное. Здесь XAML разбирается по-настоящему, шаблоны раскрываются, и раскладка
    /// считается. Именно на этом шаге вылезают ошибки, которые видно только при запуске, — например
    /// ссылка на ресурс, которого нет.
    /// </summary>
    private static int CheckWindow(App app, List<string> problems)
    {
        MainWindow? window = app.Shell;
        if (window is null)
        {
            problems.Add("окно не создалось");
            return WindowFailed;
        }

        try
        {
            if (window.Content is not FrameworkElement root)
            {
                problems.Add("у окна нет содержимого — XAML не загрузился");
                return WindowFailed;
            }

            var size = new Size(window.Width, window.Height);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();

            // Свёрнутые разделы в визуальном дереве не участвуют, а проверить надо все:
            // показываем их на время проверки и пересчитываем раскладку.
            ShowAllSections(window);

            int elements = CountElements(root);
            string[] sections =
            [
                nameof(OptimizeView), nameof(GamesView), nameof(TweaksView),
                nameof(JournalView), nameof(SettingsView), nameof(AboutView),
            ];
            var found = new List<string>();
            var missing = new List<string>();
            foreach (string section in sections)
            {
                (FindByName(root, section) is not null ? found : missing).Add(section);
            }

            bool hasTitle = HasText(root, "Custom Tweak By Doxaiz");

            StartupLog.Note($"Окно: элементов в дереве {elements}, разделов найдено {found.Count} из {sections.Length}, название на месте: {(hasTitle ? "да" : "НЕТ")}");

            if (elements < 50)
            {
                problems.Add($"в окне всего {elements} элементов — похоже, разметка не загрузилась");
                return WindowFailed;
            }

            if (missing.Count > 0)
            {
                problems.Add($"в окне нет разделов: {string.Join(", ", missing)}");
                return WindowFailed;
            }

            if (!hasTitle)
            {
                problems.Add("в окне нет названия программы — разметка собралась не та");
                return WindowFailed;
            }

            return Ok;
        }
        catch (Exception ex)
        {
            StartupLog.Problem("Окно не построилось", ex);
            problems.Add($"окно: {ex.GetType().Name}: {ex.Message}");
            return WindowFailed;
        }
    }

    /// <summary>
    /// Привязки страниц. Отдельная проверка, потому что именно здесь была настоящая ошибка, которую
    /// видно только глазами: страница «Оптимизация» получала данные не из того свойства модели
    /// ({Binding Main} вместо {Binding MainScreen}), и все её значения оставались пустыми —
    /// большая кнопка без надписи, карточки без чисел. Окно при этом строилось «успешно».
    ///
    /// Проверяем то, что видит человек: у главной кнопки есть надпись, а на странице есть числа из
    /// каталога и настроек. Пустая надпись = отказ.
    /// </summary>
    private static void CheckBindings(App app, List<string> problems)
    {
        MainWindow? window = app.Shell;
        if (window?.Content is not FrameworkElement root)
        {
            return;   // окна нет — об этом уже сказано выше, второй раз не ругаемся
        }

        string[] expected =
        [
            "Оптимизировать соединение",                     // надпись на большой кнопке (состояние «покой»)
            app.MainScreen.ButtonTextRu,                      // или то, что она говорит сейчас
            $"Твиков в каталоге: {app.Tweaks.Rows.Count}",    // шапка окна
        ];

        var texts = new List<string>();
        CollectButtonTexts(root, texts);
        CollectTextBlockTexts(root, texts);
        string all = string.Join("\n", texts);

        var missing = expected.Where(text => !all.Contains(text, StringComparison.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            problems.Add($"в окне нет надписей из привязок: {string.Join("; ", missing)} — страница потеряла данные");
            StartupLog.Warn($"Привязки: НЕ найдены надписи — {string.Join("; ", missing)}");
            return;
        }

        StartupLog.Note($"Привязки: страницы получили данные (проверено {expected.Length} надписи)");
    }

    /// <summary>
    /// Раздел «Игры» и мастер первого запуска — то, что делает программу понятной с первого клика.
    ///
    /// Проверяется то, что видит человек: в списке игр есть карточки, у каждой есть путь или честное
    /// «не найдена», а мастер либо показан на первом запуске, либо уже пройден — но его разметка
    /// не потерялась (иначе кнопка «Пройти мастер заново» в настройках открывала бы пустоту).
    /// </summary>
    private static void CheckLauncherAndSetup(App app, List<string> problems)
    {
        MainWindow? window = app.Shell;
        if (window?.Content is not FrameworkElement root)
        {
            return;
        }

        ShowAllSections(window);
        root.UpdateLayout();

        if (FindByName(root, nameof(GamesView)) is null)
        {
            problems.Add("в окне нет раздела «Игры»");
            return;
        }

        int cards = app.Games.Cards.Count;
        int withPath = app.Games.Cards.Count(card => card.HasPath);
        StartupLog.Note($"Игры: карточек {cards}, с путём на диске {withPath}; мастер первого запуска " +
            (app.Setup.IsVisible ? "показан" : "уже пройден"));

        if (cards == 0)
        {
            problems.Add("в разделе «Игры» нет ни одной карточки — список пуст");
        }

        if (app.Setup.Recommended.TitleRu.Length == 0)
        {
            problems.Add("мастер первого запуска не может посчитать совет");
        }
    }

    /// <summary>
    /// Свои игры (задача A) на живой Windows: добавить карточку в модель окна, переименовать
    /// и удалить. Файловый диалог здесь не участвует (его автоматикой честно не нажать),
    /// а вся логика окна — настоящая: валидация, запись в настройки, список карточек.
    /// В качестве .exe берётся сам файл программы: он точно существует. После проверки
    /// список возвращается к прежнему виду.
    /// </summary>
    private static void CheckCustomGames(App app, List<string> problems)
    {
        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;
            int before = app.Games.Cards.Count;

            app.Games.AddCustomGameAsync(exe, "Проверка своих игр").GetAwaiter().GetResult();
            GameCardViewModel? card = app.Games.Cards.FirstOrDefault(candidate => candidate.IsCustom);
            if (card is null)
            {
                problems.Add("свои игры: карточка своей игры не появилась в окне");
                StartupLog.Note("СВОИ ИГРЫ: НЕ работает — карточка не добавлена");
                return;
            }

            bool renamed = app.Games.RenameCustomGame(card, "Проверка своих игр (переименована)");
            bool removed = app.Games.RemoveCustomGame(card);
            bool restored = app.Games.Cards.Count == before;
            bool ok = renamed && removed && restored;

            StartupLog.Note($"СВОИ ИГРЫ: добавление/переименование/удаление в окне — {(ok ? "работает" : "НЕ работает")}");
            if (!ok)
            {
                problems.Add("свои игры: добавление, переименование или удаление в окне не сработало до конца");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"свои игры: проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"СВОИ ИГРЫ: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Сервер маршрутизации (задача B) на живой Windows: блок в «Настройках» собран, честный текст
    /// на месте, и автопоиск шлюзов читает настоящую таблицу маршрутов через разборчик продукта.
    ///
    /// Живой тест ровно тот, что можно честно провести на сборочной машине: здесь нет второго
    /// канала или VPN — и программа обязана сказать об этом прямо, а не выдумать кандидатов.
    /// Ничего не меняется: route print — чтение. Применение маршрутов остаётся за службой и
    /// кнопкой «Оптимизировать», и самопроверка её не трогает.
    /// </summary>
    private static void CheckRoutingServers(App app, List<string> problems)
    {
        try
        {
            RoutingSettingsViewModel routing = app.Settings.Routing;
            if (routing.Rows is null)
            {
                problems.Add("серверы маршрутизации: блок настроек не собран");
                StartupLog.Note("СЕРВЕРЫ МАРШРУТИЗАЦИИ: НЕ работает — модель блока не собрана");
                return;
            }

            // const — статический член, читаем его от класса, не от экземпляра.
            if (!RoutingSettingsViewModel.WarningRu.Contains("Destiny 2", StringComparison.Ordinal)
                || !RoutingSettingsViewModel.WarningRu.Contains("третью сторону", StringComparison.Ordinal))
            {
                problems.Add("серверы маршрутизации: честное предупреждение о третьей стороне потерялось из блока");
            }

            // Предупреждение и кнопки обязаны доходить до разметки: именованный блок в SettingsView.
            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement settingsRoot)
                {
                    settingsRoot.UpdateLayout();
                    if (FindNamed<FrameworkElement>(settingsRoot, "RoutingBlock") is null)
                    {
                        problems.Add("серверы маршрутизации: блок «Сервер маршрутизации» не найден в окне настроек");
                    }
                }
            }

            string routePrint = RunRoutePrint();
            if (routePrint.Length == 0)
            {
                StartupLog.Note("СЕРВЕРЫ МАРШРУТИЗАЦИИ: таблица маршрутов не прочиталась (route print) — это среда, а не дефект программы");
                return;
            }

            var routes = PingForge.Network.Routes.RouteTableParser.Parse(routePrint);
            var found = PingForge.Network.Routes.RoutingNodeDiscovery.Discover(routes, Array.Empty<PingForge.Network.Routes.AdapterInfo>());
            int alternatives = found.Count(node => node.SourceRu != PingForge.Network.Routes.RoutingNodeSourceRu.CurrentGateway);
            bool sourcesHonest = found.All(node => PingForge.Network.Routes.RoutingNodeSourceRu.IsHonest(node.SourceRu));
            bool noInvented = found.All(node => routes.Any(route =>
                route.NextHop.Equals(node.Address, StringComparison.OrdinalIgnoreCase)));

            if (!sourcesHonest || !noInvented)
            {
                problems.Add("серверы маршрутизации: поиск выдал узел с нечестным источником — так не должно быть никогда");
                StartupLog.Note("СЕРВЕРЫ МАРШРУТИЗАЦИИ: НЕ работает — источник узла не из таблицы маршрутов");
                return;
            }

            StartupLog.Note($"СЕРВЕРЫ МАРШРУТИЗАЦИИ: блок в окне есть, честный текст на месте; живой поиск шлюзов — работает, " +
                $"узлов {found.Count} (альтернатив текущему шлюзу: {alternatives}; " +
                $"{(alternatives == 0 ? "кандидатов кроме текущего шлюза нет — сравнивать пути не среди чего, это правда этой машины" : "есть с чем сравнивать")})");
        }
        catch (Exception ex)
        {
            problems.Add($"серверы маршрутизации: проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"СЕРВЕРЫ МАРШРУТИЗАЦИИ: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Задача F: анализ подключён к продукту. Проверяется то, что видит человек: график сети
    /// в «Оптимизации» (с честной надписью «данных пока нет» до первого замера), карточка
    /// «Задержка ввода» в «Твиках» и блок «Проверка настроек» в «Настройках».
    ///
    /// Модель графика прогоняется по-настоящему: серия с разрывом обязана превратиться в отрезки
    /// линий в разметке (Polyline), а точка без измерения — остаться разрывом, а не нулём.
    /// </summary>
    private static void CheckAnalysisIntegration(App app, List<string> problems)
    {
        try
        {
            // ── График сети в «Оптимизации» ────────────────────────────────────────────────
            var chart = app.MainScreen.Chart;
            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<FrameworkElement>(root, "ChartCard") is null)
                    {
                        problems.Add("график сети: нет карточки «График сети» в разделе «Оптимизация»");
                    }

                    if (!HasText(root, "График сети") || !HasText(root, "Данных пока нет"))
                    {
                        problems.Add("график сети: нет честной надписи «данных пока нет» — пустой график без объяснения");
                    }

                    if (!HasText(root, "1 минута") || !HasText(root, "15 минут"))
                    {
                        problems.Add("график сети: нет переключателя окна 1/5/15 минут");
                    }

                    if (FindNamed<FrameworkElement>(root, "InputLatencyBlock") is null || !HasText(root, "Проверить задержку ввода"))
                    {
                        problems.Add("задержка ввода: нет карточки «Задержка ввода» с кнопкой проверки в разделе «Твики»");
                    }

                    if (FindNamed<FrameworkElement>(root, "SettingsCheckBlock") is null || !HasText(root, "Проверить настройки"))
                    {
                        problems.Add("проверка настроек: нет карточки «Проверка настроек» в разделе «Настройки»");
                    }

                    // Живая прогонка модели графика: серия с разрывом → линии в разметке.
                    DateTimeOffset now = DateTimeOffset.Now;
                    chart.ReceiveSamples(
                    [
                        new PingForge.Analysis.Charting.ChartSample(now.AddSeconds(-6), RttAvgMs: 40, JitterMadMs: 1.5, PacketLossPercent: 0),
                        PingForge.Analysis.Charting.ChartSample.Empty(now.AddSeconds(-4)),
                        new PingForge.Analysis.Charting.ChartSample(now.AddSeconds(-2), RttAvgMs: 46, JitterMadMs: 2, PacketLossPercent: 0.5),
                    ]);
                    chart.IsWindow5 = true;
                    root.UpdateLayout();

                    if (!chart.HasData || chart.RttPanel.IsEmpty || chart.RttPanel.Segments.Count < 1)
                    {
                        problems.Add("график сети: серия с измерениями не построилась в линии");
                    }

                    int lines = CountPolylines(root);
                    if (lines < 3)
                    {
                        problems.Add($"график сети: в разметке {lines} линий, а панели (задержка, джиттер, потери) — три");
                    }

                    // Разрыв обязан остаться разрывом: две измеренные точки с пустой между ними —
                    // это два отрезка у ряда RTT (или один, если точки соединились в обход пустой,
                    // что было бы ошибкой), но никак не три подряд заполненных.
                    var rttSeries = PingForge.Analysis.Charting.NetworkChartBuilder
                        .Build(new[]
                        {
                            new PingForge.Analysis.Charting.ChartSample(now.AddSeconds(-6), RttAvgMs: 40),
                            PingForge.Analysis.Charting.ChartSample.Empty(now.AddSeconds(-4)),
                            new PingForge.Analysis.Charting.ChartSample(now.AddSeconds(-2), RttAvgMs: 46),
                        }, TimeSpan.FromMinutes(5))
                        .Series.Single(s => s.Indicator == PingForge.Analysis.Charting.ChartIndicator.RttAvgMs);
                    if (rttSeries.Points.Any(p => !p.IsMeasured && p.Value is not null))
                    {
                        problems.Add("график сети: пустая точка превратилась в число — нарушено правило «нет измерения — нет нуля»");
                    }
                }
            }

            // ── Карточка задержки ввода: модель и фильтр ───────────────────────────────────
            if (app.Tweaks.InputLatency is null)
            {
                problems.Add("задержка ввода: карточка не прикреплена к разделу «Твики»");
                StartupLog.Note("ЗАДЕРЖКА ВВОДА: НЕ работает — карточка не прикреплена");
            }
            else
            {
                int allRows = app.Tweaks.Rows.Count;
                app.Tweaks.ShowOnlyCodes(["MOUSE_ACCEL_SPEED", "KEYBOARD_FILTERKEYS_OFF"]);
                bool filtered = app.Tweaks.IsFiltered
                    && app.Tweaks.VisibleRows.Count < allRows
                    && app.Tweaks.VisibleRows.All(row => row.Code is "MOUSE_ACCEL_SPEED" or "KEYBOARD_FILTERKEYS_OFF");
                app.Tweaks.ClearFilter();
                bool restored = !app.Tweaks.IsFiltered && app.Tweaks.VisibleRows.Count == allRows;

                if (!filtered || !restored)
                {
                    problems.Add("задержка ввода: фильтр «показать связанные твики» не отбирает и не возвращает список");
                    StartupLog.Note("ЗАДЕРЖКА ВВОДА: НЕ работает — фильтр связанных твиков сломан");
                }
                else
                {
                    StartupLog.Note("ЗАДЕРЖКА ВВОДА: карточка в «Твиках» есть, фильтр связанных твиков работает");
                }
            }

            // ── Проверка настроек: модель ──────────────────────────────────────────────────
            if (app.Settings.Check is null)
            {
                problems.Add("проверка настроек: блок не собран в разделе «Настройки»");
                StartupLog.Note("ПРОВЕРКА НАСТРОЕК: НЕ работает — модель блока не собрана");
            }
            else
            {
                // До запуска проверки статус обязан говорить «ещё не запускалась», а не «всё хорошо».
                bool honestIdle = app.Settings.Check.StatusRu.Contains("ещё не запускалась", StringComparison.Ordinal);
                if (!honestIdle)
                {
                    problems.Add("проверка настроек: до запуска статус не говорит честное «ещё не запускалась»");
                }

                StartupLog.Note(honestIdle
                    ? "ПРОВЕРКА НАСТРОЕК: блок в «Настройках» есть, честный статус до запуска на месте"
                    : "ПРОВЕРКА НАСТРОЕК: НЕ работает — статус до запуска нечестный");
            }

            StartupLog.Note($"ГРАФИК СЕТИ: карточка построена, линий в разметке: {(app.Shell?.Content is FrameworkElement r ? CountPolylines(r) : 0)}, " +
                "источник данных — только замеры живой оптимизации (собственных зондов у графика нет)");
        }
        catch (Exception ex)
        {
            problems.Add($"анализ (задача F): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"АНАЛИЗ (ЗАДАЧА F): НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Задача I: проверка обновлений в разделе «О программе». Что обязано быть видно человеку
    /// и что самопроверка может доказать без сети: карточка и кнопка на месте, до нажатия статус
    /// честно говорит «ещё не проверяли» (значит, в фоне запросов не было), кнопка страницы релиза
    /// до проверки не предлагается. Сравнение версий и разбор ответа GitHub прогоняются на месте:
    /// «v0.4.1» старше «0.5.0» быть не должно, а мусорный тег обязан отклоняться.
    ///
    /// Настоящий запрос к GitHub самопроверка не делает: проверка обновлений — действие человека
    /// по кнопке, а не самодеятельность программы. Нажатие кнопки на живой Windows проверяет
    /// сценарий tools/ui-walkthrough.ps1.
    /// </summary>
    private static void CheckUpdateChecker(App app, List<string> problems)
    {
        try
        {
            UpdateCheckViewModel updates = app.MainScreen.Updates;

            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<FrameworkElement>(root, "UpdateBlock") is null
                        || FindNamed<FrameworkElement>(root, "CheckUpdatesButton") is null
                        || !HasText(root, "Проверить обновления"))
                    {
                        problems.Add("обновления: нет карточки «Обновления» с кнопкой «Проверить обновления» в разделе «О программе»");
                    }

                    if (!HasText(root, "Обновления ещё не проверяли"))
                    {
                        problems.Add("обновления: до нажатия кнопки статус не говорит честное «ещё не проверяли»");
                    }

                    if (!HasText(root, updates.CurrentVersionLineRu))
                    {
                        problems.Add("обновления: в карточке не видна текущая версия — сравнивать человеку не с чем");
                    }

                    if (FindNamed<FrameworkElement>(root, "OpenReleaseButton") is { Visibility: not Visibility.Collapsed })
                    {
                        problems.Add("обновления: кнопка «Открыть страницу релиза» видна до проверки — открывать пока нечего");
                    }
                }
            }

            // Главное правило задачи: до нажатия кнопки программа никуда не ходит. Модель только что
            // построена вместе с окном и обязана стоять в исходном состоянии.
            if (updates.State != UpdateCheckState.NotChecked || updates.HasReleasePage || updates.ReleasePageUrl is not null)
            {
                problems.Add("обновления: проверка началась без нажатия кнопки — фоновых запросов быть не должно");
                StartupLog.Note("ОБНОВЛЕНИЯ: НЕ работает — проверка стартовала сама, без кнопки");
            }

            // Сравнение версий и разбор ответа — чистая логика, ей сеть не нужна.
            bool versionsOk =
                ReleaseTagVersion.TryParse("v0.4.1", out Version older)
                && ReleaseTagVersion.TryParse("0.5.0", out Version newer)
                && older.CompareTo(newer) < 0
                && newer.CompareTo(older) > 0
                && !ReleaseTagVersion.TryParse("nightly-build", out _)
                && !ReleaseTagVersion.TryParse(null, out _);

            var info = GitHubReleaseInfo.Parse(
                "{\"tag_name\":\"v9.9.9\",\"html_url\":\"https://github.com/Doxaiz/Custom-Tweak-By-Doxaiz/releases/tag/v9.9.9\"," +
                "\"assets\":[{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/Doxaiz/x\"}]}");
            var sums = Sha256SumsFile.Parse(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  CustomTweakByDoxaiz.exe\r\nмусорная строка");
            bool parseOk = info.TagName == "v9.9.9" && info.FindChecksums() is not null
                && sums.Count == 1 && sums[0].FileName == "CustomTweakByDoxaiz.exe";

            if (!versionsOk || !parseOk)
            {
                problems.Add("обновления: сравнение версий или разбор ответа GitHub работает неверно");
                StartupLog.Note($"ОБНОВЛЕНИЯ: НЕ работает — версии: {(versionsOk ? "да" : "нет")}, разбор: {(parseOk ? "да" : "нет")}");
                return;
            }

            StartupLog.Note("ОБНОВЛЕНИЯ: кнопка в «О программе» на месте, до нажатия запросов нет " +
                "(статус «ещё не проверяли»), кнопка страницы релиза до проверки скрыта, " +
                "сравнение версий и разбор ответа GitHub работают");
        }
        catch (Exception ex)
        {
            problems.Add($"обновления (задача I): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"ОБНОВЛЕНИЯ: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Задача G: «Проверить сеть» рядом с главной кнопкой. Что доказывается здесь без живой сети:
    /// карточка и кнопка в окне на месте, до нажатия статус — «ещё не запускалась» (значит, ни
    /// одного замера при старте), кнопки «Применить рекомендованное» нет, пока рекомендовать нечего.
    /// Оценщики пунктов прогоняются прямыми вызовами: двойной NAT по двум частным хопам обязан
    /// находиться, провод — давать «не применимо», рост задержки под нагрузкой — считаться из
    /// измеренных медиан, а не из воздуха.
    ///
    /// Сам прогон проверки по кнопке (включая согласие на bufferbloat) на живой Windows делает
    /// сценарий tools/ui-walkthrough.ps1: самопроверка кнопку не нажимает — она стартовый дым, а не сеть.
    /// </summary>
    private static void CheckNetworkSelfCheck(App app, List<string> problems)
    {
        try
        {
            NetworkCheckViewModel check = app.MainScreen.NetworkCheck;

            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<FrameworkElement>(root, "NetworkCheckCard") is null
                        || FindNamed<FrameworkElement>(root, "CheckNetworkButton") is null
                        || !HasText(root, "Проверить сеть"))
                    {
                        problems.Add("проверка сети: нет кнопки «Проверить сеть» или карточки результатов в разделе «Оптимизация»");
                    }

                    if (!HasText(root, "ещё не запускалась"))
                    {
                        problems.Add("проверка сети: до нажатия статус не говорит честное «ещё не запускалась»");
                    }

                    if (FindNamed<FrameworkElement>(root, "ApplyRecommendedButton") is { Visibility: not Visibility.Collapsed })
                    {
                        problems.Add("проверка сети: кнопка «Применить рекомендованное» видна без единой рекомендации");
                    }
                }
            }

            if (check.HasResult || check.Items.Count != 0 || check.HasRecommendations)
            {
                problems.Add("проверка сети: замеры или рекомендации появились без нажатия кнопки");
                StartupLog.Note("ПРОВЕРКА СЕТИ: НЕ работает — результаты есть до запуска");
            }

            // Чистая логика пунктов: те же функции, что строят настоящий результат по кнопке.
            bool natOk =
                NetworkCheckEvaluators.EvaluateDoubleNat(
                    ["192.168.1.50"], "192.168.1.1",
                    [new RouteHopMetric(1, "192.168.1.1", 1, false), new RouteHopMetric(2, "10.0.0.1", 2, false)]).Status
                    == NetworkCheckStatus.Attention
                && NetworkCheckEvaluators.EvaluateDoubleNat(["85.140.1.2"], null, []).Status == NetworkCheckStatus.Ok
                && NetworkCheckEvaluators.EvaluateDoubleNat(["192.168.1.50"], null, []).Status == NetworkCheckStatus.CannotCheck;

            var wifiScan = WifiNetworksParser.Parse(
                "SSID 1 : Home\n    BSSID 1 : aa:bb\n         Channel            : 6\nSSID 2 : Neigh\n    BSSID 1 : cc:dd\n         Channel            : 11\n");
            bool wifiOk = wifiScan.NetworkCount == 2 && wifiScan.BssidsPerChannel.GetValueOrDefault(6) == 1
                && NetworkCheckEvaluators.EvaluateWifi(false, null, null).Status == NetworkCheckStatus.NotApplicable;

            var mtuOkResult = NetworkCheckEvaluators.EvaluateMtu(
                new PathMtuDiscoveryResult(MtuDiscoveryOutcome.OptimalFound, 576, 1500, 1500, 1472, "подтверждено"),
                1500, "якоря");
            var bloatResult = NetworkCheckEvaluators.EvaluateBufferbloat(new BufferbloatResult(
                BufferbloatOutcome.Measured, 20, 140, 50_000_000, 10, "пробы"));
            bool evaluatorsOk = mtuOkResult.Status == NetworkCheckStatus.Ok
                && bloatResult.Status == NetworkCheckStatus.Attention
                && bloatResult.DetailRu.Contains("140", StringComparison.Ordinal)
                && NetworkCheckEvaluators.EvaluateBackground(null, 0, null, null).Status == NetworkCheckStatus.CannotCheck;

            if (!natOk || !wifiOk || !evaluatorsOk)
            {
                problems.Add("проверка сети: оценщики пунктов работают неверно (NAT, Wi-Fi или числа bufferbloat/MTU)");
                StartupLog.Note($"ПРОВЕРКА СЕТИ: НЕ работает — NAT: {(natOk ? "да" : "нет")}, Wi-Fi: {(wifiOk ? "да" : "нет")}, оценщики: {(evaluatorsOk ? "да" : "нет")}");
                return;
            }

            StartupLog.Note("ПРОВЕРКА СЕТИ: кнопка рядом с главной на месте, карточка результатов построена, " +
                "до нажатия замеров нет (статус «ещё не запускалась»), кнопки применения без рекомендаций не видно, " +
                "оценщики пунктов работают");
        }
        catch (Exception ex)
        {
            problems.Add($"проверка сети (задача G): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"ПРОВЕРКА СЕТИ: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Задача H: блок «Путь до сервера» в «Оптимизации». Что доказывается без живой игры:
    /// блок и кнопка на месте, до нажатия трасса не строилась и в сеть не ушло ни одной пробы,
    /// а разбор «где растёт задержка» честно разделяет первый шаг (дом) и путь дальше (провайдер),
    /// подсказывая альтернативный маршрут. Анонимный хоп остаётся без чисел — «не отвечает».
    ///
    /// Саму трассу на живой Windows строит сценарий tools/ui-walkthrough.ps1: самопроверка
    /// кнопку не нажимает — она стартовый дым, а не сеть.
    /// </summary>
    private static void CheckServerPath(App app, List<string> problems)
    {
        try
        {
            ServerPathViewModel path = app.MainScreen.ServerPath;

            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<FrameworkElement>(root, "ServerPathCard") is null
                        || FindNamed<FrameworkElement>(root, "ServerPathButton") is null
                        || !HasText(root, "Путь до сервера")
                        || !HasText(root, "Показать путь"))
                    {
                        problems.Add("путь до сервера: нет блока «Путь до сервера» или кнопки «Показать путь» в разделе «Оптимизация»");
                    }
                }
            }

            if (path.Hops.Count != 0 || path.HasResult || path.SentPackets != 0 || path.IsRunning)
            {
                problems.Add("путь до сервера: трасса началась без нажатия кнопки — при старте программа в сеть не ходит");
                StartupLog.Note("ПУТЬ ДО СЕРВЕРА: НЕ работает — трасса запустилась сама");
                return;
            }

            var home = new List<ServerPathProbeHop>
            {
                new(1, "192.168.1.1", 3, 3, new[] { 9.0, 10.0, 11.0 }),
                new(2, "10.0.0.1", 3, 3, new[] { 10.0, 11.0, 12.0 }),
            };
            var provider = new List<ServerPathProbeHop>
            {
                new(1, "192.168.1.1", 3, 3, new[] { 2.0, 3.0, 2.0 }),
                new(2, "10.0.0.1", 3, 3, new[] { 3.0, 4.0, 3.0 }),
                new(3, "203.0.113.9", 3, 3, new[] { 42.0, 44.0, 43.0 }),
            };
            var silent = new List<ServerPathProbeHop>
            {
                new(1, null, 3, 0, Array.Empty<double>()),
            };

            string homeNote = ServerPathGrowth.DescribeRu(home);
            string providerNote = ServerPathGrowth.DescribeRu(provider);
            string silentNote = ServerPathGrowth.DescribeRu(silent);

            bool homeOk = homeNote.Contains("домашней сети", StringComparison.Ordinal);
            bool providerOk = providerNote.Contains("Сервер маршрутизации", StringComparison.Ordinal)
                && providerNote.Contains("WARP", StringComparison.Ordinal)
                && providerNote.Contains("/32", StringComparison.Ordinal);
            bool silentOk = silent[0].AvgRttMs is null
                && silent[0].P95RttMs is null
                && silent[0].LossPercent is null
                && ServerPathGrowth.Bottleneck(silent) is null
                && silentNote.Contains("Ни один хоп не ответил", StringComparison.Ordinal);
            bool idleOk = path.StatusRu.Contains("не строился", StringComparison.Ordinal);

            if (!homeOk || !providerOk || !silentOk || !idleOk)
            {
                problems.Add("путь до сервера: разбор прироста задержки работает неверно (дом, провайдер или анонимный хоп)");
                StartupLog.Note($"ПУТЬ ДО СЕРВЕРА: НЕ работает — дом: {(homeOk ? "да" : "нет")}, " +
                    $"провайдер: {(providerOk ? "да" : "нет")}, анонимный: {(silentOk ? "да" : "нет")}, " +
                    $"покой: {(idleOk ? "да" : "нет")}");
                return;
            }

            StartupLog.Note("ПУТЬ ДО СЕРВЕРА: блок в «Оптимизации» на месте, до нажатия проб нет (0 пакетов), " +
                "скачок на первом шаге называется домашней сетью, дальше — подсказывает альтернативный маршрут");
        }
        catch (Exception ex)
        {
            problems.Add($"путь до сервера (задача H): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"ПУТЬ ДО СЕРВЕРА: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Регионы игр и DNS (задача D): у карточек «Игры» видно, где серверы игры и какие порты она
    /// публикует; в «Твиках» есть карточка «DNS — это про вход, а не про пинг» со списком резолверов.
    /// Проверяется без сети: данные — из GameRouteCatalog, а не из прогона. Поэтому «пинга до
    /// региона» здесь и быть не может: у игр нет опубликованных адресов дата-центров.
    /// </summary>
    private static void CheckGameRegionsAndDns(App app, List<string> problems)
    {
        try
        {
            GameRegionsViewModel regions = app.Games.Regions;
            DnsCardViewModel dns = app.Tweaks.Dns;

            bool cardsOk = regions.Cards.Count == GameRouteCatalog.Targets.Count;
            bool knownOk = regions.RegionsLineRu("cs2.exe").Contains("Европа", StringComparison.Ordinal)
                && regions.RegionsLineRu("cs2.exe").Contains("матчмейкер", StringComparison.Ordinal);
            bool unknownOk = regions.RegionsLineRu("игры-вне-справочника.exe").Contains("неизвестны", StringComparison.Ordinal)
                && regions.PortsLineRu("игры-вне-справочника.exe").Contains("не публикуются", StringComparison.Ordinal);
            bool dnsOk = dns.Resolvers.Count == GameRouteCatalog.FreeDnsRoutes.Count
                && dns.ExplanationRu.Contains("не снижает пинг в матче", StringComparison.Ordinal)
                && !dns.ExplanationRu.Contains("снизил пинг", StringComparison.Ordinal);
            bool oneListOk = GameRegionsViewModel.BenchmarkCandidates.Count == GameRouteCatalog.FreeDnsRoutes.Count;

            if (!cardsOk || !knownOk || !unknownOk || !dnsOk || !oneListOk)
            {
                problems.Add($"нет карточки регионов и DNS: карточки — {(cardsOk ? "да" : "нет")}, " +
                    $"регионы CS2 — {(knownOk ? "да" : "нет")}, честное «неизвестно» — {(unknownOk ? "да" : "нет")}, " +
                    $"DNS — {(dnsOk ? "да" : "нет")}, один список — {(oneListOk ? "да" : "нет")}");
                StartupLog.Note("РЕГИОНЫ И DNS: НЕ работает — данные каталога не дошли до окна");
                return;
            }

            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<FrameworkElement>(root, "GameRegionsCard") is null
                        || FindNamed<FrameworkElement>(root, "DnsCard") is null
                        || !HasText(root, GameRegionsViewModel.HeadingRu)
                        || !HasText(root, "DNS — это про вход"))
                    {
                        problems.Add("нет карточки регионов и DNS: блока нет в разметке разделов «Игры» или «Твики»");
                        StartupLog.Note("РЕГИОНЫ И DNS: НЕ работает — блок есть в модели, но не в окне");
                        return;
                    }
                }
            }

            StartupLog.Note($"РЕГИОНЫ И DNS: карточек игр {regions.Cards.Count}, резолверов {dns.Resolvers.Count}, " +
                "«пинга до региона» не обещаем — измеряется только выданный сервер");
        }
        catch (Exception ex)
        {
            problems.Add($"нет карточки регионов и DNS (задача D): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"РЕГИОНЫ И DNS: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Пакет для поддержки (задача Q): блок в «О программе» есть, и до нажатия он ничего не читает
    /// и не пишет. Сборку здесь не делаем нарочно: самопроверка не имеет права оставлять архивы
    /// на диске сборочной машины. Проверяется главное обещание — «само собой ничего не собирается».
    /// </summary>
    private static void CheckSupportPackage(App app, List<string> problems)
    {
        try
        {
            SupportPackageViewModel support = app.MainScreen.Support;

            bool quietOk = !support.HasPreview && !support.HasBuiltPackage && !support.CanBuild;

            if (!quietOk)
            {
                problems.Add("пакет для поддержки собрался сам, без нажатия кнопки");
                StartupLog.Note("ПАКЕТ ДЛЯ ПОДДЕРЖКИ: НЕ работает — список появился до нажатия");
                return;
            }

            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<FrameworkElement>(root, "SupportBlock") is null
                        || FindNamed<FrameworkElement>(root, "SupportPrepareButton") is null
                        || FindNamed<FrameworkElement>(root, "SupportBuildButton") is null
                        || !HasText(root, "Пакет для поддержки"))
                    {
                        problems.Add("пакет для поддержки: блока нет в разделе «О программе»");
                        StartupLog.Note("ПАКЕТ ДЛЯ ПОДДЕРЖКИ: НЕ работает — блок не найден в разметке");
                        return;
                    }
                }
            }

            StartupLog.Note("ПАКЕТ ДЛЯ ПОДДЕРЖКИ: кнопка в «О программе» на месте, до нажатия не читается " +
                "ни один файл и не пишется ни один архив");
        }
        catch (Exception ex)
        {
            problems.Add($"пакет для поддержки (задача Q): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"ПАКЕТ ДЛЯ ПОДДЕРЖКИ: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// История сессий (задача J): вкладка «Сессии» в «Журнале» — «до → после», сравнение двух
    /// сессий и график среднего RTT по дням. Проверка идёт без службы: на старте программа не
    /// читает историю и ничего не меняет, поэтому записи обязаны быть пустыми до нажатия.
    /// </summary>
    private static void CheckSessionHistory(App app, List<string> problems)
    {
        try
        {
            SessionHistoryViewModel sessions = app.Journal.Sessions;

            if (app.Shell is { } shell)
            {
                ShowAllSections(shell);
                if (shell.Content is FrameworkElement root)
                {
                    root.UpdateLayout();

                    if (FindNamed<TabControl>(root, "JournalTabs") is not { } tabs)
                    {
                        problems.Add("история сессий: в разделе «Журнал» нет вкладок");
                    }
                    else
                    {
                        TabItem? page = tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                            string.Equals(item.Header as string, "Сессии", StringComparison.Ordinal));

                        if (page is null)
                        {
                            problems.Add("история сессий: в разделе «Журнал» нет вкладки «Сессии»");
                        }
                        else
                        {
                            // Вкладка создаёт содержимое только когда её показали: без этого проверять
                            // кнопки бессмысленно, а ошибки разметки внутри вкладки остались бы незамеченными.
                            int previous = tabs.SelectedIndex;
                            tabs.SelectedItem = page;
                            root.UpdateLayout();

                            bool hasRefresh = FindNamed<FrameworkElement>(root, "SessionsRefreshButton") is not null;
                            bool hasCompare = FindNamed<FrameworkElement>(root, "CompareSessionsButton") is not null;
                            bool hasClear = FindNamed<FrameworkElement>(root, "SessionsClearButton") is not null;

                            if (previous >= 0 && previous < tabs.Items.Count)
                            {
                                tabs.SelectedIndex = previous;
                            }

                            root.UpdateLayout();

                            if (!hasRefresh || !hasCompare || !hasClear)
                            {
                                problems.Add("история сессий: на вкладке «Сессии» нет кнопок обновления, сравнения или очистки");
                            }
                        }
                    }
                }
            }

            if (sessions.Rows.Count != 0 || sessions.IsLoading)
            {
                problems.Add("история сессий: записи появились до нажатия «Обновить сессии» — на старте программа ничего не читает");
                StartupLog.Note("ИСТОРИЯ СЕССИЙ: НЕ работает — записи загрузились сами");
                return;
            }

            // Честность чисел: у незавершённой сессии нет «после», и она так и называется.
            var unfinished = new SessionHistoryRow(
                SessionId: "self-check-unfinished",
                StartedUtc: DateTime.UtcNow,
                StoppedUtc: null,
                GameTitleRu: "Проверка",
                Endpoint: "203.0.113.9",
                HasGame: true,
                IsMeasurable: true,
                BaselineRttMs: 100,
                FinalRttMs: null,
                ImprovementMs: null,
                Actions: [],
                RollbackStateRu: "Неизвестно (сессия без итога)",
                IsCompleted: false,
                StatusRu: string.Empty,
                Outcome: "NotFinished");

            var other = new SessionHistoryRow(
                SessionId: "self-check-other-server",
                StartedUtc: DateTime.UtcNow.AddDays(-1),
                StoppedUtc: DateTime.UtcNow.AddDays(-1).AddMinutes(20),
                GameTitleRu: "Проверка",
                Endpoint: "198.51.100.7",
                HasGame: true,
                IsMeasurable: true,
                BaselineRttMs: 90,
                FinalRttMs: 80,
                ImprovementMs: 10,
                Actions: [],
                RollbackStateRu: "Изменения сняты полностью",
                IsCompleted: true,
                StatusRu: string.Empty,
                Outcome: "Completed");

            bool honestText = unfinished.ResultRu.Contains("сессия не завершена", StringComparison.Ordinal);
            bool noInventedNumber = unfinished.FinalRttMs is null && unfinished.ImprovementMs is null;

            SessionComparisonResult comparison = SessionComparison.Compare(unfinished, other);
            bool refusesDifferentServers = !comparison.Comparable
                && comparison.Lines.Any(line => line.Contains("разным серверам", StringComparison.Ordinal));

            IReadOnlyList<DailyRttPoint> daily = DailyRttHistory.Build([unfinished, other], "203.0.113.9");
            bool singleServerDaily = daily.Count == 1 && Math.Abs(daily[0].AverageRttMs - 100) < 0.01;

            if (!honestText || !noInventedNumber || !refusesDifferentServers || !singleServerDaily)
            {
                problems.Add("история сессий: разбор «до/после» или график по дням работает неверно " +
                    $"(незавершённая: {(honestText ? "да" : "нет")}, без выдуманных чисел: {(noInventedNumber ? "да" : "нет")}, " +
                    $"разные серверы: {(refusesDifferentServers ? "отказ" : "сравнил")}, график: {(singleServerDaily ? "да" : "нет")})");
                StartupLog.Note("ИСТОРИЯ СЕССИЙ: НЕ работает — числа или сравнение сессий расходятся с правилом честности");
                return;
            }

            StartupLog.Note("ИСТОРИЯ СЕССИЙ: вкладка «Сессии» на месте, до нажатия записей нет, " +
                "незавершённая сессия называется незавершённой, разные серверы не сравниваются, график — по одному серверу");
        }
        catch (Exception ex)
        {
            problems.Add($"история сессий (задача J): проверка упала ({ex.GetType().Name}: {ex.Message})");
            StartupLog.Note($"ИСТОРИЯ СЕССИЙ: НЕ работает — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Сколько линий графика реально нарисовано в разметке: геометрия из модели дошла до экрана.</summary>
    private static int CountPolylines(DependencyObject node)
    {
        int count = node is System.Windows.Shapes.Polyline ? 1 : 0;

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            count += CountPolylines(VisualTreeHelper.GetChild(node, i));
        }

        return count;
    }

    /// <summary>
    /// route print -4 руками: ровно та команда, чей вывод показывает и служба, — человек может
    /// сравнить таблицу в консоли с тем, что увидела программа. При ошибке — пустая строка: «не
    /// прочитали» не превращаем в «маршрутов нет».
    /// </summary>
    private static string RunRoutePrint()
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("route", "print -4")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Ответ программы на нажатие должен быть виден на экране, а не в модальном окне.
    ///
    /// Так поймали дефект живой Windows: окно сообщения с MessageBoxOptions.DefaultDesktopOnly
    /// держало главное окно в «(Not Responding)» — окно замирало на десять секунд, меню не работало.
    /// Здесь проверяется обратное: карточка ответа существует, текст из модели доходит до разметки,
    /// карточка становится видимой, а после закрытия — снова скрывается.
    /// </summary>
    private static void CheckNotice(App app, List<string> problems)
    {
        MainWindow? window = app.Shell;
        if (window?.Content is not FrameworkElement root)
        {
            return;
        }

        ShowAllSections(window);

        if (FindNamed<TextBlock>(root, "NoticeText") is not { } notice)
        {
            problems.Add("нет карточки ответа на главном экране (NoticeText) — ответ снова уйдёт в модальное окно");
            StartupLog.Warn("Ответ программы: карточки NoticeText в окне нет");
            return;
        }

        app.MainScreen.ShowNotice("Проверка", "Это проверочный ответ программы.");
        root.UpdateLayout();

        bool textArrived = notice.Text.Contains("проверочный ответ", StringComparison.Ordinal);
        bool visible = app.MainScreen.IsNoticeVisible;
        app.MainScreen.ShowNotice(null, null);
        root.UpdateLayout();

        if (!textArrived || !visible)
        {
            problems.Add("карточка ответа есть, но текст до неё не дошёл — ответ программы пользователь не увидит");
            StartupLog.Warn("Ответ программы: текст не дошёл до разметки");
            return;
        }

        if (app.MainScreen.IsNoticeVisible)
        {
            problems.Add("ответ программы нельзя убрать с экрана");
            return;
        }

        StartupLog.Note("Ответ программы: карточка на экране, текст доходит, закрывается");
    }

    /// <summary>Найти элемент с таким x:Name — по имени, а не по типу («NoticeText»).</summary>
    private static T? FindNamed<T>(DependencyObject node, string name) where T : FrameworkElement
    {
        if (node is T element && element.Name == name)
        {
            return element;
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            if (FindNamed<T>(VisualTreeHelper.GetChild(node, i), name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void ShowAllSections(MainWindow window)
    {
        foreach (UIElement section in window.SectionsForCheck)
        {
            section.Visibility = Visibility.Visible;
        }

        if (window.Content is FrameworkElement root)
        {
            root.UpdateLayout();
        }
    }

    /// <summary>
    /// Раскладка: карточки показателей не должны быть сжаты. Это тоже настоящий дефект со снимка
    /// окна — цифры «пинг сейчас», «стало лучше», «до оптимизации» и «оценка качества» сжимались
    /// в узкие полосы у правого края, потому что колонку с кнопкой растянула подпись под ней.
    ///
    /// Проверяется ширина, а не «есть элемент»: элемент был, а прочитать его было нельзя.
    /// </summary>
    private static void CheckLayout(App app, List<string> problems)
    {
        if (app.Shell?.Content is not FrameworkElement)
        {
            return;
        }

        object? page = app.Shell.SectionsForCheck.FirstOrDefault(section => section.GetType().Name == "OptimizeView");
        if (page is not FrameworkElement view)
        {
            problems.Add("страница «Оптимизация» не найдена — проверять раскладку не на чем");
            return;
        }

        const double MinCardWidth = 110;

        // Задача F: четыре статичные карточки показателей (StatPing…StatQuality) заменены
        // графиком сети. Сжатая карточка графика — это нечитаемые оси и легенда, поэтому
        // проверяется она: ширина не меньше порога, тем же правилом, что и старые карточки.
        if (view.FindName("ChartCard") is not FrameworkElement chart)
        {
            problems.Add("в окне нет карточки показателя «ChartCard»");
            return;
        }

        if (chart.ActualWidth + 0.5 < MinCardWidth)
        {
            problems.Add($"карточки показателей сжаты: график сети — {Math.Round(chart.ActualWidth)} точек");
            StartupLog.Warn($"Раскладка: карточка графика сети сжата — {Math.Round(chart.ActualWidth)} точек");
            return;
        }

        StartupLog.Note($"Раскладка: карточка графика сети не сжата ({Math.Round(chart.ActualWidth)} точек)");
    }

    /// <summary>
    /// Ничего не выходит за правый край окна. Полоса содержимого прокручивается только вниз
    /// (<c>HorizontalScrollBarVisibility="Disabled"</c>), поэтому всё, что шире окна, обрезано справа:
    /// человек видит половину карточки и доскроллить до неё не может. Этот дефект уже был —
    /// его заметили на снимке окна, но тогда ширину сравнивали глазами.
    ///
    /// Проверяются две ширины: та, на которой окно построилось, и минимальная из разметки (860).
    /// На минимальной карточки теснее всего, и именно туда человек попадает, потянув за край окна.
    /// После проверки раскладка возвращается к прежней: следующие проверки ждут штатное окно.
    ///
    /// Сравниваются границы элементов, отсчитанные от корня окна, а не <c>ActualWidth</c> по
    /// отдельности: у элемента есть поле, рамка и масштаб интерфейса, и левый край не всегда ноль.
    /// </summary>
    private static void CheckNothingIsCutOff(App app, List<string> problems)
    {
        if (app.Shell?.Content is not FrameworkElement root || app.Shell is null)
        {
            return;
        }

        var usual = new Size(root.ActualWidth, root.ActualHeight);
        if (usual.Width <= 0 || usual.Height <= 0)
        {
            problems.Add("ширина окна не измерилась — проверить правый край не на чем");
            return;
        }

        try
        {
            foreach (double width in LayoutOverflow.CheckWidths(usual.Width, app.Shell.MinWidth))
            {
                LayOutAt(root, width, usual.Height);
                CheckCutOffAt(root, width, problems);
            }
        }
        finally
        {
            LayOutAt(root, usual.Width, usual.Height);
        }
    }

    /// <summary>Пересобрать раскладку окна под заданную ширину — теми же вызовами, что при старте окна.</summary>
    private static void LayOutAt(FrameworkElement root, double width, double height)
    {
        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
    }

    /// <summary>Один замер: что вышло за правый край на этой ширине.</summary>
    private static void CheckCutOffAt(FrameworkElement root, double width, List<string> problems)
    {
        double limit = root.ActualWidth;
        if (limit <= 0)
        {
            problems.Add($"ширина {Math.Round(width)} не измерилась — проверить правый край не на чем");
            return;
        }

        var boxes = new List<LayoutBox>();
        CollectLayoutBoxes(root, root, boxes, depth: 0, parentName: string.Empty);

        // Худшее — первым: в журнал и аннотацию попадает начало списка, и там должен стоять тот
        // элемент, который вылез дальше всех, а не тот, что случайно обошли раньше.
        IReadOnlyList<LayoutBox> outside = LayoutOverflow
            .Find(boxes, limit)
            .OrderByDescending(box => box.Right)
            .ToList();
        if (outside.Count == 0)
        {
            StartupLog.Note($"Раскладка: на ширине {Math.Round(limit)} точек за правый край ничего не выходит — просмотрено {boxes.Count} элементов");
            return;
        }

        string report = LayoutOverflow.DescribeRu(outside, limit);
        problems.Add($"элементы выходят за край окна на ширине {Math.Round(limit)}: {report}");
        StartupLog.Warn($"Раскладка: на ширине {Math.Round(limit)} точек {outside.Count} элементов обрезаны справа: {report}");
    }

    /// <summary>
    /// Обойти видимое дерево и собрать границы элементов от корня окна. Свёрнутое пропускается
    /// (места не занимает), декор со свечением — тоже: он выходит за край нарочно, под обрезкой.
    /// Блок с прокруткой вбок пропускается целиком: его содержимое можно докрутить — это не обрез.
    /// </summary>
    private static void CollectLayoutBoxes(
        FrameworkElement root,
        DependencyObject node,
        List<LayoutBox> boxes,
        int depth,
        string parentName)
    {
        if (depth > 64)
        {
            return;
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is not FrameworkElement element)
            {
                continue;
            }

            if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible)
            {
                continue;
            }

            if (element is ScrollViewer scroller &&
                scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                continue;
            }

            try
            {
                Rect bounds = element.TransformToAncestor(root)
                    .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                string name = DescribeLayoutElement(element);
                boxes.Add(new LayoutBox(
                    parentName.Length == 0 ? name : $"{name} в {parentName}",
                    bounds.Left,
                    bounds.Width));
            }
            catch (InvalidOperationException)
            {
                continue;   // элемент не из этого дерева — сравнивать нечего
            }

            CollectLayoutBoxes(root, element, boxes, depth + 1, DescribeLayoutElement(element));
        }
    }

    /// <summary>
    /// Имя элемента для отчёта. <c>x:Name</c> важнее типа — по нему ищут в разметке, — а если имени нет,
    /// называются первые слова текста: «TextBlock «Готово: пакет…»» ищется в разметке мгновенно, а
    /// «TextBlock» без ничего не ищется никак.
    /// </summary>
    private static string DescribeLayoutElement(FrameworkElement element)
    {
        string type = element.GetType().Name;
        string own = string.IsNullOrEmpty(element.Name) ? type : $"{type} «{element.Name}»";
        if (element is not TextBlock text || string.IsNullOrWhiteSpace(text.Text))
        {
            return own;
        }

        string snippet = text.Text.Trim().Replace("\r", " ").Replace("\n", " ");
        if (snippet.Length > 28)
        {
            snippet = snippet[..28] + "…";
        }

        return $"{own} «{snippet}»";
    }

    /// <summary>
    /// Абзацы должны переноситься по строкам. Это ещё одна ошибка со снимка окна: длинные
    /// пояснения рисовались одной строкой и обрезались по правому краю, потому что именованный
    /// стиль (Caption, Title) отменял неявный стиль с TextWrapping=Wrap.
    ///
    /// Проверяются именно длинные надписи: короткие («пинг сейчас») переносить не нужно.
    /// </summary>
    private static void CheckParagraphWrapping(App app, List<string> problems)
    {
        if (app.Shell?.Content is not DependencyObject root)
        {
            return;
        }

        var broken = new List<string>();
        CollectUnwrappedLongText(root, broken);

        if (broken.Count == 0)
        {
            StartupLog.Note("Перенос строк: длинные пояснения переносятся");
            return;
        }

        problems.Add($"длинные надписи не переносятся по строкам (обрезаются): {string.Join(" | ", broken.Take(3))}");
        StartupLog.Warn($"Перенос строк: не переносятся — {string.Join(" | ", broken.Take(3))}");
    }

    private static void CollectUnwrappedLongText(DependencyObject node, List<string> broken)
    {
        if (node is System.Windows.Controls.TextBlock { Text: { Length: > 60 } text } block
            && block.TextWrapping == TextWrapping.NoWrap)
        {
            broken.Add(text[..Math.Min(48, text.Length)] + "…");
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            CollectUnwrappedLongText(VisualTreeHelper.GetChild(node, i), broken);
        }
    }

    private static void CollectButtonTexts(DependencyObject node, List<string> texts)
    {
        if (node is Button { Content: string content } && content.Length > 0)
        {
            texts.Add(content);
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            CollectButtonTexts(VisualTreeHelper.GetChild(node, i), texts);
        }
    }

    private static void CollectTextBlockTexts(DependencyObject node, List<string> texts)
    {
        if (node is TextBlock { Text: { Length: > 0 } text })
        {
            texts.Add(text);
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            CollectTextBlockTexts(VisualTreeHelper.GetChild(node, i), texts);
        }
    }

    /// <summary>
    /// Значок в трее. На машине без рабочего стола (такое бывает у сборочной) он может не создаться —
    /// это предупреждение, а не отказ: без трея программа работает, без окна — нет.
    /// </summary>
    private static void CheckTray(App app, List<string> problems)
    {
        try
        {
            if (app.Tray is null)
            {
                problems.Add("значок в трее не создался");
                StartupLog.Warn("Трей: не создался");
                return;
            }

            StartupLog.Note("Трей: значок создан, меню собрано");
        }
        catch (Exception ex)
        {
            StartupLog.Warn($"Трей: {ex.GetType().Name}: {ex.Message}");
            problems.Add($"трей: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Служба: проверяем связь, но провалом это не считаем. Программа без службы запускается и честно
    /// говорит «будут только замеры» — так и задумано. Здесь важно показать человеку, что именно происходит.
    /// </summary>
    private static void CheckService(List<string> problems)
    {
        try
        {
            using var channel = new NamedPipeEngineChannel(null, TimeSpan.FromSeconds(3));
            var client = new EngineClient(channel);
            (bool ok, string message) = client.PingAsync().GetAwaiter().GetResult();

            if (ok)
            {
                StartupLog.Note($"Служба: {message}");
                StartupLog.Note($"СЛУЖБА: отвечает — {message}");

                // C5: версии окна и службы сверяются на живой машине, а не только в тестах.
                StartupLog.Note($"ВЕРСИИ: {client.VersionsNoteRu}");
                if (client.VersionState == EngineVersionState.ProtocolMismatch)
                {
                    StartupLog.Note("ВЕРСИИ: протоколы не совпадают — проверка считается проваленной");
                    problems.Add(client.VersionsNoteRu);
                }

                ServiceAnswered = true;
                return;
            }

            StartupLog.Warn($"Служба ответила отказом: {message}");
            StartupLog.Note("СЛУЖБА: отказала");
            problems.Add($"служба отказала: {message}");
        }
        catch (Exception ex)
        {
            StartupLog.Warn($"Служба не отвечает ({ex.GetType().Name}: {ex.Message}). Это нормально, если она ещё не установлена: программа запустится, но менять настройки не сможет.");
            StartupLog.Note("СЛУЖБА: не отвечает");
            problems.Add("служба не отвечает — установите её установщиком, иначе будут только замеры");
        }
    }

    private static int Finish(List<string> problems, int code)
    {
        if (problems.Count == 0)
        {
            StartupLog.Step("Самопроверка пройдена: окно, разделы, трей и каталог собрались. Ничего не изменено.");
        }
        else
        {
            StartupLog.Warn($"Самопроверка завершена с замечаниями ({problems.Count}): {string.Join("; ", problems)}");
        }

        // Замечания про службу — не отказ: без неё программа запускается и честно говорит «будут только замеры».
        return code;
    }

    /// <summary>Сколько элементов в визуальном дереве — грубая, но честная мера «разметка загрузилась».</summary>
    private static int CountElements(DependencyObject node)
    {
        int count = 1;
        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            count += CountElements(VisualTreeHelper.GetChild(node, i));
        }

        return count;
    }

    /// <summary>Есть ли в дереве элемент нужного типа (разделы называются по своим классам).</summary>
    private static object? FindByName(DependencyObject node, string typeName)
    {
        if (node.GetType().Name == typeName)
        {
            return node;
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            if (FindByName(VisualTreeHelper.GetChild(node, i), typeName) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Есть ли в дереве надпись с таким текстом. Так проверяется, что окно — это наше окно.</summary>
    private static bool HasText(DependencyObject node, string text)
    {
        if (node is TextBlock { Text: { } value } && value.Contains(text, StringComparison.Ordinal))
        {
            return true;
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
        {
            if (HasText(VisualTreeHelper.GetChild(node, i), text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Сторож: если самопроверка не уложилась в отведённое время, процесс завершается сам.
    /// Без этого зависшая самопроверка держала бы сборочную машину до конца её лимита.
    /// </summary>
    internal static void ArmWatchdog(int seconds)
    {
        var timer = new System.Timers.Timer(seconds * 1000.0) { AutoReset = false };
        timer.Elapsed += (_, _) =>
        {
            StartupLog.Warn($"Самопроверка не уложилась в {seconds} с — завершаю принудительно.");
            Environment.Exit(TimedOut);
        };
        timer.Start();
    }
}
