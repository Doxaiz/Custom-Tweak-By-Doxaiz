using System.Text.Json;
using PingForge.Core.Abstractions;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Читает, какие устройства ввода подключены сейчас. Источник — список PnP-устройств Windows
/// (<c>Get-PnpDevice</c>): он отвечает на вопрос «что реально подключено», а не «что когда-то стояло».
///
/// Если прочитать список не удалось, чтение честно помечается как неуспешное: пустой список устройств
/// и «не смогли прочитать» — разные вещи, и путать их нельзя (иначе твик акселерации предложится машине,
/// где мышь вообще не найдена, или наоборот пропадёт там, где она есть).
///
/// Ответ кэшируется на время сеанса: опрос PnP занимает заметное время, а список устройств за сеанс
/// диагностики не меняется.
/// </summary>
public sealed class WindowsPeripheralInventoryReader : IPeripheralInventoryReader
{
    /// <summary>Слова-маркеры беспроводного подключения. Список явный — пользователь должен видеть основание вывода.</summary>
    private static readonly string[] WirelessMarkers =
    {
        "bluetooth", "ble", "wireless", "wi-fi", "wifi", "2.4g", "2,4g", "5g", "radio",
        "receiver", "донгл", "беспровод", "wl"
    };

    private readonly ICommandPort _command;
    private readonly object _gate = new();
    private Task<PeripheralsSnapshot>? _cached;

    public WindowsPeripheralInventoryReader(ICommandPort command) =>
        _command = command ?? throw new ArgumentNullException(nameof(command));

    public Task<PeripheralsSnapshot> ReadAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _cached ??= ReadCoreAsync(ct);
        }
    }

    private async Task<PeripheralsSnapshot> ReadCoreAsync(CancellationToken ct)
    {
        const string script =
            "Get-PnpDevice -PresentOnly -Class Mouse,Keyboard | " +
            "Select-Object Class,FriendlyName,InstanceId | ConvertTo-Json -Compress -Depth 3";

        CommandResult result = await _command.RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return PeripheralsSnapshot.Unknown(
                "Перечень устройств ввода не прочитан: " +
                $"{FirstLine(result.StandardError) ?? "PowerShell вернул ошибку без текста."} " +
                "Проверки мыши и клавиатуры пропущены.");
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new PeripheralsSnapshot(
                Array.Empty<PeripheralInventoryItem>(),
                Array.Empty<PeripheralInventoryItem>(),
                new[]
                {
                    "Система не вернула ни одного устройства класса мышь или клавиатура. " +
                    "Проверки устройств ввода пропущены."
                });
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            var pointing = new List<PeripheralInventoryItem>();
            var keyboards = new List<PeripheralInventoryItem>();
            var unresolved = new List<string>();

            foreach (JsonElement element in EnumerateObjects(document.RootElement))
            {
                string deviceClass = ReadString(element, "Class");
                string name = ReadString(element, "FriendlyName");

                if (string.IsNullOrWhiteSpace(name))
                {
                    unresolved.Add("Одно из устройств ввода пришло без имени — оно не учтено.");
                    continue;
                }

                if (deviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
                {
                    pointing.Add(new PeripheralInventoryItem("Указатель", name, LooksWireless(name), true));
                }
                else if (deviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    keyboards.Add(new PeripheralInventoryItem("Клавиатура", name, LooksWireless(name), true));
                }
            }

            return new PeripheralsSnapshot(pointing, keyboards, unresolved);
        }
        catch (JsonException ex)
        {
            return PeripheralsSnapshot.Unknown(
                $"Ответ системы о устройствах ввода разобрать не удалось ({ex.Message}). Проверки устройств пропущены.");
        }
    }

    /// <summary>Беспроводным считаем только по явному маркеру в имени: угадывать по модели запрещено.</summary>
    private static bool LooksWireless(string name) =>
        WirelessMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[0] : null;
    }
}
