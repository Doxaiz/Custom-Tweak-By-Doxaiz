using System.Text.Json;
using PingForge.App.ViewModels;
using PingForge.Service.Protocol;

namespace PingForge.EngineSmoke;

/// <summary>
/// Живая проверка службы: подключается к её каналу и задаёт те же вопросы, что и окно программы,
/// но без человека. Нужна там, где окно не запустить — на сборочной машине с Windows и на машине
/// разработчика: до этого «поведение службы проверяется только на Windows» было словами, а не проверкой.
///
/// Что именно проверяется:
///   1) служба отвечает на «проверить связь» и называет ту же версию протокола;
///   2) список команд и версия протокола совпадают с тем, что знает эта сборка (разные сборки — честный отказ);
///   3) «состояние» отдаётся без ошибки;
///   4) чтение состояния системы возвращает разборчивый JSON, и в нём действительно что-то прочитано,
///      а не одни пустые поля: пустота — это «не прочитано», и её видно счётчиком;
///   5) хвост журнала отдаётся без ошибки.
///
/// Чего проверка не делает: ничего не меняет в системе. Ни одной команды, меняющей настройки, здесь нет —
/// нарочно, чтобы её можно было запускать где угодно и когда угодно.
///
/// Коды возврата: 0 — всё хорошо, 1 — служба недоступна, 2 — служба отказала в команде,
/// 3 — ответ пришёл, но разобрать его нельзя.
/// </summary>
internal static class Program
{
    /// <summary>Сколько полей состояния должно быть прочитано, чтобы это считалось работой, а не пустышкой.</summary>
    private const int MinReadFields = 8;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Режимы живой проверки C7 задаются флагами; первый аргумент без «--» — по-прежнему
        // имя канала. Без флагов поведение прежнее: пять читающих проверок, ничего не меняем.
        var flags = new HashSet<string>(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)), StringComparer.Ordinal);
        string? pipeName = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        bool specialMode = flags.Contains("--expect-identity") || flags.Contains("--try-apply")
            || flags.Contains("--apply-and-rollback") || flags.Contains("--rollback-only")
            || flags.Contains("--realtime");
        Console.WriteLine("Custom Tweak By Doxaiz — живая проверка службы");

        // Живая оптимизация ищет поток и меряет «до» — это дольше обычного запроса,
        // поэтому для неё предел канала поднят до двух минут сверх срока самой команды.
        TimeSpan channelTimeout = flags.Contains("--realtime") ? TimeSpan.FromSeconds(200) : TimeSpan.FromSeconds(30);
        using var channel = new NamedPipeEngineChannel(pipeName, channelTimeout);
        // Печатаем имя канала, к которому ПОДКЛЮЧИЛИСЬ: без аргумента клиент сам выбирает канал
        // по своим правам (основной от администратора, иначе чтение), и прежний текст мог соврать.
        Console.WriteLine(pipeName is null
            ? $"Канал: {channel.PipeName} (выбран по правам процесса)"
            : $"Канал: {pipeName} (задан аргументом)");
        Console.WriteLine();
        var client = new EngineClient(channel);

        if (specialMode)
        {
            return await RunSpecialModeAsync(client, flags);
        }

        try
        {
            // 1. Служба вообще на связи?
            (bool pingOk, string pingMessage) = await client.PingAsync();
            Console.WriteLine($"[1/7] Связь: {pingMessage}");
            if (!pingOk)
            {
                Console.WriteLine("Служба ответила отказом на «проверить связь» — дальше спрашивать нечего.");
                return 2;
            }

            Console.WriteLine($"      Версия протокола, названная службой: {client.ProtocolVersion?.ToString() ?? "не названа"}");

            // 2. Список команд должен совпадать с тем, что знает эта сборка. Сверяем не «сколько»,
            // а какие именно: счётчик совпадёт и тогда, когда одну команду переименовали, а другую потеряли.
            string capabilities = await client.GetCapabilitiesAsync();
            (bool commandsMatch, string commandsNote) = CompareCommands(capabilities);
            Console.WriteLine($"[2/7] Список команд: {commandsNote}");
            if (!commandsMatch)
            {
                Console.WriteLine("      Служба и утилита собраны из разных версий — это отказ версий, а не мелочь.");
                Console.WriteLine(Indent(capabilities));
                return 3;
            }

            // 3. Состояние.
            EngineResponse status = await client.SendAsync(EngineCommandKind.Status);
            Console.WriteLine($"[3/7] Состояние: {(status.Ok ? status.MessageRu : "ОТКАЗ: " + status.MessageRu)}");
            if (!status.Ok)
            {
                return 2;
            }

            // 4. Чтение состояния системы — самое интересное: тут служба трогает настоящую Windows.
            EngineResponse state = await client.SendAsync(EngineCommandKind.ReadSystemState);
            Console.WriteLine($"[4/7] Чтение состояния системы: {(state.Ok ? "прочитано" : "ОТКАЗ: " + state.MessageRu)}");
            if (!state.Ok)
            {
                return 2;
            }

            if (state.DataJson is null)
            {
                Console.WriteLine("      Данных нет — а без них интерфейсу показывать нечего.");
                return 3;
            }

            int stateProblems = ReportState(state.DataJson);
            if (stateProblems > 0)
            {
                return 3;
            }

            // 5. Журнал.
            EngineResponse journal = await client.SendAsync(EngineCommandKind.JournalTail);
            Console.WriteLine($"[5/7] Хвост журнала: {(journal.Ok ? journal.MessageRu : "ОТКАЗ: " + journal.MessageRu)}");
            if (!journal.Ok)
            {
                return 2;
            }

            // 6. Узлы маршрутизации (задача B): только чтение — таблица маршрутов и список адаптеров.
            // Проверка принципиально честная: если на машине нет второго канала или VPN, служба обязана
            // сказать «кандидатов кроме текущего шлюза нет», а не показать выдуманный список.
            EngineResponse routes = await client.SendAsync(EngineCommandKind.RouteDiscover);
            Console.WriteLine($"[6/7] Узлы маршрутизации: {(routes.Ok ? routes.MessageRu : "ОТКАЗ: " + routes.MessageRu)}");
            if (!routes.Ok)
            {
                return 2;
            }

            int invented = CountInventedNodes(routes.DataJson);
            if (invented > 0)
            {
                Console.WriteLine($"ПРОВАЛ: в ответе {invented} узл(ов) с нечестным источником — «серверов PingForge» быть не должно.");
                return 3;
            }

            // 7. Следы перед удалением (задача T): команда обязана отвечать числами из журнала и
            // снапшотов. Отказ здесь — это сломанное удаление, а не мелочь: скрипт снятия опирается на неё.
            EngineResponse traces = await client.SendAsync(EngineCommandKind.UninstallCheck);
            Console.WriteLine($"[7/7] Следы перед удалением: {(traces.Ok ? traces.MessageRu : "ОТКАЗ: " + traces.MessageRu)}");
            if (!traces.Ok)
            {
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine("Итог: служба отвечает, состояние читается, список команд совпадает, узлы маршрутизации честные, следы перед удалением названы. Ничего не изменено.");
            return 0;
        }
        catch (EngineUnavailableException ex)
        {
            Console.WriteLine($"Служба недоступна: {ex.Message}");
            Console.WriteLine("Проверьте, что служба PingForge установлена и запущена: Get-Service PingForge");
            return 1;
        }
    }

    /// <summary>
    /// Режимы C7: проверка личности, попытка применения без прав и применение с откатом.
    /// Каждый режим печатает то, что прислала служба, дословно — журнал прогона и есть доказательство.
    /// </summary>
    /// <summary>
    /// Считает узлы с источником вне белого списка («текущий шлюз», «второй шлюз», «VPN-адаптер …»,
    /// «введён вручную»). Это живая версия теста «выдуманных серверов PingForge нет».
    /// </summary>
    private static int CountInventedNodes(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return 1;
            }

            int bad = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                string source = item.TryGetProperty("source", out var s1) ? s1.GetString() ?? "" : "";
                bool honest = source == "текущий шлюз" || source == "второй шлюз" || source == "введён вручную"
                    || source.StartsWith("VPN-адаптер ", StringComparison.Ordinal);
                if (!honest)
                {
                    bad++;
                }
            }

            return bad;
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    private static async Task<int> RunSpecialModeAsync(EngineClient client, HashSet<string> flags)
    {
        try
        {
            if (flags.Contains("--rollback-only"))
            {
                EngineResponse rollback = await client.SendAsync(EngineCommandKind.RollbackSnapshot);
                Console.WriteLine($"Откат последнего снапшота: {(rollback.Ok ? rollback.MessageRu : "ОТКАЗ: " + rollback.MessageRu)}");
                return rollback.Ok ? 0 : 2;
            }

            // Во всех остальных режимах сначала — ping: служба называет, кого она видит.
            EngineResponse ping = await client.SendAsync(EngineCommandKind.Ping);
            Console.WriteLine($"Связь: {(ping.Ok ? ping.MessageRu : "ОТКАЗ: " + ping.MessageRu)}");
            if (!ping.Ok)
            {
                return 2;
            }

            (string clientName, string clientSid, bool clientIsAdmin) = ReadIdentity(ping.DataJson);
            Console.WriteLine($"Служба видит клиента: clientName={clientName}; clientSid={clientSid}; clientIsAdmin={clientIsAdmin}");

            if (flags.Contains("--expect-identity"))
            {
                return CheckIdentity(clientName, clientSid, clientIsAdmin);
            }

            if (flags.Contains("--try-apply"))
            {
                // Несуществующий твик нарочно: если проверка прав работает, отказ придёт ДО
                // поиска твика. Ответ «твик не найден» означал бы, что до применения дошло.
                EngineResponse attempt = await client.SendAsync(
                    EngineCommandKind.ApplyTweaks,
                    new Dictionary<string, string> { ["tweaks"] = "C7_NO_SUCH_TWEAK", ["restorePoint"] = "false" });
                Console.WriteLine($"Попытка применения: {(attempt.Ok ? attempt.MessageRu : "ОТКАЗ: " + attempt.MessageRu)}");
                if (attempt.Ok)
                {
                    Console.WriteLine("ПРОВАЛ: применение несуществующего твика вдруг удалось.");
                    return 2;
                }

                if (attempt.MessageRu.Contains("в каталоге нет", StringComparison.Ordinal))
                {
                    Console.WriteLine("ПРОВАЛ: отказ — «твик не найден», значит проверка прав пройдена или пропущена, а не сработала.");
                    return 2;
                }

                string layer = attempt.MessageRu.Contains("только для чтения", StringComparison.Ordinal)
                    ? "C2 (канал только для чтения)"
                    : attempt.MessageRu.Contains("прав администратора нет", StringComparison.Ordinal)
                        ? "C1 (права клиента в диспетчере)"
                        : "канал/ACL (подключение или диспетчер)";
                Console.WriteLine($"Итог: отказ пришёл до применения, слой: {layer}. Ничего не изменено.");
                return 0;
            }

            if (flags.Contains("--realtime"))
            {
                // Живая проверка распознавания игрового потока на настоящей Windows: служба ищет игру
                // по живой таблице соединений и трассировке UDP, называет игру и адрес сервера,
                // а затем мы сразу останавливаем оптимизацию, чтобы ничего не осталось в системе.
                EngineResponse start = await client.SendAsync(EngineCommandKind.RealtimeStart);
                Console.WriteLine($"Старт: {(start.Ok ? start.MessageRu : "ОТКАЗ: " + start.MessageRu)}");
                Console.WriteLine($"StartData: {start.DataJson ?? "нет"}");

                EngineResponse stop;
                try
                {
                    stop = await client.SendAsync(EngineCommandKind.RealtimeStop);
                }
                catch (EngineUnavailableException)
                {
                    Console.WriteLine("Остановка не удалась: служба недоступна. Проверьте службу перед повторным запуском.");
                    return 2;
                }

                Console.WriteLine($"Остановка: {(stop.Ok ? stop.MessageRu : "ОТКАЗ: " + stop.MessageRu)}");
                Console.WriteLine($"StopData: {stop.DataJson ?? "нет"}");

                return start.Ok && stop.Ok ? 0 : 2;
            }

            if (flags.Contains("--apply-and-rollback"))
            {
                EngineResponse apply = await client.SendAsync(
                    EngineCommandKind.ApplyTweaks,
                    new Dictionary<string, string> { ["tweaks"] = "MENU_SHOW_DELAY_ZERO", ["restorePoint"] = "true" });
                Console.WriteLine($"Применение MENU_SHOW_DELAY_ZERO: {(apply.Ok ? apply.MessageRu : "ОТКАЗ: " + apply.MessageRu)}");
                if (!apply.Ok)
                {
                    return 2;
                }

                EngineResponse rollback = await client.SendAsync(EngineCommandKind.RollbackSnapshot);
                Console.WriteLine($"Откат: {(rollback.Ok ? rollback.MessageRu : "ОТКАЗ: " + rollback.MessageRu)}");
                return rollback.Ok ? 0 : 2;
            }

            return 0;
        }
        catch (EngineUnavailableException ex)
        {
            Console.WriteLine($"Служба недоступна: {ex.Message}");
            return 1;
        }
    }

    private static (string Name, string Sid, bool IsAdmin) ReadIdentity(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return (string.Empty, string.Empty, false);
        }

        try
        {
            using JsonDocument data = JsonDocument.Parse(dataJson);
            JsonElement root = data.RootElement;
            string name = root.TryGetProperty("clientName", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
            string sid = root.TryGetProperty("clientSid", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
            bool admin = root.TryGetProperty("clientIsAdmin", out JsonElement a) && a.ValueKind == JsonValueKind.True;
            return (name, sid, admin);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, false);
        }
    }

    /// <summary>
    /// Сличить личность от службы с токеном ЭТОГО процесса. Ловит обе ошибки C7 сразу:
    /// чужая функция дала бы SYSTEM/S-1-5-18, чужое право на токен — «не определён»/false.
    /// </summary>
    private static int CheckIdentity(string clientName, string clientSid, bool clientIsAdmin)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("ПРОВАЛ: --expect-identity имеет смысл только на Windows.");
            return 2;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        string expectedName = identity.Name;
        string expectedSid = identity.User?.Value ?? string.Empty;
        bool expectedAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Console.WriteLine($"Этот процесс: name={expectedName}; sid={expectedSid}; isAdmin={expectedAdmin}");

        var problems = new List<string>();
        if (!string.Equals(clientName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"имя не совпало: служба назвала «{clientName}»");
        }

        if (!string.Equals(clientSid, expectedSid, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"SID не совпал: служба назвала «{clientSid}»");
        }

        if (string.Equals(clientSid, "S-1-5-18", StringComparison.OrdinalIgnoreCase)
            || clientName.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("служба назвала клиентом саму себя (SYSTEM)");
        }

        if (clientIsAdmin != expectedAdmin)
        {
            problems.Add($"флаг администратора не совпал: служба сказала {clientIsAdmin}, у процесса {expectedAdmin}");
        }

        if (problems.Count > 0)
        {
            Console.WriteLine("ПРОВАЛ личности: " + string.Join("; ", problems));
            return 2;
        }

        Console.WriteLine("Итог: служба видит именно этот процесс — имя, SID и флаг администратора совпали.");
        return 0;
    }

    /// <summary>
    /// Сверить список команд службы с таблицей этой сборки: версия протокола, имена команд по порядку
    /// и то, какие из них требуют прав администратора. Названия и пояснения не сверяем — это тексты,
    /// их можно улучшать, не меняя протокол.
    /// </summary>
    private static (bool Matches, string Note) CompareCommands(string capabilitiesJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(capabilitiesJson);
        }
        catch (JsonException ex)
        {
            return (false, $"ответ не разбирается как JSON: {ex.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("protocolVersion", out JsonElement version)
                && version.ValueKind == JsonValueKind.Number
                && version.GetInt32() != EngineProtocol.Version)
            {
                return (false, $"версия протокола у службы {version.GetInt32()}, у этой сборки {EngineProtocol.Version}");
            }

            if (!root.TryGetProperty("commands", out JsonElement commands) || commands.ValueKind != JsonValueKind.Array)
            {
                return (false, "в ответе нет списка команд");
            }

            var fromService = new List<(string Wire, bool Elevated)>();
            foreach (JsonElement item in commands.EnumerateArray())
            {
                string wire = item.TryGetProperty("wire", out JsonElement wireValue) && wireValue.ValueKind == JsonValueKind.String
                    ? wireValue.GetString() ?? string.Empty
                    : string.Empty;
                bool elevated = item.TryGetProperty("needsElevation", out JsonElement elevatedValue)
                    && elevatedValue.ValueKind == JsonValueKind.True;
                fromService.Add((wire, elevated));
            }

            string[] expected = EngineCommandCatalog.All.Select(item => item.WireName).ToArray();
            string[] actual = fromService.Select(item => item.Wire).ToArray();

            string[] missing = expected.Except(actual, StringComparer.Ordinal).ToArray();
            string[] extra = actual.Except(expected, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
            {
                string note = $"РАСХОДИТСЯ: служба знает {actual.Length} команд, эта сборка — {expected.Length}";
                if (missing.Length > 0)
                {
                    note += $"; служба не знает: {string.Join(", ", missing)}";
                }

                if (extra.Length > 0)
                {
                    note += $"; лишние у службы: {string.Join(", ", extra)}";
                }

                return (false, note);
            }

            string[] rightsMismatch = EngineCommandCatalog.All
                .Where(item => item.NeedsElevation != fromService.First(pair => pair.Wire == item.WireName).Elevated)
                .Select(item => item.WireName)
                .ToArray();
            if (rightsMismatch.Length > 0)
            {
                return (false, $"права администратора расходятся у: {string.Join(", ", rightsMismatch)}");
            }

            return (true, $"совпадает: {actual.Length} команд, из них {fromService.Count(pair => pair.Elevated)} требуют прав администратора");
        }
    }

    /// <summary>
    /// Разобрать и напечатать прочитанное состояние. Возвращает число проблем: 0 — всё разборчиво.
    /// Пустые поля при этом не «исправляются» и не печатаются как ноль — они честно называются непрочитанными.
    /// </summary>
    private static int ReportState(string dataJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(dataJson);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"      Ответ не разбирается как JSON: {ex.Message}");
            return 1;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            var problems = 0;

            // Обязательный минимум: без этих полей интерфейс не соберёт даже шапку раздела.
            foreach (string required in new[] { "windowsEditionAndBuild", "isRunningAsAdministrator", "unresolvedFields" })
            {
                if (!root.TryGetProperty(required, out _))
                {
                    Console.WriteLine($"      НЕТ ОБЯЗАТЕЛЬНОГО ПОЛЯ: {required}");
                    problems++;
                }
            }

            string windows = Text(root, "windowsEditionAndBuild");
            if (windows.Length == 0)
            {
                Console.WriteLine("      Версия и сборка Windows не прочитаны.");
                problems++;
            }
            else if (!windows.StartsWith("Windows", StringComparison.Ordinal))
            {
                Console.WriteLine($"      Сборка Windows названа странно: «{windows}»");
                problems++;
            }

            // Печатаем всё, что прочитано, и отдельно — что не прочитано. Именно это увидит человек в окне.
            Console.WriteLine("      Что прочитано:");
            Console.WriteLine($"        версия и сборка Windows : {Or(windows, "не прочитано")}");
            Console.WriteLine($"        права администратора    : {Or(Flag(root, "isRunningAsAdministrator"), "не прочитано")}");
            Console.WriteLine($"        активный адаптер        : {Or(Text(root, "adapterDisplayName"), "не прочитано")}");
            Console.WriteLine($"        скорость линка          : {Units(Number(root, "linkSpeedMbps"), " Мбит/с")}");
            Console.WriteLine($"        MTU                     : {Units(Number(root, "currentMtuBytes"), " байт")}");
            Console.WriteLine($"        схема питания           : {Or(Text(root, "powerSchemeName"), "не прочитано")}");
            Console.WriteLine($"        автонастройка TCP       : {Or(Text(root, "tcpAutoTuningLevel"), "не прочитано")}");
            Console.WriteLine($"        провайдер перегрузки    : {Or(Text(root, "congestionProvider"), "не прочитано")}");
            Console.WriteLine($"        RSS / RSC / ECN         : {Or(Flag(root, "isRssEnabled"), "?")} / {Or(Flag(root, "isRscEnabled"), "?")} / {Or(Flag(root, "isEcnEnabled"), "?")}");
            Console.WriteLine($"        Nagle на активном NIC   : {Or(Flag(root, "isNagleDisabledOnActiveNic"), "не прочитано")}");
            Console.WriteLine($"        Game Mode               : {Or(Flag(root, "isGameModeEnabled"), "не прочитано")}");
            Console.WriteLine($"        оптимизация доставки P2P: {Or(Flag(root, "isDeliveryOptimizationP2pEnabled"), "не прочитано")}");
            Console.WriteLine($"        точка восстановления    : {Or(Flag(root, "isSystemRestoreEnabledOnSystemDrive"), "не прочитано")}");
            Console.WriteLine($"        таймер поднят           : {Or(Flag(root, "isTimerResolutionRaised"), "не прочитано")}");
            Console.WriteLine($"        DNS                     : {Or(List(root, "configuredDnsServers"), "не прочитано")}");
            Console.WriteLine($"        NDIS-фильтры            : {Or(List(root, "attachedNdisFilters"), "не прочитано")}");

            string[] unread = Lines(root, "unresolvedFields");
            Console.WriteLine(unread.Length == 0
                ? "      Не прочитано: ничего — все поля заполнены."
                : $"      Не прочитано ({unread.Length}): {string.Join("; ", unread)}");

            int read = CountReadFields(root);
            Console.WriteLine($"      Прочитано полей: {read} из 20 (плюс список непрочитанных).");
            if (read < MinReadFields)
            {
                Console.WriteLine($"      ЭТО МАЛО: ожидалось хотя бы {MinReadFields}. Похоже, чтение не работает.");
                problems++;
            }

            return problems;
        }
    }

    /// <summary>Сколько полей состояния заполнено: null'ов среди них быть не должно ни в одном случае.</summary>
    private static int CountReadFields(JsonElement root)
    {
        string[] scalarNames =
        [
            "windowsEditionAndBuild", "adapterDisplayName", "powerSchemeGuid", "powerSchemeName",
            "tcpAutoTuningLevel", "congestionProvider",
        ];
        string[] flagNames =
        [
            "isRunningAsAdministrator", "adapterIsUp", "isRssEnabled", "isRscEnabled", "isEcnEnabled",
            "isNagleDisabledOnActiveNic", "isGameModeEnabled", "isDeliveryOptimizationP2pEnabled",
            "isSystemRestoreEnabledOnSystemDrive", "isTimerResolutionRaised",
        ];
        string[] numberNames = ["linkSpeedMbps", "currentMtuBytes"];
        string[] listNames = ["configuredDnsServers", "attachedNdisFilters"];

        int read = 0;
        foreach (string name in scalarNames)
        {
            if (Text(root, name).Length > 0)
            {
                read++;
            }
        }

        foreach (string name in flagNames)
        {
            if (Flag(root, name) is not null)
            {
                read++;
            }
        }

        foreach (string name in numberNames)
        {
            if (Number(root, name) is not null)
            {
                read++;
            }
        }

        foreach (string name in listNames)
        {
            if (Lines(root, name).Length > 0)
            {
                read++;
            }
        }

        return read;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.ToString()
            : string.Empty;

    private static string? Flag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => "да",
            JsonValueKind.False => "нет",
            _ => null,
        };
    }

    private static string[] Lines(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

    private static string List(JsonElement root, string name)
    {
        string[] items = Lines(root, name);
        return items.Length == 0 ? string.Empty : string.Join(", ", items);
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>Число с единицей измерения — но только если оно вообще прочитано: «0 Мбит/с» было бы враньём.</summary>
    private static string Units(string value, string unit) => value.Length == 0 ? "не прочитано" : value + unit;

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => "      " + line.TrimEnd()));
}
