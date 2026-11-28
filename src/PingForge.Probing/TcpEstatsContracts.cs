namespace PingForge.Probing;

/// <summary>
/// Задержка уже открытого TCP-соединения — только то, что отдаёт сетевой стек.
/// </summary>
/// <param name="RemoteAddress">Адрес сервера соединения.</param>
/// <param name="RemotePort">Порт сервера соединения.</param>
/// <param name="SmoothedRttMs">Сглаженная оценка RTT соединения, мс.</param>
/// <param name="RttVariationMs">Сглаженный разброс RTT (`RttVar`), мс.</param>
/// <param name="Samples">Сколько измерений RTT успел увидеть стек (`CountRtt`).</param>
/// <param name="Retransmissions">Сколько пакетов соединение отправило повторно (`PktsRetrans`).</param>
public sealed record LiveTcpConnectionStats(
    string RemoteAddress,
    int RemotePort,
    double SmoothedRttMs,
    double RttVariationMs,
    int Samples,
    int Retransmissions);

/// <summary>
/// Читатель статистики уже открытых TCP-соединений процесса.
///
/// Отдельный интерфейс здесь не ради красоты: сам вызов iphlpapi нельзя проверить на Linux,
/// а путать «нет соединения» с «нет прав» и с «система не Windows» нельзя — поэтому читатель
/// возвращает и список, и названную причину пустоты, а провайдер ничего не выдумывает.
/// </summary>
public interface ITcpConnectionStatsReader
{
    /// <summary>Почему список пуст: готовая причина для журнала. Пустая строка — объяснять нечего.</summary>
    string ProblemRu { get; }

    /// <summary>
    /// Установленные TCP-соединения процесса. Номер процесса 0 и меньше — все процессы:
    /// служба может не знать, какой процесс держит соединение.
    /// </summary>
    IReadOnlyList<LiveTcpConnectionStats> ReadEstablishedConnections(int processId);
}

/// <summary>
/// Единицы чисел сетевого стека — в одном месте, чтобы это можно было проверить тестом.
///
/// Снимок пути (<c>TCP_ESTATS_PATH_ROD_v0</c>) отдаёт время в миллисекундах; микросекунды — только
/// у снимка точной задержки (<c>TCP_ESTATS_FINE_RTT_ROD_v0</c>). Здесь читается снимок пути, поэтому
/// перевода нет: деление на тысячу занижало бы задержку в тысячу раз и показывало 0,0 мс на живой сети.
/// </summary>
public static class TcpEstatsMath
{
    /// <summary>Время пути уже в миллисекундах: единица названа явно, чтобы её не перепутали с точной задержкой.</summary>
    public static double PathMilliseconds(uint milliseconds) => milliseconds;
}
