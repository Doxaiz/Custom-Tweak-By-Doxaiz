using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PingForge.Core.Models;
using PingForge.Probing;

namespace PingForge.App.ViewModels;

/// <summary>
/// Семь проверок кнопки «Проверить сеть» (задача G) — в том порядке, в каком их видит человек.
/// Bufferbloat стоит на своём месте в списке, но запускается отдельно: только по явному согласию.
/// </summary>
public enum NetworkCheckItemKind
{
    Mtu,
    Dns,
    DoubleNat,
    Bufferbloat,
    Wifi,
    BackgroundDownloads,
    RightsAndService,
}

/// <summary>
/// Статус пункта проверки. Три обязательных состояния задачи — «в порядке», «стоит поправить»,
/// «проверить нельзя»; плюс «не применимо» (проводной адаптер и Wi-Fi) и «не проверено»
/// (bufferbloat до согласия). «Проверить нельзя» никогда не маскируется под «всё хорошо».
/// </summary>
public enum NetworkCheckStatus
{
    Ok,
    Attention,
    NotApplicable,
    CannotCheck,
    NotChecked,
}

/// <summary>Насколько срочная находка. Info — строка диагностики без действия (права и служба).</summary>
public enum NetworkCheckPriority
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Итог одного пункта: статус, цифры замера, что делать и — если есть — код твика каталога.</summary>
public sealed record NetworkCheckItemResult(
    NetworkCheckItemKind Kind,
    string TitleRu,
    NetworkCheckStatus Status,
    NetworkCheckPriority Priority,
    string DetailRu,
    string ActionRu,
    string? TweakCode = null)
{
    /// <summary>Статус словами для списка — формулировки живут в одном месте, а не в разметке.</summary>
    public string StatusRu => Status switch
    {
        NetworkCheckStatus.Ok => "В порядке",
        NetworkCheckStatus.Attention => "Стоит поправить",
        NetworkCheckStatus.NotApplicable => "Не применимо",
        NetworkCheckStatus.CannotCheck => "Проверить нельзя",
        _ => "Не проверено",
    };

    /// <summary>Приоритет словами для итога.</summary>
    public string PriorityRu => Priority switch
    {
        NetworkCheckPriority.High => "Высокий",
        NetworkCheckPriority.Medium => "Средний",
        NetworkCheckPriority.Low => "Низкий",
        _ => "Для сведения",
    };
}

/// <summary>
/// Результат основного прогона: шесть пунктов, которым согласие не нужно. Bufferbloat сюда
/// НЕ входит никогда — его слот держит модель и заполняет только после нажатия «Понятно».
/// </summary>
public sealed record NetworkSelfCheckReport(IReadOnlyList<NetworkCheckItemResult> Items);

/// <summary>
/// Точка входа «Проверить сеть» для интерфейса. Реализация собирает существующие измерения проекта
/// (DF-MTU, бенчмарк DNS, трассировка первых хопов, чтение окружения, netsh только на чтение)
/// и превращает их в пункты готовыми оценщиками <see cref="NetworkCheckEvaluators"/>.
/// Сама проверка ничего не меняет в системе.
/// </summary>
public interface INetworkSelfCheck
{
    /// <summary>Шесть проверок чтения и замеров. Каждый пункт — с одним из честных статусов.</summary>
    Task<NetworkSelfCheckReport> RunAsync(CancellationToken ct = default);

    /// <summary>Замер bufferbloat — ТОЛЬКО после согласия человека: канал нагружается на 10 секунд.</summary>
    Task<NetworkCheckItemResult> RunBufferbloatAsync(CancellationToken ct = default);
}

/// <summary>
/// Проверка сети недоступна в этой сборке (тесты моделей и аварийные ветки): честное
/// «проверить нельзя» по каждому пункту вместо тишины или выдуманного «всё хорошо».
/// </summary>
internal sealed class UnavailableNetworkSelfCheck : INetworkSelfCheck
{
    private const string ReasonRu = "Проверка сети не подключена в этой сборке окна: измерить нечем, выдумывать не будем.";

    public Task<NetworkSelfCheckReport> RunAsync(CancellationToken ct = default) =>
        Task.FromResult(new NetworkSelfCheckReport(
        [
            Item(NetworkCheckItemKind.Mtu, "MTU и фрагментация"),
            Item(NetworkCheckItemKind.Dns, "DNS"),
            Item(NetworkCheckItemKind.DoubleNat, "Двойной NAT"),
            Item(NetworkCheckItemKind.Wifi, "Шум Wi-Fi и каналы"),
            Item(NetworkCheckItemKind.BackgroundDownloads, "Фоновые загрузки"),
            Item(NetworkCheckItemKind.RightsAndService, "Права и служба"),
        ]));

    public Task<NetworkCheckItemResult> RunBufferbloatAsync(CancellationToken ct = default) =>
        Task.FromResult(Item(NetworkCheckItemKind.Bufferbloat, "Bufferbloat (задержка под нагрузкой)"));

    private static NetworkCheckItemResult Item(NetworkCheckItemKind kind, string titleRu) =>
        new(kind, titleRu, NetworkCheckStatus.CannotCheck, NetworkCheckPriority.Info, ReasonRu, string.Empty);
}

/// <summary>Снимок эфира Wi-Fi из вывода netsh: сколько сетей видно и как точки лежат по каналам.</summary>
public sealed record WifiScanResult(int NetworkCount, IReadOnlyDictionary<int, int> BssidsPerChannel)
{
    /// <summary>Самое большое число точек на одном канале; 0, если каналы не нашлись.</summary>
    public int BusiestChannelBssids => BssidsPerChannel.Count == 0 ? 0 : BssidsPerChannel.Values.Max();
}

/// <summary>
/// Разбор `netsh wlan show networks mode=bssid` (только чтение). Сети считаются по заголовкам SSID,
/// каналы — по строкам «Channel»/«Канал» у каждой точки (BSSID): netsh локализован, поэтому
/// разбираются оба языка, а шапке «видимо N сетей» мы не верим — считаем сами.
/// </summary>
public static partial class WifiNetworksParser
{
    [GeneratedRegex(@"(?i)(channel|канал)\s*:\s*(\d+)")]
    private static partial Regex ChannelLine();

    public static WifiScanResult Parse(string netshOutput)
    {
        ArgumentNullException.ThrowIfNull(netshOutput);

        int networks = 0;
        var channels = new Dictionary<int, int>();

        foreach (string raw in netshOutput.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("SSID ", StringComparison.Ordinal) && line.Contains(':', StringComparison.Ordinal))
            {
                networks++;
                continue;
            }

            Match match = ChannelLine().Match(line);
            if (match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
            {
                channels[channel] = channels.GetValueOrDefault(channel) + 1;
            }
        }

        return new WifiScanResult(networks, channels);
    }
}

/// <summary>
/// Оценщики пунктов проверки сети: измеренные числа превращаются в статус, приоритет и действие.
/// Все функции чистые и синхронные — их гоняют и тесты, и самопроверка на живой сборке.
/// Общее правило: «не измерено» (null, пустой список, сбой чтения) — это «проверить нельзя»
/// с причиной, а не ноль и не «всё хорошо».
/// </summary>
public static class NetworkCheckEvaluators
{
    /// <summary>Имя, под которым бенчмарк меряет сейчас настроенный DNS — чтобы сравнивать честно.</summary>
    public const string CurrentDnsName = "Сейчас настроенный";

    /// <summary>Коды твиков каталога, которые проверка вправе рекомендовать (и только по замерам).</summary>
    public const string MtuTweakCode = "MTU_SUBINTERFACE_OPTIMAL";

    public const string DnsTweakCode = "DNS_SERVERS_FASTEST";

    private const string MtuTitleRu = "MTU и фрагментация";
    private const string DnsTitleRu = "DNS";
    private const string DoubleNatTitleRu = "Двойной NAT";
    private const string BufferbloatTitleRu = "Bufferbloat (задержка под нагрузкой)";
    private const string WifiTitleRu = "Шум Wi-Fi и каналы";
    private const string BackgroundTitleRu = "Фоновые загрузки";
    private const string RightsTitleRu = "Права и служба";

    /// <summary>Порог «эфир зашумлён»: столько соседних сетей вокруг уже держит Wi-Fi в очередях.</summary>
    private const int WifiBusyNetworkCount = 10;

    /// <summary>Столько точек на одном канале — канал делят, задержки плавают.</summary>
    private const int WifiBusyChannelBssids = 5;

    /// <summary>Заметная фоновая передача: от полутора мегабит в секунду приёма в коротком замере.</summary>
    private const double BackgroundBusyMbps = 1.5;

    /// <summary>Выигрыш DNS, ради которого стоит менять настройки: меньше — погрешность замера.</summary>
    private const double DnsWorthChangingMs = 10.0;

    // ── MTU ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MTU: сравнение измеренного максимума пути с тем, что настроено на адаптере.
    /// <paramref name="result"/> null означает «замер не запускался» (нет базового значения).
    /// </summary>
    public static NetworkCheckItemResult EvaluateMtu(PathMtuDiscoveryResult? result, int? currentMtuBytes, string targetRu)
    {
        if (currentMtuBytes is not { } current || result is null)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Mtu, MtuTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                "Текущий MTU активного адаптера прочитать не удалось — сравнивать замер не с чем.",
                "MTU не трогаем: вслепую его менять нельзя.");
        }

        if (result.ApplicableMtuBytes is not { } optimal)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Mtu, MtuTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                result.ExplanationRu,
                "MTU не меняем: значение наугад не ставится.");
        }

        if (optimal >= current)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Mtu, MtuTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                $"Путь до {targetRu} пропускает пакеты {current} байт без фрагментации — столько и настроено на адаптере.",
                "Ничего делать не нужно.");
        }

        return new NetworkCheckItemResult(NetworkCheckItemKind.Mtu, MtuTitleRu, NetworkCheckStatus.Attention,
            NetworkCheckPriority.Medium,
            $"Адаптер настроен на {current} байт, а путь до {targetRu} без фрагментации пропускает только {optimal} байт: " +
            $"пакеты крупнее {optimal} байт будут дробиться или теряться — это выглядит как скачущий пинг и рывки.",
            "Примените твик «Оптимальный MTU интерфейса» (кнопкой «Применить рекомендованное» ниже или в разделе «Твики») — " +
            "значение при применении пересчитается замерами.",
            MtuTweakCode);
    }

    // ── DNS ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// DNS: победитель бенчмарка и время каждого — всё из замеров. Рекомендация появляется,
    /// только если сейчас настроенный резолвер измеренно медленнее лучшего публичного на
    /// <see cref="DnsWorthChangingMs"/>+ мс. Резолверы с признаком перехвата ответов исключаются.
    /// </summary>
    public static NetworkCheckItemResult EvaluateDns(IReadOnlyList<DnsResolverBenchmarkResult> results)
    {
        string times = DescribeDnsTimes(results);
        List<DnsResolverBenchmarkResult> reachable = results.Where(r => r.IsReachable && r.QueriesAnswered > 0).ToList();
        List<DnsResolverBenchmarkResult> clean = reachable.Where(r => !r.AnswersLookHijacked).ToList();

        if (reachable.Count == 0)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Dns, DnsTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                $"Ни один из проверенных DNS-серверов не ответил — сравнить и посоветовать нечего. {times}".Trim(),
                "DNS не трогаем: менять работающую настройку вслепую нельзя.");
        }

        if (clean.Count == 0)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Dns, DnsTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                $"Все ответившие серверы имеют признак перехвата ответов (отвечают на несуществующие имена). {times}",
                "Оставляем текущие настройки: предлагать перехватчик было бы вредно.");
        }

        DnsResolverBenchmarkResult winner = clean.OrderBy(r => r.CompositeScore).First();
        DnsResolverBenchmarkResult? current = clean.FirstOrDefault(r =>
            string.Equals(r.ResolverName, CurrentDnsName, StringComparison.Ordinal));

        if (current is null)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Dns, DnsTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                $"Текущий DNS в замер не попал (не настроен явно, не ответил или перехватывает) — сравнить не с чем. " +
                $"Самый быстрый из измеренных публичных: {winner.ResolverName} ({winner.PrimaryIp}), прогрев {Ms(winner.WarmCacheAvgMs)}. {times}",
                "Рекомендации нет: нельзя сказать «ваш медленнее», не измерив ваш.");
        }

        if (ReferenceEquals(winner, current) || string.Equals(winner.PrimaryIp, current.PrimaryIp, StringComparison.Ordinal))
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Dns, DnsTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                $"Сейчас настроен самый быстрый из измеренных ({current.PrimaryIp}, прогрев {Ms(current.WarmCacheAvgMs)}). {times}",
                "Ничего делать не нужно.");
        }

        double? gain = current.WarmCacheAvgMs is { } cur && winner.WarmCacheAvgMs is { } win ? cur - win : null;
        if (gain is { } g && g < DnsWorthChangingMs)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Dns, DnsTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                $"Текущий DNS отвечает за {Ms(current.WarmCacheAvgMs)}, лучший публичный ({winner.ResolverName}) — за {Ms(winner.WarmCacheAvgMs)}: " +
                $"разница {Ms(g)} в пределах погрешности. {times}",
                "Менять незачем.");
        }

        return new NetworkCheckItemResult(NetworkCheckItemKind.Dns, DnsTitleRu, NetworkCheckStatus.Attention,
            NetworkCheckPriority.Low,
            $"Сейчас настроенный DNS отвечает за {Ms(current.WarmCacheAvgMs)}, а {winner.ResolverName} ({winner.PrimaryIp}) — за {Ms(winner.WarmCacheAvgMs)}. {times} " +
            "Честно: DNS ускоряет поиск матча и вход в лобби, а пинг внутри боя от него не зависит.",
            "По желанию примените твик «Самые быстрые DNS-серверы».",
            DnsTweakCode);
    }

    private static string DescribeDnsTimes(IReadOnlyList<DnsResolverBenchmarkResult> results)
    {
        if (results.Count == 0)
        {
            return string.Empty;
        }

        var lines = results.Select(r => r.IsReachable && r.QueriesAnswered > 0
            ? $"{r.ResolverName} ({r.PrimaryIp}): прогрев {Ms(r.WarmCacheAvgMs)}, холодный {Ms(r.ColdCacheAvgMs)}"
            : $"{r.ResolverName} ({r.PrimaryIp}): не ответил");

        return "Замеры по серверам — " + string.Join("; ", lines) + ".";
    }

    // ── Двойной NAT ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Двойной NAT по собственным адресам, шлюзу и первым хопам пути к публичному узлу.
    /// Доказательство двойного NAT — два первых ответивших узла из частных диапазонов подряд;
    /// одного частного адреса компьютера для обвинения мало (так устроен любой домашний роутер).
    /// </summary>
    public static NetworkCheckItemResult EvaluateDoubleNat(
        IReadOnlyList<string> ownIpv4Addresses, string? gatewayAddress, IReadOnlyList<RouteHopMetric> firstHops)
    {
        List<IPAddress> own = ownIpv4Addresses
            .Select(text => IPAddress.TryParse(text, out IPAddress? parsed) ? parsed : null)
            .OfType<IPAddress>()
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        if (own.Count == 0)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.DoubleNat, DoubleNatTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                "Адрес компьютера в локальной сети определить не удалось.",
                "Выводов не делаем: без адреса не видно даже, есть ли NAT.");
        }

        IPAddress? publicOwn = own.FirstOrDefault(IsPublicGlobal);
        if (publicOwn is not null)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.DoubleNat, DoubleNatTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                $"Компьютер получает публичный адрес {publicOwn} напрямую — NAT между компьютером и интернетом на вашей стороне нет.",
                "Ничего делать не нужно.");
        }

        IPAddress? cgnatOwn = own.FirstOrDefault(IsCgnat);
        if (cgnatOwn is not null)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.DoubleNat, DoubleNatTitleRu, NetworkCheckStatus.Attention,
                NetworkCheckPriority.Medium,
                $"Адрес компьютера {cgnatOwn} — из диапазона 100.64.0.0/10: это NAT самого провайдера (CGNAT). " +
                "Порты снаружи не открываются, тип NAT в играх будет «строгий».",
                "Позвоните провайдеру и попросите публичный («белый») IP-адрес — настройками Windows это не лечится.");
        }

        List<string> answered = firstHops
            .Where(h => !h.IsTimedOut && IPAddress.TryParse(h.IpAddress, out _))
            .Select(h => h.IpAddress)
            .ToList();

        if (answered.Count >= 2
            && IPAddress.TryParse(answered[0], out IPAddress? hop1) && IsPrivateRfc1918(hop1)
            && IPAddress.TryParse(answered[1], out IPAddress? hop2) && IsPrivateRfc1918(hop2))
        {
            string gatewayNote = gatewayAddress is { Length: > 0 } g ? $" Шлюз компьютера: {g}." : string.Empty;
            return new NetworkCheckItemResult(NetworkCheckItemKind.DoubleNat, DoubleNatTitleRu, NetworkCheckStatus.Attention,
                NetworkCheckPriority.Medium,
                $"Первые два узла пути — оба из частных диапазонов ({answered[0]}, затем {answered[1]}).{gatewayNote} " +
                "Похоже, между компьютером и интернетом стоят два роутера подряд (двойной NAT): тип NAT в играх строгий, голос и P2P страдают.",
                "Переведите верхний роутер (от провайдера) в режим моста или подключите кабель в обход одного из роутеров. " +
                "Программа в роутер не заходит — это ручная настройка.");
        }

        if (answered.Count >= 2
            && IPAddress.TryParse(answered[0], out IPAddress? first) && IsPrivateRfc1918(first)
            && IPAddress.TryParse(answered[1], out IPAddress? second) && IsPublicGlobal(second))
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.DoubleNat, DoubleNatTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                $"Один домашний роутер ({answered[0]}), дальше путь сразу уходит в публичную сеть ({answered[1]}). Двойного NAT не видно.",
                "Ничего делать не нужно.");
        }

        string ownText = string.Join(", ", own.Select(ip => ip.ToString()));
        return new NetworkCheckItemResult(NetworkCheckItemKind.DoubleNat, DoubleNatTitleRu, NetworkCheckStatus.CannotCheck,
            NetworkCheckPriority.Info,
            $"Адрес(а) компьютера частные ({ownText}), но узлы пути на пробы не ответили — отличить один роутер от двух нельзя.",
            "Если игра пишет «строгий NAT», проверьте вручную, не стоит ли второй роутер перед вашим.");
    }

    private static bool IsPrivateRfc1918(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168));
    }

    private static bool IsCgnat(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    private static bool IsPublicGlobal(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b.Length != 4 || IsPrivateRfc1918(ip) || IsCgnat(ip))
        {
            return false;
        }

        // Не публичны: 0/8, петля 127/8, link-local 169.254/16, документация, мультикаст 224/4.
        return b[0] is not (0 or 127)
            && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 192 && b[1] == 0 && b[2] == 2)
            && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113)
            && b[0] < 224;
    }

    // ── Bufferbloat ──────────────────────────────────────────────────────────────────────────

    /// <summary>Рост задержки под нагрузкой: медианы и объём — как измерил тестер, без округлений «в пользу».</summary>
    public static NetworkCheckItemResult EvaluateBufferbloat(BufferbloatResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Outcome != BufferbloatOutcome.Measured || result.GrowthMs is not { } growth)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Bufferbloat, BufferbloatTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                result.NoteRu,
                "Выводов о bufferbloat не делаем: замер не состоялся.");
        }

        double megabytes = result.BytesDownloaded / 1024.0 / 1024.0;
        double mbps = result.SecondsLoaded > 0 ? result.BytesDownloaded * 8.0 / result.SecondsLoaded / 1_000_000.0 : 0;
        string loadText = $"За {result.SecondsLoaded.ToString("0.#", CultureInfo.CurrentCulture)} с нагрузки скачано " +
                          $"{megabytes.ToString("0.#", CultureInfo.CurrentCulture)} МБ (≈{mbps.ToString("0.#", CultureInfo.CurrentCulture)} Мбит/с).";
        string numbers = $"Задержка: в простое {Ms(result.IdleMedianMs)}, под нагрузкой {Ms(result.LoadedMedianMs)} — рост {Ms(growth)}. {loadText}";

        if (growth < 25)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Bufferbloat, BufferbloatTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                numbers + " Очередь на канале почти не копится: чужие загрузки пинг почти не испортят.",
                "Ничего делать не нужно.");
        }

        NetworkCheckPriority priority = growth > 100 ? NetworkCheckPriority.High : NetworkCheckPriority.Medium;
        string verdict = growth > 100
            ? "Это классический bufferbloat: пока канал занят (загрузка, синхронизация, чужой стрим), игра будет ощутимо лагать."
            : "Заметный рост: при занятом канале задержка в игре будет выше обычной.";

        return new NetworkCheckItemResult(NetworkCheckItemKind.Bufferbloat, BufferbloatTitleRu, NetworkCheckStatus.Attention,
            priority,
            numbers + " " + verdict,
            "Лечится на роутере: включите SQM/CAKE/fq_codel («умная очередь»), если прошивка умеет. " +
            "Настройками Windows очередь в роутере не убирается, программа в роутер не заходит.");
    }

    // ── Wi-Fi ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Эфир Wi-Fi: не применимо на проводе, шумно при толпе соседей, числа — из netsh.</summary>
    public static NetworkCheckItemResult EvaluateWifi(bool activeIsWireless, WifiScanResult? scan, string? failureRu)
    {
        if (!activeIsWireless)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Wifi, WifiTitleRu, NetworkCheckStatus.NotApplicable,
                NetworkCheckPriority.Info,
                "Активное подключение не по Wi-Fi (или беспроводного адаптера нет) — проверка эфира не применима. Для игр провод надёжнее в любом случае.",
                "Ничего делать не нужно.");
        }

        if (scan is null)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Wifi, WifiTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                failureRu ?? "Список сетей прочитать не удалось.",
                "Выводов об эфире не делаем.");
        }

        if (scan.NetworkCount == 0)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Wifi, WifiTitleRu, NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                "netsh не показал ни одной сети при беспроводном подключении — странно; считать это «тихим эфиром» нельзя.",
                "Проверьте, что Wi-Fi включён, и повторите проверку.");
        }

        string channels = scan.BssidsPerChannel.Count == 0
            ? "Каналы точек в выводе не нашлись."
            : $"Точки по каналам: {string.Join(", ", scan.BssidsPerChannel.OrderBy(kv => kv.Key).Select(kv => $"канал {kv.Key} — {kv.Value}"))}.";

        if (scan.NetworkCount >= WifiBusyNetworkCount || scan.BusiestChannelBssids >= WifiBusyChannelBssids)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.Wifi, WifiTitleRu, NetworkCheckStatus.Attention,
                NetworkCheckPriority.Medium,
                $"Вокруг видно сетей: {scan.NetworkCount}. {channels} В таком эфире Wi-Fi делит время с соседями, и задержка плавает.",
                "Для игр надёжнее кабель или диапазон 5 ГГц; канал на роутере можно сменить вручную на самый свободный из списка выше.");
        }

        return new NetworkCheckItemResult(NetworkCheckItemKind.Wifi, WifiTitleRu, NetworkCheckStatus.Ok,
            NetworkCheckPriority.Info,
            $"Вокруг видно сетей: {scan.NetworkCount}. {channels} Перегруженных каналов не видно.",
            "Ничего делать не нужно; кабель при случае всё равно стабильнее.");
    }

    // ── Фоновые загрузки ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Фоновые загрузки: Delivery Optimization по имени (если прочитался) и измеренная скорость
    /// адаптера за короткий интервал. Кто именно качает, программа не определяет и не выдумывает.
    /// </summary>
    public static NetworkCheckItemResult EvaluateBackground(
        bool? deliveryOptimizationDownloading, int deliveryOptimizationFiles, double? downloadMbps, double? uploadMbps)
    {
        if (deliveryOptimizationDownloading == true)
        {
            string files = deliveryOptimizationFiles > 0 ? $" (файлов в работе: {deliveryOptimizationFiles})" : string.Empty;
            return new NetworkCheckItemResult(NetworkCheckItemKind.BackgroundDownloads, BackgroundTitleRu, NetworkCheckStatus.Attention,
                NetworkCheckPriority.Medium,
                $"Delivery Optimization прямо сейчас скачивает обновления{files} — канал делится с обновлениями Windows.",
                "Дайте загрузке закончиться до матча или поставьте обновления на паузу в «Параметрах». " +
                "Программа обновления не отключает никогда — это сознательный запрет проекта.");
        }

        if (downloadMbps is { } down && down >= BackgroundBusyMbps)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.BackgroundDownloads, BackgroundTitleRu, NetworkCheckStatus.Attention,
                NetworkCheckPriority.Medium,
                $"За короткий замер адаптер принимал ≈{down.ToString("0.#", CultureInfo.CurrentCulture)} Мбит/с" +
                (uploadMbps is { } up ? $", отдавал ≈{up.ToString("0.#", CultureInfo.CurrentCulture)} Мбит/с" : string.Empty) +
                " — что-то заметно качает. Кто именно, программа не определяет.",
                "Посмотрите в «Диспетчере задач» (столбец «Сеть»), какая программа создаёт нагрузку, и закройте её на время игры.");
        }

        if (downloadMbps is { } quietDown || deliveryOptimizationDownloading == false)
        {
            string measured = downloadMbps is { } d
                ? $" (приём ≈{d.ToString("0.#", CultureInfo.CurrentCulture)} Мбит/с за время замера)"
                : string.Empty;
            return new NetworkCheckItemResult(NetworkCheckItemKind.BackgroundDownloads, BackgroundTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                $"Прямо сейчас заметных фоновых загрузок не видно{measured}. Это снимок момента, а не гарантия на весь вечер.",
                "Ничего делать не нужно.");
        }

        return new NetworkCheckItemResult(NetworkCheckItemKind.BackgroundDownloads, BackgroundTitleRu, NetworkCheckStatus.CannotCheck,
            NetworkCheckPriority.Info,
            "Ни состояние Delivery Optimization, ни скорость адаптера прочитать не удалось.",
            "Выводов не делаем.");
    }

    // ── Права и служба ───────────────────────────────────────────────────────────────────────

    /// <summary>Узел диагностики: отсутствие прав — не ошибка, а молчащая служба — повод поправить.</summary>
    public static NetworkCheckItemResult EvaluateRightsAndService(bool hasAdministrator, bool serviceResponds, string? serviceNoteRu)
    {
        if (!serviceResponds)
        {
            return new NetworkCheckItemResult(NetworkCheckItemKind.RightsAndService, RightsTitleRu, NetworkCheckStatus.Attention,
                NetworkCheckPriority.Medium,
                $"Служба не отвечает{(string.IsNullOrWhiteSpace(serviceNoteRu) ? string.Empty : $": {serviceNoteRu}")}. " +
                "Без неё применять твики и живую оптимизацию нельзя — только смотреть и измерять.",
                "Переустановите программу установщиком или запустите install-service.ps1 от имени администратора из папки программы.");
        }

        return hasAdministrator
            ? new NetworkCheckItemResult(NetworkCheckItemKind.RightsAndService, RightsTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                "Служба отвечает, у окна права администратора есть — рекомендации можно будет применить обычным путём.",
                "Ничего делать не нужно.")
            : new NetworkCheckItemResult(NetworkCheckItemKind.RightsAndService, RightsTitleRu, NetworkCheckStatus.Ok,
                NetworkCheckPriority.Info,
                "Служба отвечает. Прав администратора у окна нет — это не ошибка: замеры и проверка работают полностью, " +
                "а вот применение твиков служба честно отклонит.",
                "Если решите применять рекомендации — запустите программу от имени администратора.");
    }

    private static string Ms(double? value) => value is { } v
        ? v.ToString("0.#", CultureInfo.CurrentCulture) + " мс"
        : "не измерено";
}

/// <summary>Строка пункта проверки в списке: результат оценщика плюс, у bufferbloat, кнопка согласия.</summary>
public sealed class NetworkCheckItemViewModel
{
    private NetworkCheckItemViewModel(NetworkCheckItemResult result, AsyncRelayCommand? consentCommand)
    {
        Result = result;
        ConsentCommand = consentCommand;
    }

    public NetworkCheckItemViewModel(NetworkCheckItemResult result)
        : this(result, null)
    {
    }

    public NetworkCheckItemResult Result { get; }

    public string TitleRu => Result.TitleRu;

    public string StatusRu => Result.StatusRu;

    public string DetailRu => Result.DetailRu;

    public string ActionRu => Result.ActionRu;

    /// <summary>Связь с каталогом — как в проверке настроек: код или честное «действие вручную».</summary>
    public string LinkRu => Result.TweakCode is { Length: > 0 } code
        ? $"Связанный твик каталога: {code}"
        : "Твика в каталоге нет — действие вручную";

    /// <summary>Нужна ли кнопка согласия: только у bufferbloat до его запуска.</summary>
    public bool NeedsConsent => ConsentCommand is not null;

    /// <summary>Кнопка «Понятно»: явное согласие на 10 секунд нагрузки. Без неё замер не начнётся.</summary>
    public AsyncRelayCommand? ConsentCommand { get; }

    internal static NetworkCheckItemViewModel BufferbloatPlaceholder(AsyncRelayCommand consentCommand) => new(
        new NetworkCheckItemResult(
            NetworkCheckItemKind.Bufferbloat,
            "Bufferbloat (задержка под нагрузкой)",
            NetworkCheckStatus.NotChecked,
            NetworkCheckPriority.Info,
            "Для этой проверки на 10 секунд займём канал, чтобы увидеть рост задержки под нагрузкой. " +
            "В это время интернетом лучше не пользоваться, и уйдёт заметный объём трафика. " +
            "Без вашего нажатия «Понятно» замер не начнётся.",
            "Нажмите «Понятно», когда будете готовы, — или оставьте пункт непроверенным."),
        consentCommand);

    internal static NetworkCheckItemViewModel BufferbloatRunning() => new(
        new NetworkCheckItemResult(
            NetworkCheckItemKind.Bufferbloat,
            "Bufferbloat (задержка под нагрузкой)",
            NetworkCheckStatus.NotChecked,
            NetworkCheckPriority.Info,
            "Замер идёт: сначала задержка в простое, затем 10 секунд нагрузки…",
            string.Empty));
}

/// <summary>
/// «Проверить сеть» в разделе «Оптимизация» (задача G): семь проверок одной кнопкой, ни одна
/// ничего не меняет. Bufferbloat запускается отдельно и только после предупреждения и нажатия
/// «Понятно» — нагружать канал молча нельзя.
///
/// Итог — не «оценка 87/100», а список находок с приоритетами и действиями; числа в нём —
/// только измеренные. «Применить рекомендованное» существует лишь когда пункты сослались
    /// на твики, которые реально есть в каталоге; применяет обычный путь раздела «Твики»
/// (подтверждение, снапшот, проверка, откат) — здесь только передаются коды.
/// </summary>
public sealed class NetworkCheckViewModel : ObservableObject
{
    private const string IdleRu =
        "Проверка ещё не запускалась: нажмите «Проверить сеть» — программа прочитает состояние и измерит, ничего не меняя.";

    private static readonly NetworkCheckItemKind[] DisplayOrder =
    [
        NetworkCheckItemKind.Mtu,
        NetworkCheckItemKind.Dns,
        NetworkCheckItemKind.DoubleNat,
        NetworkCheckItemKind.Bufferbloat,
        NetworkCheckItemKind.Wifi,
        NetworkCheckItemKind.BackgroundDownloads,
        NetworkCheckItemKind.RightsAndService,
    ];

    private readonly INetworkSelfCheck _runner;
    private readonly Action<IReadOnlyList<string>> _applyRecommended;
    private readonly HashSet<string> _catalogCodes;

    private IReadOnlyList<NetworkCheckItemViewModel> _items = [];
    private IReadOnlyList<string> _findingsRu = [];
    private IReadOnlyList<string> _recommendedCodes = [];
    private string _statusRu = IdleRu;
    private string _summaryRu = string.Empty;
    private string _recommendationLineRu = string.Empty;
    private bool _hasResult;
    private bool _isBusy;

    public NetworkCheckViewModel(
        INetworkSelfCheck runner,
        Action<IReadOnlyList<string>> applyRecommended,
        IReadOnlyCollection<string> catalogCodes)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _applyRecommended = applyRecommended ?? throw new ArgumentNullException(nameof(applyRecommended));
        _catalogCodes = new HashSet<string>(
            catalogCodes ?? throw new ArgumentNullException(nameof(catalogCodes)),
            StringComparer.OrdinalIgnoreCase);

        CheckCommand = new AsyncRelayCommand(RunCheckAsync);
        ApplyRecommendedCommand = new RelayCommand(ApplyRecommended, () => HasRecommendations);
    }

    /// <summary>Сборка без подключённой проверки: честное «не подключена», а не пустая кнопка.</summary>
    internal static NetworkCheckViewModel Unavailable() => new(
        new UnavailableNetworkSelfCheck(),
        _ => { },
        []);

    /// <summary>Кнопка «Проверить сеть»: шесть проверок чтения и замеров (bufferbloat — отдельно).</summary>
    public AsyncRelayCommand CheckCommand { get; }

    /// <summary>Кнопка «Применить рекомендованное»: видна и активна только при живых рекомендациях каталога.</summary>
    public RelayCommand ApplyRecommendedCommand { get; }

    public IReadOnlyList<NetworkCheckItemViewModel> Items
    {
        get => _items;
        private set => Set(ref _items, value);
    }

    /// <summary>Строки итога: находки по приоритету, затем «проверено успешно» и «не проверено».</summary>
    public IReadOnlyList<string> FindingsRu
    {
        get => _findingsRu;
        private set => Set(ref _findingsRu, value);
    }

    /// <summary>Коды, которые «Применить рекомендованное» передаст обычному применению. Пусто — кнопки нет.</summary>
    public IReadOnlyList<string> RecommendedCodes
    {
        get => _recommendedCodes;
        private set
        {
            if (Set(ref _recommendedCodes, value))
            {
                OnPropertyChanged(nameof(HasRecommendations));
                ApplyRecommendedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasRecommendations => RecommendedCodes.Count > 0;

    public bool HasResult
    {
        get => _hasResult;
        private set => Set(ref _hasResult, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string StatusRu
    {
        get => _statusRu;
        private set => Set(ref _statusRu, value);
    }

    /// <summary>Одна строка счётчиков по статусам — числа считаются из пунктов, а не пишутся руками.</summary>
    public string SummaryRu
    {
        get => _summaryRu;
        private set => Set(ref _summaryRu, value);
    }

    /// <summary>Что предлагается применить — или честное «ничего не предлагаем».</summary>
    public string RecommendationLineRu
    {
        get => _recommendationLineRu;
        private set => Set(ref _recommendationLineRu, value);
    }

    /// <summary>Основной прогон: чтение и замеры. Bufferbloat после него остаётся ждать согласия.</summary>
    public async Task RunCheckAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusRu = "Проверяем… Читаем состояние и меряем: MTU, DNS, первые хопы, эфир и фон. Ничего не меняется.";

        try
        {
            NetworkSelfCheckReport report = await _runner.RunAsync().ConfigureAwait(true);

            var items = new List<NetworkCheckItemViewModel>();
            foreach (NetworkCheckItemKind kind in DisplayOrder)
            {
                if (kind == NetworkCheckItemKind.Bufferbloat)
                {
                    items.Add(NetworkCheckItemViewModel.BufferbloatPlaceholder(
                        new AsyncRelayCommand(RunBufferbloatByConsentAsync)));
                    continue;
                }

                NetworkCheckItemResult? result = report.Items.FirstOrDefault(i => i.Kind == kind);
                items.Add(new NetworkCheckItemViewModel(result ?? MissingItem(kind)));
            }

            Items = items;
            HasResult = true;
            Recompute();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Весь прогон упал — говорим честно, а не оставляем вчерашний результат за свежий.
            Items = [];
            HasResult = false;
            RecommendedCodes = [];
            FindingsRu = [];
            SummaryRu = string.Empty;
            RecommendationLineRu = string.Empty;
            StatusRu = $"Проверка не выполнилась: {ex.Message}. Ничего не изменилось — проверка вообще ничего не меняет.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Согласие получено — только теперь канал нагружается. До этого вызова замер bufferbloat
    /// не существует: пункт честно стоял со статусом «Не проверено».
    /// </summary>
    private async Task RunBufferbloatByConsentAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ReplaceBufferbloat(NetworkCheckItemViewModel.BufferbloatRunning());

        try
        {
            NetworkCheckItemResult result = await _runner.RunBufferbloatAsync().ConfigureAwait(true);
            ReplaceBufferbloat(new NetworkCheckItemViewModel(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReplaceBufferbloat(new NetworkCheckItemViewModel(new NetworkCheckItemResult(
                NetworkCheckItemKind.Bufferbloat,
                "Bufferbloat (задержка под нагрузкой)",
                NetworkCheckStatus.CannotCheck,
                NetworkCheckPriority.Info,
                $"Замер не выполнился: {ex.Message}",
                "Выводов о bufferbloat не делаем.")));
        }
        finally
        {
            IsBusy = false;
            Recompute();
        }
    }

    private void ReplaceBufferbloat(NetworkCheckItemViewModel replacement)
    {
        Items = Items
            .Select(item => item.Result.Kind == NetworkCheckItemKind.Bufferbloat ? replacement : item)
            .ToList();
    }

    private void ApplyRecommended()
    {
        if (!HasRecommendations)
        {
            return;
        }

        // Действие целиком — обычный путь раздела «Твики»: подтверждение, снапшот, точка
        // восстановления, проверка и откат живут там и не дублируются здесь.
        _applyRecommended(RecommendedCodes);
    }

    private void Recompute()
    {
        IReadOnlyList<NetworkCheckItemResult> results = Items.Select(i => i.Result).ToList();

        int ok = results.Count(r => r.Status == NetworkCheckStatus.Ok);
        int attention = results.Count(r => r.Status == NetworkCheckStatus.Attention);
        int cannot = results.Count(r => r.Status == NetworkCheckStatus.CannotCheck);
        int notApplicable = results.Count(r => r.Status == NetworkCheckStatus.NotApplicable);
        int notChecked = results.Count(r => r.Status == NetworkCheckStatus.NotChecked);

        SummaryRu = $"Итог из {results.Count} пунктов: в порядке — {ok}, стоит поправить — {attention}, " +
                    $"проверить нельзя — {cannot}, не применимо — {notApplicable}, не проверено — {notChecked}.";
        StatusRu = "Проверка выполнена. Ничего не изменилось: применений здесь нет, только чтение и замеры.";

        var findings = new List<string>();
        foreach (NetworkCheckItemResult item in results
                     .Where(r => r.Status == NetworkCheckStatus.Attention)
                     .OrderByDescending(r => r.Priority))
        {
            findings.Add($"{item.PriorityRu} приоритет — {item.TitleRu}. {item.ActionRu}");
        }

        List<string> okTitles = results.Where(r => r.Status == NetworkCheckStatus.Ok).Select(r => r.TitleRu).ToList();
        if (okTitles.Count > 0)
        {
            findings.Add("Проверено и в порядке: " + string.Join(", ", okTitles) + ".");
        }

        List<string> unknownTitles = results
            .Where(r => r.Status is NetworkCheckStatus.CannotCheck or NetworkCheckStatus.NotChecked or NetworkCheckStatus.NotApplicable)
            .Select(r => $"{r.TitleRu} ({r.StatusRu.ToLowerInvariant()})")
            .ToList();
        if (unknownTitles.Count > 0)
        {
            findings.Add("Без вывода: " + string.Join(", ", unknownTitles) + ".");
        }

        FindingsRu = findings;

        // Рекомендации — только коды, которые реально есть в каталоге этой сборки. Код-призрак
        // (убрали из каталога, опечатка в раннере) в применение не попадёт никогда.
        RecommendedCodes = results
            .Select(r => r.TweakCode)
            .OfType<string>()
            .Where(code => _catalogCodes.Contains(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        RecommendationLineRu = RecommendedCodes.Count > 0
            ? $"По этой проверке из каталога можно применить: {string.Join(", ", RecommendedCodes)}. " +
              "Кнопка ниже отметит их в разделе «Твики» и запустит обычное применение — с подтверждением и снапшотом."
            : "Применять по этой проверке нечего — ничего не предлагаем.";
    }

    private static NetworkCheckItemResult MissingItem(NetworkCheckItemKind kind) => new(
        kind,
        kind switch
        {
            NetworkCheckItemKind.Mtu => "MTU и фрагментация",
            NetworkCheckItemKind.Dns => "DNS",
            NetworkCheckItemKind.DoubleNat => "Двойной NAT",
            NetworkCheckItemKind.Wifi => "Шум Wi-Fi и каналы",
            NetworkCheckItemKind.BackgroundDownloads => "Фоновые загрузки",
            NetworkCheckItemKind.RightsAndService => "Права и служба",
            _ => "Проверка",
        },
        NetworkCheckStatus.CannotCheck,
        NetworkCheckPriority.Info,
        "Этот пункт в результат проверки не попал — считать его пройденным нельзя.",
        "Повторите проверку; если повторится — это ошибка сборки проверки, а не вашей сети.");
}
