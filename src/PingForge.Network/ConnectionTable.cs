namespace PingForge.Network;

public enum ConnectionProtocol
{
    Tcp,
    Udp
}

/// <summary>
/// Строка таблицы соединений в том виде, в котором её отдаёт система. Никакой «умной» обработки здесь нет:
/// фильтрация и выбор потока — отдельно, в <see cref="GameFlowDetector"/>, чтобы это можно было проверить тестами.
/// </summary>
public sealed record RawConnectionRow(
    ConnectionProtocol Protocol,
    int ProcessId,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string TcpState = "");

/// <summary>Таблица соединений ОС. На Windows — GetExtendedUdpTable/GetExtendedTcpTable (iphlpapi).</summary>
public interface IRawConnectionTableProvider
{
    Task<IReadOnlyList<RawConnectionRow>> ReadAsync(CancellationToken ct = default);
}

/// <summary>Имя процесса по его идентификатору. Не удалось прочитать — null, а не «неизвестный».</summary>
public interface IProcessNameResolver
{
    Task<string?> TryGetProcessNameAsync(int processId, CancellationToken ct = default);
}

/// <summary>
/// Строка таблицы маршрутов ровно так, как её показывает сама Windows (`route print -4`):
/// адрес назначения, маска, шлюз, адрес интерфейса и метрика. Отдельного «владельца» у маршрута нет,
/// поэтому свои маршруты приложение узнаёт по журналу операций, а не по имени.
/// </summary>
public sealed record RouteTableEntry(
    string Destination,
    string Netmask,
    string NextHop,
    string InterfaceAddress,
    int Metric,
    bool IsOnLink = false,
    bool IsPersistent = false)
{
    public string Key => $"{Destination}|{Netmask}|{NextHop}";

    public bool IsDefaultRoute => Destination == "0.0.0.0" && Netmask == "0.0.0.0";

    public bool IsHostRoute => Netmask == "255.255.255.255";
}

/// <summary>Чтение таблицы маршрутов (route print / GetIpForwardTable2).</summary>
public interface IRouteTableReader
{
    Task<IReadOnlyList<RouteTableEntry>> ReadAsync(CancellationToken ct = default);
}
