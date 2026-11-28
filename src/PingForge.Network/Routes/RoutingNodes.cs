using System.Globalization;
using System.Text.Json;

namespace PingForge.Network.Routes;

/// <summary>
/// Узел маршрутизации, который человек выбрал или добавил в «Настройках».
///
/// Принципиально: у программы НЕТ своей сети узлов. «Сервер маршрутизации» — это шлюз, который
/// уже есть у пользователя (текущий, второй канал, настроенный VPN-адаптер), либо адрес, который
/// человек ввёл сам. Поэтому у каждого узла честный источник, а список «серверов PingForge» здесь
/// физически не может появиться: узлы создаются только из таблицы маршрутов и ручного ввода.
/// </summary>
/// <param name="Address">Строгий IPv4 в нормализованном виде («10.8.0.1»).</param>
/// <param name="Name">Имя адаптера или подпись человека. Пустым не бывает: при пустой подписи подставляется адрес.</param>
/// <param name="SourceRu">Честный источник: «текущий шлюз», «второй шлюз», «VPN-адаптер …», «введён вручную».</param>
/// <param name="MedianRttMs">Медиана RTT через этот узел — только если он измерялся. Иначе null, а не ноль.</param>
/// <param name="JitterMs">Джиттер (MAD) из того же замера.</param>
/// <param name="LossPercent">Потери из того же замера.</param>
/// <param name="MeasuredUtc">Когда был замер.</param>
public sealed record RoutingNodeEntry(
    string Address,
    string Name,
    string SourceRu,
    double? MedianRttMs = null,
    double? JitterMs = null,
    double? LossPercent = null,
    DateTime? MeasuredUtc = null)
{
    /// <summary>Узел введён человеком, а не найден в системе — его удалять можно так же, как найденный.</summary>
    public bool IsManual => string.Equals(SourceRu, RoutingNodeSourceRu.Manual, StringComparison.Ordinal);

    public string MedianRu => MedianRttMs is { } median
        ? $"{median.ToString("0.#", CultureInfo.InvariantCulture)} мс"
        : "не измерялась";
}

/// <summary>Канонические подписи источника узла. Любая другая подпись — повод тесту упасть: источник обязан быть честным.</summary>
public static class RoutingNodeSourceRu
{
    public const string CurrentGateway = "текущий шлюз";
    public const string SecondGateway = "второй шлюз";
    public const string Manual = "введён вручную";

    public static string VpnAdapter(string adapterName) => $"VPN-адаптер «{adapterName}»";

    /// <summary>Все подписи, которые программа вообще умеет ставить. Тест сверяет список именно с ними.</summary>
    public static IReadOnlyList<string> KnownFixed { get; } = [CurrentGateway, SecondGateway, Manual];

    public static bool IsHonest(string? source) =>
        source is not null
        && (KnownFixed.Contains(source, StringComparer.Ordinal) || source.StartsWith("VPN-адаптер ", StringComparison.Ordinal));
}

/// <summary>
/// Настройки маршрутизации целиком: список узлов, узел по умолчанию и режим «выбирать лучший
/// автоматически». Лежат в общем settings.json рядом со своими играми и профилем потребления.
/// </summary>
public sealed record RoutingSettings(
    IReadOnlyList<RoutingNodeEntry> Nodes,
    string? DefaultNodeAddress,
    bool AutoSelectBest,
    bool MultipathEnabled = false,
    string? SecondaryNodeAddress = null)
{
    public static RoutingSettings Empty { get; } = new([], null, false);

    public RoutingNodeEntry? DefaultNode => Nodes.FirstOrDefault(node =>
        node.Address.Equals(DefaultNodeAddress, StringComparison.OrdinalIgnoreCase));

    public RoutingNodeEntry? SecondaryNode => SecondaryNodeAddress is null ? null : Nodes.FirstOrDefault(node =>
        node.Address.Equals(SecondaryNodeAddress, StringComparison.OrdinalIgnoreCase));

    /// <summary>Узлы со строгим IPv4 адресом: мусор в сохранённом файле не должен доезжать до службы.</summary>
    public IReadOnlyList<RoutingNodeEntry> ValidNodes =>
        Nodes.Where(node => RoutingNodeValidation.IsStrictIpv4(node.Address)).ToList();

    public bool IsMultipathValid => MultipathEnabled && SecondaryNode is not null && DefaultNode is not null &&
        !DefaultNodeAddress!.Equals(SecondaryNodeAddress, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Строгая проверка адреса узла. Мусор не сохраняется никогда:
///   • только IPv4 точечной записи: имена хостов, «localhost», IPv6 и «1.2» — отказ;
///   • ведущие нули («01.2.3.4») — отказ: это не адрес, а двусмысленность;
///   • 0.0.0.0 и 255.255.255.255 — не адреса узлов; multicast, loopback и link-local отклоняются:
///     маршрут на них вести бессмысленно;
///   • дубли (тот же адрес второй раз) — отказ;
///   • подпись необязательна: пустая — это адрес; переводы строк и управляющие символы не пройдут.
/// </summary>
public static class RoutingNodeValidation
{
    public const int MaxLabelChars = 80;

    /// <summary>Строгий IPv4 для любых проверок: используется и здесь, и при поиске шлюзов в таблице.</summary>
    public static bool IsStrictIpv4(string? value) => NormalizeIpv4(value, out _);

    /// <summary>Нормализовать адрес или честно отказать. Без исключений: false — и есть ответ.</summary>
    public static bool NormalizeIpv4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();

        // IPv6 и «::ffff:...» отсекаются сразу: точечная запись обязательна.
        if (candidate.Contains(':') || candidate.Any(char.IsWhiteSpace) || candidate.Contains('/'))
        {
            return false;
        }

        string[] parts = candidate.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            string part = parts[i];
            if (part.Length is < 1 or > 3 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            // «04» — двусмысленно (классика парсеров: восьмеричная запись). Строго: ноль или без ведущего нуля.
            if (part.Length > 1 && part[0] == '0')
            {
                return false;
            }

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int octet) || octet > 255)
            {
                return false;
            }

            octets[i] = (byte)octet;
        }

        // Отбрасываем адреса, которые узлом быть не могут.
        if (octets[0] == 0)
        {
            return false; // Any и «эта сеть».
        }

        if (octets[0] == 127)
        {
            return false; // loopback: маршрут «через себя» ничего не измеряет.
        }

        if (octets[0] == 169 && octets[1] == 254)
        {
            return false; // link-local APIPA: ходиться туда некуда.
        }

        if (octets[0] >= 224)
        {
            return false; // multicast (224–239) и резерв (240–255), включая 255.255.255.255.
        }

        normalized = $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}";
        return true;
    }

    /// <summary>
    /// Собрать узел из ручного ввода: адрес + необязательная подпись. Отказ всегда с причиной —
    /// человек должен понять, что именно не так, а не гадать по пустому полю.
    /// </summary>
    public static bool TryCreateManual(
        string? address,
        string? label,
        IEnumerable<RoutingNodeEntry> existing,
        out RoutingNodeEntry? node,
        out string errorRu)
    {
        node = null;
        errorRu = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
        {
            errorRu = "Адрес узла пустой. Введите IPv4-адрес, например 10.8.0.1.";
            return false;
        }

        if (!NormalizeIpv4(address, out string normalized))
        {
            errorRu = $"«{address.Trim()}» не подходит: нужен IPv4-адрес вида 10.8.0.1. " +
                      "Имена компьютеров, IPv6, адреса вида 0.0.0.0 или multicast программа не принимает — маршрут на них не ставится.";
            return false;
        }

        if (existing.Any(candidate => candidate.Address.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            errorRu = $"Узел {normalized} уже в списке: второй раз добавлять не нужно.";
            return false;
        }

        string labelRu = SanitizeLabel(label);
        node = new RoutingNodeEntry(normalized, labelRu.Length > 0 ? labelRu : normalized, RoutingNodeSourceRu.Manual);
        return true;
    }

    /// <summary>Подпись: одна строка, без управляющих символов, в разумном размере. Пустая — не ошибка.</summary>
    public static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(label.Length, MaxLabelChars)];
        int written = 0;
        foreach (char ch in label)
        {
            if (written >= buffer.Length)
            {
                break;
            }

            if (char.IsControl(ch))
            {
                if (written > 0 && buffer[written - 1] != ' ')
                {
                    buffer[written++] = ' ';
                }

                continue;
            }

            buffer[written++] = ch;
        }

        return new string(buffer[..written]).Trim();
    }
}

/// <summary>
/// Терпимое чтение узлов маршрутизации из текста общего settings.json — тем же правилам, что и свои
/// игры (задача A): нет файла, пустой файл, битый JSON, мусор вместо списка — служба НЕ падает,
/// берёт пустой список и объяснение для журнала. Битая запись внутри списка пропускается,
/// остальные остаются.
///
/// Файл принадлежит окну программы, поэтому этот класс только читает. Пишет настройки окно
/// (JsonFileSettingsStore), а узлы в JSON выглядят так:
/// «RoutingNodes»: [{ «Address»: «10.8.0.1», «Name»: «WireGuard», «SourceRu»: «VPN-адаптер …» }],
/// «RoutingDefaultNode»: «10.8.0.1», «RoutingAutoSelect»: true.
/// </summary>
public static class RoutingNodesJson
{
    public const string NodesKey = "RoutingNodes";
    public const string DefaultNodeKey = "RoutingDefaultNode";
    public const string AutoSelectKey = "RoutingAutoSelect";
    public const string MultipathKey = "RoutingMultipathEnabled";
    public const string SecondaryNodeKey = "RoutingSecondaryNode";

    /// <summary>
    /// Прочитать настройки маршрутизации из текста файла. tryRead=false — файл не прочитался
    /// (нет доступа и т.п.): это не «пустые настройки», и служба честно пишет причину в журнал.
    /// </summary>
    public static RoutingSettings Read(string json, out string? problemRu)
    {
        problemRu = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            // Файла ещё нет — это первый запуск, а не поломка.
            return RoutingSettings.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            problemRu = $"настройки не разобрались как JSON ({ex.Message}) — беру пустой список узлов";
            return RoutingSettings.Empty;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problemRu = "в файле настроек не объект — беру пустой список узлов";
                return RoutingSettings.Empty;
            }

            JsonElement nodesElement = FindRootProperty(document.RootElement, NodesKey);
            string? defaultAddress = FindRootString(document.RootElement, DefaultNodeKey);
            bool? autoSelect = FindRootBool(document.RootElement, AutoSelectKey);
            bool? multipath = FindRootBool(document.RootElement, MultipathKey);
            string? secondaryAddress = FindRootString(document.RootElement, SecondaryNodeKey);

            if (nodesElement.ValueKind == JsonValueKind.Undefined)
            {
                return new RoutingSettings([], null, autoSelect ?? false, multipath ?? false, secondaryAddress);
            }

            if (nodesElement.ValueKind != JsonValueKind.Array)
            {
                problemRu = $"вместо «{NodesKey}» лежит не список — беру пустой список узлов";
                return new RoutingSettings([], null, autoSelect ?? false, multipath ?? false, secondaryAddress);
            }

            var nodes = new List<RoutingNodeEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;

            foreach (JsonElement item in nodesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                string? rawAddress = ReadString(item, "Address");
                if (!RoutingNodeValidation.NormalizeIpv4(rawAddress, out string address) || !seen.Add(address))
                {
                    skipped++;
                    continue;
                }

                string name = ReadString(item, "Name") ?? string.Empty;
                string source = ReadString(item, "SourceRu") ?? string.Empty;
                if (!RoutingNodeSourceRu.IsHonest(source))
                {
                    // Источник в файле — не из наших подписей. Узел не выбрасываем, но и врать
                    // не будем: считаем такой узел введённым вручную.
                    source = RoutingNodeSourceRu.Manual;
                }

                nodes.Add(new RoutingNodeEntry(
                    address,
                    name.Length > 0 ? name : address,
                    source,
                    ReadDouble(item, "MedianRttMs"),
                    ReadDouble(item, "JitterMs"),
                    ReadDouble(item, "LossPercent"),
                    ReadTime(item, "MeasuredUtc")));
            }

            if (skipped > 0)
            {
                problemRu = $"часть записей в «{NodesKey}» битая ({skipped}) — они пропущены, остальные на месте";
            }

            string? defaultNormalized = defaultAddress is not null && RoutingNodeValidation.NormalizeIpv4(defaultAddress, out string d)
                ? d
                : null;

            if (defaultAddress is not null && defaultNormalized is null)
            {
                problemRu = (problemRu is null ? string.Empty : problemRu + ". ") +
                    $"значение «{DefaultNodeKey}» не IPv4-адрес — узел по умолчанию сброшен";
            }

            if (defaultNormalized is not null && !nodes.Any(node => node.Address.Equals(defaultNormalized, StringComparison.OrdinalIgnoreCase)))
            {
                defaultNormalized = null;
            }

            string? secondaryNormalized = secondaryAddress is not null && RoutingNodeValidation.NormalizeIpv4(secondaryAddress, out string s)
                ? s
                : null;
            if (secondaryAddress is not null && secondaryNormalized is null)
            {
                problemRu = (problemRu is null ? string.Empty : problemRu + ". ") +
                    $"значение «{SecondaryNodeKey}» не IPv4-адрес — второй узел сброшен";
            }
            if (secondaryNormalized is not null && !nodes.Any(node => node.Address.Equals(secondaryNormalized, StringComparison.OrdinalIgnoreCase)))
            {
                secondaryNormalized = null;
            }
            if (secondaryNormalized is not null && secondaryNormalized.Equals(defaultNormalized, StringComparison.OrdinalIgnoreCase))
            {
                secondaryNormalized = null;
            }

            return new RoutingSettings(nodes, defaultNormalized, autoSelect ?? false, multipath ?? false, secondaryNormalized);
        }
    }

    private static JsonElement FindRootProperty(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? FindRootString(JsonElement root, string name)
    {
        JsonElement element = FindRootProperty(root, name);
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static bool? FindRootBool(JsonElement root, string name)
    {
        JsonElement element = FindRootProperty(root, name);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, string property) =>
        FindRootProperty(element, property).ValueKind == JsonValueKind.String
            ? FindRootProperty(element, property).GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string property)
    {
        JsonValueKind kind = FindRootProperty(element, property).ValueKind;
        if (kind == JsonValueKind.Number && FindRootProperty(element, property).TryGetDouble(out double value)
            && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            return Math.Round(value, 2);
        }

        return null;
    }

    private static DateTime? ReadTime(JsonElement element, string property)
    {
        string? raw = ReadString(element, property);
        return raw is not null && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value)
            ? value
            : null;
    }
}
