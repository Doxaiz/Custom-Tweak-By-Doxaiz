using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PingForge.App.ViewModels;
using PingForge.App.Views;

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
        CheckLayout(app, problems);
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
            || problem.Contains("не переносятся", StringComparison.Ordinal)))
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
            StartupLog.Note($"Каталог твиков: {tweaks} штук, в пресете «низкий пинг» {presets}");

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
        string[] cards = ["StatPing", "StatBetter", "StatBefore", "StatQuality"];
        var squeezed = new List<string>();

        foreach (string name in cards)
        {
            if (view.FindName(name) is not FrameworkElement card)
            {
                problems.Add($"в окне нет карточки показателя «{name}»");
                continue;
            }

            System.Diagnostics.Debug.WriteLine($"{name}: {card.ActualWidth}×{card.ActualHeight}");
            if (card.ActualWidth + 0.5 < MinCardWidth)
            {
                squeezed.Add($"{name} — {Math.Round(card.ActualWidth)} точек");
            }
        }

        if (squeezed.Count > 0)
        {
            problems.Add($"карточки показателей сжаты: {string.Join("; ", squeezed)}");
            StartupLog.Warn($"Раскладка: карточки показателей сжаты — {string.Join("; ", squeezed)}");
            return;
        }

        StartupLog.Note($"Раскладка: карточки показателей не сжаты (проверено {cards.Length} штук)");
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
