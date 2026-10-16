using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PingForge.Network.Routes;

/// <summary>
/// Сборка аргументов для <c>route.exe</c>. Аргументы передаются списком, а не склеенной строкой,
/// и каждое значение проверяется до запуска: адрес — это адрес, метрика — это число.
/// Так значение из системы не может превратиться в дополнительный ключ команды.
///
/// Маршрут добавляется только на конкретный адрес сервера (маска 255.255.255.255) — это не VPN и не
/// «весь трафик через нас»: остальные приложения идут как обычно.
/// </summary>
public static class RouteCommandBuilder
{
    public const string RouteTool = "route";

    public static IReadOnlyList<string> Print() => new[] { "print", "-4" };

    public static IReadOnlyList<string> AddHostRoute(string destinationIp, string nextHop, int metric)
    {
        EnsureAddress(destinationIp, nameof(destinationIp));
        EnsureAddress(nextHop, nameof(nextHop));
        EnsureMetric(metric);

        return new[]
        {
            "add",
            destinationIp,
            "mask",
            "255.255.255.255",
            nextHop,
            "metric",
            metric.ToString(CultureInfo.InvariantCulture)
        };
    }

    public static IReadOnlyList<string> DeleteHostRoute(string destinationIp)
    {
        EnsureAddress(destinationIp, nameof(destinationIp));
        return new[] { "delete", destinationIp, "mask", "255.255.255.255" };
    }

    /// <summary>Команда целиком — для журнала и для тестов: видно, что именно запускалось.</summary>
    public static string Describe(IReadOnlyList<string> arguments) => $"{RouteTool} {string.Join(' ', arguments)}";

    internal static bool IsValidAddress(string value) =>
        IPAddress.TryParse(value, out IPAddress? ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && !ip.Equals(IPAddress.Any)
        && !ip.Equals(IPAddress.Broadcast);

    internal static bool IsValidMetric(int metric) => metric is >= 1 and <= 9999;

    private static void EnsureAddress(string value, string parameterName)
    {
        if (!IsValidAddress(value))
        {
            throw new ArgumentException($"«{value}» не является корректным IPv4-адресом.", parameterName);
        }
    }

    private static void EnsureMetric(int metric)
    {
        if (!IsValidMetric(metric))
        {
            throw new ArgumentOutOfRangeException(nameof(metric), metric, "Метрика должна быть в диапазоне 1…9999.");
        }
    }
}
