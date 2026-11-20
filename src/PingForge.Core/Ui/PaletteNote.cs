namespace PingForge.Core.Ui;

/// <summary>Чем закончилась проверка контраста палитры.</summary>
public enum PaletteVerdict
{
    /// <summary>Оба цвета выше AAA: мелкие подписи читаются без напряжения.</summary>
    AboveAaa = 0,

    /// <summary>Выше AA, но ниже AAA: обычный текст проходит, мелкие подписи читаются хуже.</summary>
    AboveAa = 1,

    /// <summary>Ниже AA: такую палитру оставлять нельзя.</summary>
    BelowAa = 2,
}

/// <summary>
/// Подпись о контрасте палитры для «Настроек»: числа считаются по цветам темы, а не печатаются по памяти.
/// </summary>
/// <remarks>
/// Раньше в модели представления стояли готовые «17,1:1» и «7,5:1», а тест сверял их с цветами
/// <c>App.xaml</c>. Сверка спасала от вранья, но числа всё равно вписывались руками: смена палитры
/// означала правку текста, а вывод «выше AA и выше AAA» был утверждением, которое проверялось
/// отдельной строкой теста — то есть сам текст подписи об этом не знал.
///
/// Здесь считается всё: отношения берутся из <see cref="Contrast"/>, пороги — из его же констант,
/// а вывод о AA и AAA следует из самих чисел, а не из того, что хотелось написать. Если палитра
/// однажды не пройдёт AA, подпись скажет это прямо, а не «читается без напряжения». И цвета окно
/// передаёт настоящие — из ресурсов темы, а не из констант рядом с подписью.
/// </remarks>
public static class PaletteNote
{
    /// <summary>Цвет основного текста темы — значение по умолчанию до того, как окно передало своё.</summary>
    public const string TextColor = "#E9EFFB";

    /// <summary>Цвет подписей темы — значение по умолчанию.</summary>
    public const string MutedColor = "#8FA1BD";

    /// <summary>Цвет фона темы — значение по умолчанию.</summary>
    public const string BgColor = "#070B14";

    /// <summary>Вывод по двум отношениям контраста: оба выше AAA, выше AA или ниже AA.</summary>
    /// <param name="textRatio">Контраст основного текста на фоне.</param>
    /// <param name="mutedRatio">Контраст подписей на фоне.</param>
    public static PaletteVerdict VerdictOf(double textRatio, double mutedRatio)
    {
        double lowest = Math.Min(textRatio, mutedRatio);
        if (lowest >= Contrast.EnhancedText)
        {
            return PaletteVerdict.AboveAaa;
        }

        return lowest >= Contrast.NormalText ? PaletteVerdict.AboveAa : PaletteVerdict.BelowAa;
    }

    /// <summary>Вывод по цветам: то же самое, но цвета важнее чисел — их и передаёт окно.</summary>
    /// <param name="textColor">Цвет основного текста.</param>
    /// <param name="mutedColor">Цвет подписей.</param>
    /// <param name="bgColor">Цвет фона.</param>
    public static PaletteVerdict VerdictOf(string textColor, string mutedColor, string bgColor) =>
        VerdictOf(Contrast.Ratio(textColor, bgColor), Contrast.Ratio(mutedColor, bgColor));

    /// <summary>
    /// Подпись для человека: оба отношения с запятой и вывод, который следует из самих чисел.
    /// </summary>
    /// <param name="textColor">Цвет основного текста.</param>
    /// <param name="mutedColor">Цвет подписей.</param>
    /// <param name="bgColor">Цвет фона.</param>
    public static string SentenceRu(string textColor, string mutedColor, string bgColor)
    {
        double textRatio = Contrast.Ratio(textColor, bgColor);
        double mutedRatio = Contrast.Ratio(mutedColor, bgColor);

        string head =
            $"Контраст основного текста {textColor} на фоне {bgColor} — {Contrast.RatioRu(textColor, bgColor)}, " +
            $"подписей {mutedColor} — {Contrast.RatioRu(mutedColor, bgColor)}. ";

        return head + VerdictOf(textRatio, mutedRatio) switch
        {
            PaletteVerdict.AboveAaa =>
                $"Это выше требования AA ({Number(Contrast.NormalText)}:1) и выше AAA ({Number(Contrast.EnhancedText)}:1) " +
                "для обычного текста: мелкие подписи читаются без напряжения, а не «угадываются».",
            PaletteVerdict.AboveAa =>
                $"Это выше требования AA ({Number(Contrast.NormalText)}:1), но ниже AAA ({Number(Contrast.EnhancedText)}:1): " +
                "обычный текст проходит, мелкие подписи читаются хуже.",
            _ =>
                $"Это ниже требования AA ({Number(Contrast.NormalText)}:1): такую палитру оставлять нельзя — " +
                "текст придётся угадывать.",
        };
    }

    private static string Number(double value) =>
        value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
}
