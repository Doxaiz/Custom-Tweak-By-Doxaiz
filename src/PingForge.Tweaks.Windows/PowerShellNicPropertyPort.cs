using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Дополнительные свойства сетевого адаптера через командлеты Windows
/// (Get-NetAdapterAdvancedProperty / Set-NetAdapterAdvancedProperty).
///
/// Почему не пишем напрямую в ключ драйвера: список допустимых значений и их отображаемые имена
/// знает только драйвер, а прямая запись в ключ класса применяется лишь после перезапуска адаптера.
/// Командлет делает это штатным путём и сам сообщает, если значение недопустимо.
///
/// Цена решения — запуск PowerShell (1–2 секунды), поэтому ответы кэшируются на время сеанса.
/// </summary>
public sealed class PowerShellNicPropertyPort : INicPropertyPort
{
    private const string SelectFields = "RegistryKeyword,DisplayName,DisplayValue,ValidDisplayValues";

    private readonly ICommandPort _command;
    private readonly Dictionary<string, IReadOnlyList<NicAdapterRef>> _adaptersCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NicPropertySnapshot> _propertyCache = new(StringComparer.OrdinalIgnoreCase);

    public PowerShellNicPropertyPort(ICommandPort command) =>
        _command = command ?? throw new ArgumentNullException(nameof(command));

    public async Task<IReadOnlyList<NicAdapterRef>> ListAdaptersAsync(CancellationToken ct = default)
    {
        const string key = "adapters";
        if (_adaptersCache.TryGetValue(key, out IReadOnlyList<NicAdapterRef>? cached))
        {
            return cached;
        }

        CommandResult result = await RunPowerShellAsync(
            "Get-NetAdapter -Physical | Select-Object InterfaceGuid,InterfaceDescription,Name,Status,MediaType | ConvertTo-Json -Compress",
            ct).ConfigureAwait(false);

        var adapters = new List<NicAdapterRef>();

        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
                foreach (JsonElement element in EnumerateObjects(document.RootElement))
                {
                    string guid = ReadString(element, "InterfaceGuid");
                    if (guid.Length == 0)
                    {
                        continue;
                    }

                    string description = ReadString(element, "InterfaceDescription");
                    string alias = ReadString(element, "Name");
                    string status = ReadString(element, "Status");
                    string mediaType = ReadString(element, "MediaType");

                    bool isVirtual = mediaType.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
                                     || description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                     || description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);

                    // DisplayName здесь — имя подключения («Ethernet»), потому что именно его принимают
                    // и netsh (`name=Ethernet`), и командлеты PowerShell (`-Name Ethernet`).
                    adapters.Add(new NicAdapterRef(
                        guid,
                        alias.Length > 0 ? alias : description,
                        status.Equals("Up", StringComparison.OrdinalIgnoreCase),
                        IsPhysical: true,
                        IsVirtual: isVirtual));
                }
            }
            catch (JsonException)
            {
                // Пустой или неожиданный вывод — отдаём пустой список, а не выдуманный адаптер.
            }
        }

        _adaptersCache[key] = adapters;
        return adapters;
    }

    public async Task<NicPropertySnapshot> ReadAsync(TweakTarget target, string propertyKeyword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyKeyword);

        string cacheKey = $"{target.AdapterInterfaceGuid}|{propertyKeyword}";
        if (_propertyCache.TryGetValue(cacheKey, out NicPropertySnapshot? cached))
        {
            return cached;
        }

        string adapter = await ResolveAdapterSelectorAsync(target, ct).ConfigureAwait(false);
        if (adapter.Length == 0)
        {
            return NicPropertySnapshot.Unsupported("Не удалось определить сетевой адаптер, к которому относится параметр.");
        }

        CommandResult result = await RunPowerShellAsync(
            $"Get-NetAdapterAdvancedProperty -Name {Quote(adapter)} | Select-Object {SelectFields} | ConvertTo-Json -Compress",
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return NicPropertySnapshot.Unsupported(
                "Не удалось прочитать дополнительные свойства адаптера: " + FirstLine(result.StandardError, result.StandardOutput));
        }

        AdvancedPropertyEntry? entry = FindProperty(result.StandardOutput, propertyKeyword);
        if (entry is null)
        {
            return NicPropertySnapshot.Unsupported(
                $"Драйвер адаптера не сообщает свойство «{propertyKeyword}» — менять нечего.");
        }

        var snapshot = new NicPropertySnapshot(
            Exists: true,
            CanonicalValue: entry.DisplayValue,
            AllowedValues: entry.ValidDisplayValues,
            IsSupported: true,
            SupportReasonRu: entry.ValidDisplayValues.Count > 0
                ? $"Драйвер разрешает: {string.Join(", ", entry.ValidDisplayValues)}."
                : "Драйвер не сообщил список допустимых значений.");

        _propertyCache[cacheKey] = snapshot;
        return snapshot;
    }

    public async Task WriteAsync(TweakTarget target, string propertyKeyword, string canonicalValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyKeyword);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalValue);

        if (canonicalValue.IndexOfAny(new[] { '\'', '"', ';', '|', '&', '`', '\n', '\r' }) >= 0)
        {
            throw new InvalidOperationException(
                $"Значение «{canonicalValue}» содержит недопустимые символы и не будет подставлено в команду.");
        }

        string adapter = await ResolveAdapterSelectorAsync(target, ct).ConfigureAwait(false);
        if (adapter.Length == 0)
        {
            throw new InvalidOperationException("Не удалось определить сетевой адаптер для записи свойства.");
        }

        CommandResult readResult = await RunPowerShellAsync(
            $"Get-NetAdapterAdvancedProperty -Name {Quote(adapter)} | Select-Object {SelectFields} | ConvertTo-Json -Compress",
            ct).ConfigureAwait(false);

        if (!readResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Не удалось прочитать дополнительные свойства адаптера: " + FirstLine(readResult.StandardError, readResult.StandardOutput));
        }

        AdvancedPropertyEntry? entry = FindProperty(readResult.StandardOutput, propertyKeyword);
        if (entry is null)
        {
            throw new InvalidOperationException($"Драйвер адаптера не сообщает свойство «{propertyKeyword}».");
        }

        if (entry.ValidDisplayValues.Count > 0 &&
            !entry.ValidDisplayValues.Contains(canonicalValue, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Драйвер не принимает значение «{canonicalValue}» для «{entry.DisplayName}». " +
                $"Допустимы: {string.Join(", ", entry.ValidDisplayValues)}.");
        }

        // -NoRestart: адаптер не дёргаем, иначе пользователь потеряет соединение в момент применения.
        CommandResult writeResult = await RunPowerShellAsync(
            $"Set-NetAdapterAdvancedProperty -Name {Quote(adapter)} -DisplayName {Quote(entry.DisplayName)} " +
            $"-DisplayValue {Quote(canonicalValue)} -NoRestart",
            ct).ConfigureAwait(false);

        if (!writeResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Windows отказал в изменении «{entry.DisplayName}»: {FirstLine(writeResult.StandardError, writeResult.StandardOutput)}");
        }

        _propertyCache.Remove($"{target.AdapterInterfaceGuid}|{propertyKeyword}");
    }

    private async Task<string> ResolveAdapterSelectorAsync(TweakTarget target, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(target.AdapterDisplayName))
        {
            return target.AdapterDisplayName!;
        }

        IReadOnlyList<NicAdapterRef> adapters = await ListAdaptersAsync(ct).ConfigureAwait(false);
        NicAdapterRef? byGuid = adapters.FirstOrDefault(a =>
            string.Equals(a.InterfaceGuid, target.AdapterInterfaceGuid, StringComparison.OrdinalIgnoreCase));

        return byGuid?.DisplayName ?? string.Empty;
    }

    private static AdvancedPropertyEntry? FindProperty(string json, string propertyKeyword)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        foreach (JsonElement element in EnumerateObjects(document.RootElement))
        {
            string registryKeyword = ReadString(element, "RegistryKeyword");
            string displayName = ReadString(element, "DisplayName");

            if (!KeywordMatches(registryKeyword, propertyKeyword) && !KeywordMatches(displayName, propertyKeyword))
            {
                continue;
            }

            var allowed = new List<string>();
            if (element.TryGetProperty("ValidDisplayValues", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
            {
                allowed.AddRange(values.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
            }

            return new AdvancedPropertyEntry(displayName.Length > 0 ? displayName : registryKeyword, ReadString(element, "DisplayValue"), allowed);
        }

        return null;
    }

    /// <summary>Сравнение по нормализованному имени: «*EEE», «EEE» и «Energy-Efficient Ethernet» — одно свойство.</summary>
    private static bool KeywordMatches(string left, string right)
    {
        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        string a = Normalize(left);
        string b = Normalize(right);

        return a.Length > 0 && b.Length > 0 &&
               (a.Equals(b, StringComparison.Ordinal) ||
                a.EndsWith(b, StringComparison.Ordinal) ||
                b.EndsWith(a, StringComparison.Ordinal));
    }

    private Task<CommandResult> RunPowerShellAsync(string script, CancellationToken ct) =>
        _command.RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            ct);

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstLine(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string? line = candidate.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is not null)
            {
                return line;
            }
        }

        return "подробностей нет";
    }

    private sealed record AdvancedPropertyEntry(string DisplayName, string DisplayValue, IReadOnlyList<string> ValidDisplayValues);
}

/// <summary>
/// Определяет адаптер, через который идёт трафик: включённый физический интерфейс с шлюзом по умолчанию.
/// Приоритет отдаётся проводному подключению — «оптимизировать» Wi-Fi, когда вставлен кабель, бессмысленно.
/// </summary>
public sealed class WindowsActiveAdapterResolver : IActiveAdapterResolver
{
    // Определение активного адаптера не требует PowerShell: имя подключения и GUID берём
    // из самого .NET (NetworkInterface), а описание — уже для показа пользователю.
    private TweakTarget? _cached;

    public Task<TweakTarget> ResolveActiveAdapterAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return Task.FromResult(_cached);
        }

        List<NetworkInterface> candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.Wireless80211)
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("Не найден активный сетевой адаптер с шлюзом по умолчанию: оптимизировать нечего.");
        }

        NetworkInterface selected = candidates
            .OrderByDescending(IsWired)
            .ThenBy(n => n.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue)
            .First();

        string guid = selected.Id;

        _cached = new TweakTarget(
            ResourceLocator: $"NIC {selected.Description} ({selected.Name})",
            AdapterInterfaceGuid: guid,
            AdapterDisplayName: selected.Name);

        return Task.FromResult(_cached);
    }

    private static bool IsWired(NetworkInterface nic) =>
        nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit;
}
