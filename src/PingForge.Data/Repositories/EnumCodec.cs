namespace PingForge.Data.Repositories;

/// <summary>
/// Разбор enum-значений из БД без «тихой» подстановки значения по умолчанию.
/// Раньше игнорировался результат Enum.TryParse: при несовпадении профиль молча становился
/// LowLatencyGaming, что искажало отчёты.
/// </summary>
public static class EnumCodec
{
    public static T ParseOrThrow<T>(string? raw, string columnName)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidDataException($"Колонка «{columnName}» пуста, ожидалось значение типа {typeof(T).Name}.");
        }

        if (Enum.TryParse(raw, ignoreCase: false, out T value) && Enum.IsDefined(value))
        {
            return value;
        }

        throw new InvalidDataException($"Колонка «{columnName}» содержит неизвестное значение «{raw}» типа {typeof(T).Name}.");
    }

    public static T? ParseNullable<T>(string? raw, string columnName)
        where T : struct, Enum =>
        string.IsNullOrWhiteSpace(raw) ? null : ParseOrThrow<T>(raw, columnName);

    /// <summary>Разбор метки времени, записанной в формате «O»: устойчиво к культуре.</summary>
    public static DateTime ParseUtc(string raw, string columnName)
    {
        if (!DateTime.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime value))
        {
            throw new InvalidDataException($"Колонка «{columnName}» содержит некорректную метку времени «{raw}».");
        }

        return value.ToUniversalTime();
    }

    public static DateTime? ParseUtcNullable(string? raw, string columnName) =>
        string.IsNullOrWhiteSpace(raw) ? null : ParseUtc(raw, columnName);
}
