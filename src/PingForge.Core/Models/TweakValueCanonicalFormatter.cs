using System.Globalization;

namespace PingForge.Core.Models;

/// <summary>
/// Единая каноническая форма значения для записи, сравнения и журнала.
/// Убирает класс багов вида «-1» против «0xFFFFFFFF» против «4294967295»,
/// а также проверяет, что значение из системы безопасно передавать аргументом внешней утилите.
/// </summary>
public static class TweakValueCanonicalFormatter
{
    public static string Format(TweakValueDataType dataType, object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return dataType switch
        {
            TweakValueDataType.DWord => FormatDWord(value),
            TweakValueDataType.QWord => FormatQWord(value),
            TweakValueDataType.MultiString => value is string[] arr
                ? string.Join("|", arr.Select(s => s.Trim()))
                : Normalize(value.ToString()),
            TweakValueDataType.Binary => value is byte[] bytes
                ? Convert.ToHexString(bytes).ToUpperInvariant()
                : Normalize(value.ToString()).ToUpperInvariant(),
            _ => Normalize(value.ToString())
        };
    }

    /// <summary>Строгий разбор DWORD: «0x…», «4294967295» и «-1» дают одно и то же число бит.</summary>
    public static int ParseDWord(string raw)
    {
        string s = raw.Trim();

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return unchecked((int)uint.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return unchecked((int)long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Строгий разбор QWORD. Работает и с unsigned-значением из реестра (18446744073709551615),
    /// и с представлением «-1», которое вернул API, читающий QWORD как signed.
    /// </summary>
    public static ulong ParseQWord(string raw)
    {
        string s = raw.Trim();

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (s.StartsWith('-'))
        {
            return unchecked((ulong)long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));
        }

        return ulong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public static bool TryParseDWord(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            value = ParseDWord(raw);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    public static bool TryParseQWord(string? raw, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            value = ParseQWord(raw);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Сравнение значений строго по типу. Нечисловой «числовой» тип НЕ бросает исключение:
    /// строка вида «restricted» просто не равна «normal» (так Windows отдаёт уровень автонастройки TCP).
    /// </summary>
    public static bool AreEqual(TweakValueDataType dataType, string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return dataType switch
        {
            TweakValueDataType.DWord =>
                TryParseDWord(left, out int l) && TryParseDWord(right, out int r) && l == r,
            TweakValueDataType.QWord =>
                TryParseQWord(left, out ulong lq) && TryParseQWord(right, out ulong rq) && lq == rq,
            TweakValueDataType.MultiString =>
                string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal),
            TweakValueDataType.Binary =>
                string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>Человеко-читаемое описание значения для журнала и карточек.</summary>
    public static string Describe(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "<не задано>" : raw.Trim();

    /// <summary>Список значений (например, DNS-серверов): «1.1.1.1|1.0.0.1» → массив.</summary>
    public static string[] SplitMultiString(string raw) =>
        raw.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Проверка перед подстановкой значения из системы в аргументы внешней утилиты.
    /// Значение, пришедшее от ОС или из реестра, — недоверенный ввод: «normal; shutdown -r»
    /// не должно попасть в командную строку. Разрешены только служебные символы
    /// (буквы, цифры, «.», «_», «:», «,», «-», «@»), без пробелов и разделителей команд.
    /// </summary>
    public static bool IsSafeCliToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 129)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or ',' or '-' or '@';
            if (!allowed)
            {
                return false;
            }
        }

        // Значение целиком не должно начинаться с «-»: иначе утилита примет его за собственный ключ.
        return !value.StartsWith('-');
    }

    private static string FormatDWord(object value) =>
        value switch
        {
            string s => ParseDWord(s).ToString(CultureInfo.InvariantCulture),
            _ => unchecked((int)Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)
        };

    private static string FormatQWord(object value) =>
        value switch
        {
            string s => ParseQWord(s).ToString(CultureInfo.InvariantCulture),
            ulong u => u.ToString(CultureInfo.InvariantCulture),
            _ => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)
        };

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
