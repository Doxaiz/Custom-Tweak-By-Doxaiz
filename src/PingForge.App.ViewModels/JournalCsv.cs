using System.Text;

namespace PingForge.App.ViewModels;

/// <summary>
/// CSV журнала изменений: заголовок, экранирование и защита от формул.
///
/// Про формулы. Значения в журнал приходят из реестра и из текстов ошибок Windows, то есть это
/// чужие строки. Excel и LibreOffice считают формулой любое поле, начинающееся с «=», «+», «@»,
/// «-» или табуляции, и выполняют её при открытии файла. Поэтому такое поле получает апостроф
/// впереди — тогда программа показывает его как текст. Обычные числа («-1», «-3.5») не трогаются:
/// минус в числе формулой не является, и портить значение из-за страха незачем.
/// </summary>
public static class JournalCsv
{
    public const string Header = "Дата,Твик,Код,Предыдущее,Применено,Проверено,Статус";

    /// <summary>Собрать CSV из строк журнала: заголовок плюс по строке на запись.</summary>
    public static string Build(IEnumerable<JournalRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        builder.AppendLine(Header);

        foreach (JournalRowViewModel row in rows)
        {
            string date = row.LoggedUtc == DateTime.MinValue
                ? string.Empty
                : row.LoggedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            builder.AppendLine(string.Join(
                ",",
                EscapeField(date),
                EscapeField(row.TweakTitleRu),
                EscapeField(row.TweakCode),
                EscapeField(row.PreviousValueText),
                EscapeField(row.AppliedValueText),
                EscapeField(row.VerifiedActualText),
                EscapeField(row.StatusRu)));
        }

        return builder.ToString();
    }

    /// <summary>Экранировать одно поле: кавычки, запятые, переносы строк и формулы.</summary>
    public static string EscapeField(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (StartsFormula(value))
        {
            value = "'" + value;
        }

        bool needsQuotes = value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r');
        if (value.Contains('"'))
        {
            value = value.Replace("\"", "\"\"");
            needsQuotes = true;
        }

        return needsQuotes ? $"\"{value}\"" : value;
    }

    /// <summary>Поле начинается так, что таблица может выполнить его как формулу.</summary>
    public static bool StartsFormula(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value[0] switch
        {
            '=' or '+' or '@' or '\t' or '\r' => true,
            '-' => !IsPlainNumber(value),
            _ => false,
        };
    }

    /// <summary>«-1», «-3.5» и «-1,5» — числа, а не формулы: минус перед цифрой ничего не выполняет.</summary>
    private static bool IsPlainNumber(string value)
    {
        bool digitSeen = false;
        bool separatorSeen = false;

        for (int i = 1; i < value.Length; i++)
        {
            char symbol = value[i];
            if (char.IsAsciiDigit(symbol))
            {
                digitSeen = true;
                continue;
            }

            if ((symbol == '.' || symbol == ',') && !separatorSeen && digitSeen)
            {
                separatorSeen = true;
                continue;
            }

            return false;
        }

        return digitSeen;
    }
}
