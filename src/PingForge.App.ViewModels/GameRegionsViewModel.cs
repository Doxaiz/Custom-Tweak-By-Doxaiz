using PingForge.Routing;

namespace PingForge.App.ViewModels;

/// <summary>
/// Одна игра справочника глазами окна: регионы и опубликованные порты — ровно те, что есть в
/// <see cref="GameRouteCatalog"/>. Ни одного адреса дата-центра здесь нет и быть не может: игры их
/// не публикуют, а конкретный сервер выбирает матчмейкер.
/// </summary>
public sealed record GameRegionCardViewModel(
    string GameRu,
    string ProcessName,
    IReadOnlyList<string> RegionsRu,
    IReadOnlyList<string> PublishedPortsRu,
    string NoteRu,
    string RegionsLineRu,
    string PortsLineRu);

/// <summary>Один бесплатный резолвер в списке «Твиков»: выбор входа, а не «ускоритель пинга».</summary>
public sealed record FreeDnsRowViewModel(string ProviderRu, string AddressesRu, string NoteRu);

/// <summary>
/// Карточка «DNS — это про вход, а не про пинг» для раздела «Твики».
///
/// Список резолверов не написан в разметке: он собран из <see cref="GameRouteCatalog.FreeDnsRoutes"/>.
/// Тот же список уходит в «Проверить сеть» (<see cref="GameRegionsViewModel.BenchmarkCandidates"/>) и в совет DNS, поэтому
/// разойтись этим трём местам негде.
/// </summary>
public sealed class DnsCardViewModel
{
    public DnsCardViewModel(IReadOnlyList<PublicDnsRoute>? routes = null)
    {
        Routes = routes ?? GameRouteCatalog.FreeDnsRoutes;
        Resolvers = Routes
            .Select(route => new FreeDnsRowViewModel(
                route.ProviderRu,
                string.IsNullOrWhiteSpace(route.SecondaryIp)
                    ? route.PrimaryIp
                    : $"{route.PrimaryIp} / {route.SecondaryIp}",
                route.NoteRu))
            .ToList();
    }

    /// <summary>Заголовок карточки. Он же проверяется тестом дословно.</summary>
    public string TitleRu { get; } = "DNS — это про вход, а не про пинг";

    /// <summary>
    /// Главное объяснение. «DNS снизил пинг» — это утверждение, которого не бывает: DNS ускоряет
    /// только поиск имени и вход, а пинг в бою определяется маршрутом до сервера.
    /// </summary>
    public string ExplanationRu { get; } =
        "DNS не снижает пинг в матче: он ускоряет поиск имени сервера и вход в игру. " +
        "Пинг в бою определяется маршрутом до сервера, и узнать его можно только замером живого потока.";

    /// <summary>Подпись к списку: что человек выбирает на самом деле.</summary>
    public string ListTitleRu { get; } = "Бесплатные публичные резолверы — выбор входа, не ускоритель пинга";

    /// <summary>Что означают числа в списке (их здесь нет нарочно).</summary>
    public string MeasuredRu { get; } =
        "Время ответа каждого резолвера измеряется в «Проверить сеть» на этой машине. " +
        "До замера здесь нет чисел: «примерно быстро» — это не измерение.";

    public IReadOnlyList<PublicDnsRoute> Routes { get; }

    public IReadOnlyList<FreeDnsRowViewModel> Resolvers { get; }
}

/// <summary>
/// Регионы и порты игр из справочника — то, чего раньше не было видно человеку.
///
/// Что здесь есть и чего нарочно нет:
///   • у каждой игры справочника — её регионы и её опубликованные порты, дословно из каталога;
///   • у игры вне справочника — честное «неизвестно», а не чужие регионы и не пустая строка;
///   • «пинга до региона» нет: игр с опубликованными адресами дата-центров не существует, поэтому
///     единственное честное число — замер уже выданного матчмейкером сервера;
///   • программа ничего не настраивает в игре и не подменяет регион: она объясняет, что выбрать.
///
/// Один и тот же экземпляр-справочник используют карточки «Игры» и «Твики»; данные неизменяемы.
/// </summary>
public sealed class GameRegionsViewModel
{
    /// <summary>Заголовок блока у карточки игры.</summary>
    public const string HeadingRu = "Где серверы этой игры";

    /// <summary>Честная приписка про адрес конкретного матча.</summary>
    public const string HonestNoteRu =
        "Адреса дата-центров игры не публикуют, поэтому «пинга до региона» здесь нет: " +
        "конкретный сервер выбирает матчмейкер, а программа измеряет уже выданный.";

    /// <summary>Ответ для игры, которой нет в справочнике.</summary>
    public const string UnknownRegionsRu =
        "Регионы этой игры неизвестны: её нет в справочнике маршрутов, и придумывать их программа не будет.";

    /// <summary>Ответ про порты, когда игра их не публикует.</summary>
    public const string UnpublishedPortsRu =
        "Порты этой игры официально не публикуются — программа их не выдумывает.";

    private readonly IReadOnlyList<GameRegionCardViewModel> _cards;

    public GameRegionsViewModel(IReadOnlyList<GameRouteTarget>? targets = null, IReadOnlyList<PublicDnsRoute>? routes = null)
    {
        IReadOnlyList<GameRouteTarget> source = targets ?? GameRouteCatalog.Targets;
        _cards = source.Select(CreateCard).ToList();
        Dns = new DnsCardViewModel(routes);
    }

    /// <summary>Карточка DNS того же справочника — для раздела «Твики».</summary>
    public DnsCardViewModel Dns { get; }

    /// <summary>Все игры справочника по порядку: ровно те, что в каталоге, без добавок.</summary>
    public IReadOnlyList<GameRegionCardViewModel> Cards => _cards;

    /// <summary>Заголовок блока в разметке (привязка не умеет читать const).</summary>
    public string HeadingTextRu => HeadingRu;

    /// <summary>Честная приписка про матчмейкер — та же, что у каждой карточки.</summary>
    public string HonestNoteTextRu => HonestNoteRu;

    /// <summary>
    /// Кандидаты для бенчмарка DNS. Единственный список на программу: «Проверить сеть» меряет
    /// именно эти резолверы, а не свой отдельный набор.
    /// </summary>
    public static IReadOnlyList<(string Name, string Primary, string Secondary)> BenchmarkCandidates { get; } =
        GameRouteCatalog.FreeDnsRoutes
            .Select(route => (route.ProviderRu, route.PrimaryIp, route.SecondaryIp))
            .ToList();

    /// <summary>Строка «Где серверы» для карточки игры (по имени процесса, без учёта регистра).</summary>
    public string RegionsLineRu(string? processName) =>
        Find(processName)?.RegionsLineRu
        ?? $"{UnknownRegionsRu} {HonestNoteRu}";

    /// <summary>Строка «Порты» для карточки игры; неизвестное — честный отказ, а не пустая строка.</summary>
    public string PortsLineRu(string? processName) =>
        Find(processName)?.PortsLineRu
        ?? UnpublishedPortsRu;

    /// <summary>Карточка игры справочника или null: null здесь означает «игра не из справочника».</summary>
    public GameRegionCardViewModel? Find(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        return _cards.FirstOrDefault(card =>
            string.Equals(card.ProcessName, processName!.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static GameRegionCardViewModel CreateCard(GameRouteTarget target)
    {
        string regions = target.RegionsRu.Count == 0
            ? UnknownRegionsRu
            : $"Где серверы: {string.Join("; ", target.RegionsRu)}. {HonestNoteRu}";

        string ports = target.PublishedPortsRu.Count == 0
            ? UnpublishedPortsRu
            : $"Порты (описаны игрой): {string.Join("; ", target.PublishedPortsRu)}.";

        return new GameRegionCardViewModel(
            target.GameRu,
            target.ProcessName,
            target.RegionsRu,
            target.PublishedPortsRu,
            target.NoteRu,
            regions,
            ports);
    }
}
