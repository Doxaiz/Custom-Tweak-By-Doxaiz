namespace PingForge.Core.Abstractions;

/// <summary>
/// Одно устройство ввода, найденное в системе. Поля намеренно «плоские»: приложение показывает
/// пользователю ровно то, что смогло прочитать, а не догадку о модели устройства.
/// </summary>
public sealed record PeripheralInventoryItem(
    string DeviceClassRu,
    string Name,
    bool IsWireless,
    bool IsPresent);

/// <summary>
/// Что подключено к машине сейчас. Пустые списки — это факт «устройств такого класса не найдено»,
/// а <see cref="UnresolvedFields"/> — честное «прочитать не удалось». Смешивать эти два случая нельзя.
/// </summary>
public sealed record PeripheralsSnapshot(
    IReadOnlyList<PeripheralInventoryItem> PointingDevices,
    IReadOnlyList<PeripheralInventoryItem> Keyboards,
    IReadOnlyList<string> UnresolvedFields)
{
    public IReadOnlyList<PeripheralInventoryItem> PresentPointingDevices =>
        PointingDevices.Where(d => d.IsPresent).ToList();

    public IReadOnlyList<PeripheralInventoryItem> PresentKeyboards =>
        Keyboards.Where(d => d.IsPresent).ToList();

    /// <summary>Указатель работает по радио (Bluetooth/донгл) — у него есть собственная задержка опроса.</summary>
    public bool HasWirelessPointer => PresentPointingDevices.Any(d => d.IsWireless);

    public static PeripheralsSnapshot Unknown(string reason) =>
        new(Array.Empty<PeripheralInventoryItem>(), Array.Empty<PeripheralInventoryItem>(), new[] { reason });
}

/// <summary>
/// Чтение списка устройств ввода. Нужно, чтобы твики задержки ввода предлагались только тем,
/// у кого эти устройства действительно есть (и чтобы отличить беспроводную мышь от проводной).
/// </summary>
public interface IPeripheralInventoryReader
{
    Task<PeripheralsSnapshot> ReadAsync(CancellationToken ct = default);
}
