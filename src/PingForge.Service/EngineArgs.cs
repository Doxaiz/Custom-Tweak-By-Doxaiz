using System.Globalization;

namespace PingForge.Service;

/// <summary>Аргумент команды не подходит: не прислали, не число, вне предела. Текст — для пользователя.</summary>
public sealed class EngineArgumentException : Exception
{
    public EngineArgumentException(string messageRu)
        : base(messageRu)
    {
    }
}

/// <summary>
/// Разбор аргументов запроса. Все сообщения об ошибках называют команду, аргумент и допустимые значения:
/// «неверный аргумент» без объяснения — плохой ответ для службы, которая меняет настройки системы.
/// </summary>
public sealed class EngineArgs
{
    private readonly IReadOnlyDictionary<string, string> _args;
    private readonly string _commandWireName;

    public EngineArgs(IReadOnlyDictionary<string, string>? args, string commandWireName)
    {
        _args = args ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _commandWireName = commandWireName;
    }

    /// <summary>Сколько аргументов прислали. Нужно, чтобы отличить «не прислали» от «прислали пустое».</summary>
    public int Count => _args.Count;

    /// <summary>Значение аргумента или <c>null</c>.</summary>
    public string? Get(string name) =>
        _args.TryGetValue(name, out string? value) ? value : null;

    /// <summary>Обязательный аргумент: пусто или пробелы — ошибка с примером запроса.</summary>
    public string Require(string name, string exampleRu)
    {
        string? value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new EngineArgumentException(
                $"Команде «{_commandWireName}» нужен аргумент «{name}». Пример: {exampleRu}");
        }

        return value.Trim();
    }

    /// <summary>Целое число с пределом. Мусор — честная ошибка, а не «молча возьму ноль».</summary>
    public int RequireInt(string name, int min, int max, string exampleRu)
    {
        string raw = Require(name, exampleRu);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new EngineArgumentException(
                $"Аргумент «{name}» команды «{_commandWireName}» должен быть целым числом, а пришло «{raw}». Пример: {exampleRu}");
        }

        if (value < min || value > max)
        {
            throw new EngineArgumentException(
                $"Аргумент «{name}» команды «{_commandWireName}» вне предела: допустимо от {min} до {max}, а пришло {value}.");
        }

        return value;
    }

    /// <summary>Целое число с пределом и значением по умолчанию, если аргумента нет.</summary>
    public int GetInt(string name, int fallback, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(Get(name)))
        {
            return fallback;
        }

        string example = $"{{\"{name}\":\"{fallback}\"}}";
        return RequireInt(name, min, max, example);
    }

    /// <summary>Логический аргумент: принимает true/false, 1/0, да/нет. Непонятное — ошибка.</summary>
    public bool GetBool(string name, bool fallback)
    {
        string? raw = Get(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        string value = raw.Trim();
        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        if (value is "1" or "да" or "yes")
        {
            return true;
        }

        if (value is "0" or "нет" or "no")
        {
            return false;
        }

        throw new EngineArgumentException(
            $"Аргумент «{name}» команды «{_commandWireName}» должен быть true или false, а пришло «{value}».");
    }

    /// <summary>
    /// Список кодов твиков: «A,B,C». Пустые куски выбрасываются, повторы убираются,
    /// порядок сохраняется — так в журнале виден порядок, в котором твики выбирал пользователь.
    /// </summary>
    public IReadOnlyList<string> RequireList(string name, string exampleRu, int maxItems)
    {
        string raw = Require(name, exampleRu);
        string[] parts = raw.Split([',', ';', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            if (seen.Add(part))
            {
                result.Add(part);
            }
        }

        if (result.Count == 0)
        {
            throw new EngineArgumentException(
                $"В аргументе «{name}» команды «{_commandWireName}» нет ни одного твика. Пример: {exampleRu}");
        }

        if (result.Count > maxItems)
        {
            throw new EngineArgumentException(
                $"В аргументе «{name}» пришло {result.Count} твиков, а предел — {maxItems}. Это ошибка вызывающего.");
        }

        return result;
    }
}
