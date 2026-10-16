using PingForge.Core.Abstractions;

namespace PingForge.Network.Routes;

/// <summary>
/// Чтение таблицы маршрутов через штатную <c>route print -4</c>. Отдельного «своего» чтения таблицы
/// маршрутов здесь нет намеренно: пользователь и служба поддержки могут сравнить вывод команды с тем,
/// что показывает программа, — числа должны совпадать.
///
/// Если команду выполнить не удалось, метод возвращает пустой список, а не выдуманные данные:
/// «маршрутов нет» и «прочитать не смогли» — разные вещи, и второе видно по пустому результату и журналу.
/// </summary>
public sealed class RoutePrintTableReader : IRouteTableReader
{
    private readonly ICommandPort _command;

    public RoutePrintTableReader(ICommandPort command) =>
        _command = command ?? throw new ArgumentNullException(nameof(command));

    public async Task<IReadOnlyList<RouteTableEntry>> ReadAsync(CancellationToken ct = default)
    {
        CommandResult result = await _command
            .RunAsync(RouteCommandBuilder.RouteTool, RouteCommandBuilder.Print(), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Array.Empty<RouteTableEntry>();
        }

        return RouteTableParser.Parse(result.StandardOutput);
    }
}
