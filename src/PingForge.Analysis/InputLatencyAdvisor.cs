using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;

namespace PingForge.Analysis;

/// <summary>
/// Задержка ввода — это не задержка сети. Между движением руки и картинкой на экране стоят:
/// устройство ввода, «фильтр ввода» Windows, обработка кадра и только потом сеть. Этот советник
/// разбирает три первые звена и честно говорит, что из них правится твиками, а что нет.
///
/// Правила те же, что и у <see cref="NetworkAnalyzer"/>: предложение появляется только после чтения
/// фактического значения; «не знаем» и «нечего менять» — разные причины пропуска; чего правки НЕ делают,
/// сказано прямо. Роутер и модем программой не настраиваются — они только упоминаются в предупреждении,
/// со списком того, что стоит проверить вручную.
/// </summary>
public sealed class InputLatencyAdvisor
{
    public const string WarningPeripheralsUnknown = "PERIPHERALS_UNKNOWN";
    public const string WarningWirelessPointer = "WIRELESS_POINTER";
    public const string WarningInputOverWifi = "INPUT_OVER_WIFI";
    public const string WarningPathThroughRouter = "INPUT_PATH_THROUGH_ROUTER";

    /// <summary>Мышиные параметры идут вместе: по отдельности акселерация остаётся частично включённой.</summary>
    private static readonly string[] PointerTweaks =
    {
        "MOUSE_ACCEL_SPEED",
        "MOUSE_ACCEL_THRESHOLD1",
        "MOUSE_ACCEL_THRESHOLD2",
        "MOUSE_SENSITIVITY_NEUTRAL"
    };

    private static readonly string[] KeyboardTweaks = { "KEYBOARD_FILTERKEYS_OFF" };

    private static readonly string[] FrameTweaks = { "GAME_DVR_OFF", "GAME_DVR_CAPTURE_OFF" };

    private readonly INetworkContextReader _context;
    private readonly IPeripheralInventoryReader _peripherals;
    private readonly ITweakCatalog? _catalog;

    public InputLatencyAdvisor(
        INetworkContextReader context,
        IPeripheralInventoryReader peripherals,
        ITweakCatalog? catalog = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _peripherals = peripherals ?? throw new ArgumentNullException(nameof(peripherals));
        _catalog = catalog;
    }

    public async Task<NetworkAnalysisReport> AdviseAsync(CancellationToken ct = default)
    {
        NetworkContextSnapshot context = await _context.ReadAsync(ct).ConfigureAwait(false);
        PeripheralsSnapshot devices = await _peripherals.ReadAsync(ct).ConfigureAwait(false);

        var warnings = new List<InterferenceWarning>();
        var recommendations = new List<TweakRecommendationItem>();
        var skipped = new List<string>();
        var notes = new List<string>();

        AdapterInventoryItem? active = context.ActiveAdapterName is { } activeName
            ? context.Adapters.FirstOrDefault(a =>
                string.Equals(a.Name, activeName, StringComparison.OrdinalIgnoreCase))
            : context.PhysicalUpAdapters.FirstOrDefault();

        AddHardwareWarnings(context, devices, active, warnings);

        int checks = 0;

        if (devices.PresentPointingDevices.Count == 0)
        {
            skipped.Add(devices.UnresolvedFields.Count > 0
                ? "Акселерация указателя: список устройств ввода прочитать не удалось — править наугад нельзя."
                : "Акселерация указателя: указатель не обнаружен — менять нечего.");
        }
        else
        {
            string pointerNames = string.Join(", ", devices.PresentPointingDevices.Select(d => d.Name));
            string pointerReason =
                $"Обнаружен указатель: {pointerNames}. Ускорение делает движение мыши нелинейным — в шутерах " +
                "это ощущается как вязкий прицел и мешает привыкнуть к чувствительности.";

            foreach (string code in PointerTweaks)
            {
                checks++;
                string reason = code == "MOUSE_SENSITIVITY_NEUTRAL"
                    ? pointerReason + " Нейтральное положение 6/11 — единственное, где Windows не домножает движение."
                    : pointerReason;
                await AddAdviceAsync(code, reason, recommendations, skipped, notes, ct).ConfigureAwait(false);
            }
        }

        if (devices.PresentKeyboards.Count == 0)
        {
            skipped.Add(devices.UnresolvedFields.Count > 0
                ? "Фильтр ввода клавиатуры: список устройств прочитать не удалось — состояние фильтра не оцениваем."
                : "Фильтр ввода клавиатуры: клавиатура не обнаружена — менять нечего.");
        }
        else
        {
            checks++;
            string keyboardNames = string.Join(", ", devices.PresentKeyboards.Select(d => d.Name));
            await AddAdviceAsync(
                KeyboardTweaks[0],
                $"Клавиатура найдена: {keyboardNames}. «Фильтр ввода» Windows отбрасывает короткие и повторные нажатия — " +
                "если он включён, часть нажатий не доходит до игры.",
                recommendations,
                skipped,
                notes,
                ct).ConfigureAwait(false);
        }

        // Автоповтор клавиш в списки предложений не попадает осознанно: на отклик в игре он не влияет.
        skipped.Add("Автоповтор клавиш (KeyboardDelay): на игровой отклик не влияет — в предложения не добавлен.");

        foreach (string code in FrameTweaks)
        {
            checks++;
            await AddAdviceAsync(
                code,
                "Фоновая запись Xbox (Game DVR) держит буфер последних секунд игры и отбирает кадры — " +
                "кадры приходят позже, инпут ощущается «тяжёлым».",
                recommendations,
                skipped,
                notes,
                ct).ConfigureAwait(false);
        }

        checks++;
        await AddNicPriorityAdviceAsync(active, recommendations, skipped, notes, ct).ConfigureAwait(false);

        notes.Add("Эти правки убирают задержку на вашей стороне: ускорение указателя, фильтр клавиатуры, фоновая запись " +
                  "и очередь пакетов на сетевой карте. Скорость интернета и задержку внутри игрового сервера они не меняют.");

        if (active?.HasIpv4Gateway == true)
        {
            notes.Add("Твики применяются только на вашей машине. Роутер или модем (шлюз активного подключения" +
                      (active.Ipv4GatewayAddress is { Length: > 0 } gw ? $" {gw}" : string.Empty) +
                      ") программой не настраивается — для него ниже дан список ручных проверок.");
        }

        string summary =
            $"Проверок выполнено: {checks}. Предупреждений: {warnings.Count}. Предлагается изменить: {recommendations.Count}. " +
            $"Пропущено проверок: {skipped.Count}.";

        return new NetworkAnalysisReport(warnings, recommendations, skipped, notes, summary);
    }

    private static void AddHardwareWarnings(
        NetworkContextSnapshot context,
        PeripheralsSnapshot devices,
        AdapterInventoryItem? active,
        List<InterferenceWarning> warnings)
    {
        if (devices.UnresolvedFields.Count > 0)
        {
            warnings.Add(new InterferenceWarning(
                WarningPeripheralsUnknown,
                "Список устройств ввода прочитать не удалось",
                string.Join(" ", devices.UnresolvedFields),
                "Проверки устройств ввода пропущены. Приложение не подставляет устройства «по памяти»: без списка " +
                "непонятно, есть ли вообще мышь и клавиатура.",
                TweakRiskLevel.Low));
        }

        if (devices.HasWirelessPointer)
        {
            string names = string.Join(", ", devices.PresentPointingDevices.Where(d => d.IsWireless).Select(d => d.Name));
            warnings.Add(new InterferenceWarning(
                WarningWirelessPointer,
                "Беспроводной указатель",
                $"Мышь работает по радио: {names}. У беспроводных мышей своя задержка — частота опроса радиоканала " +
                "обычно 125 Гц, то есть 8 мс только на передачу движения.",
                "Акселерацию уберём — движение станет линейным, но задержку радиоканала правки реестра не сокращают. " +
                "Если важна каждая миллисекунда, помогает проводная мышь или беспроводная с высокой частотой опроса " +
                "(и приёмник в USB-порт на передней панели, а не через хаб).",
                TweakRiskLevel.Low));
        }

        if (active?.IsWireless == true)
        {
            warnings.Add(new InterferenceWarning(
                WarningInputOverWifi,
                "Активное подключение — Wi-Fi",
                $"Трафик уходит по радиоэфиру ({active.Name}). У Wi-Fi задержка зависит от повторов передачи: " +
                "несколько миллисекунд добавляются не на вашей машине, а в эфире.",
                "Проводное подключение убирает эту составляющую целиком. Если кабеля нет — можно поставить роутер ближе " +
                "и выбрать канал свободнее, но обещать «пинг как по кабелю» программа не станет.",
                TweakRiskLevel.Medium));
        }

        if (active?.HasIpv4Gateway == true)
        {
            string gatewayText = active.Ipv4GatewayAddress is { Length: > 0 } gw
                ? $"Шлюз {gw}"
                : "Шлюз активного подключения";

            warnings.Add(new InterferenceWarning(
                WarningPathThroughRouter,
                "Между вами и игрой стоит роутер или модем",
                $"{gatewayText} — значит, трафик проходит через промежуточное устройство. Его очередь и режим " +
                "энергосбережения добавляют задержку к каждому пакету, а войти в него программа не имеет права.",
                "Что стоит проверить вручную в веб-панели устройства: включён ли режим QoS/приоритета трафика, " +
                "выключено ли энергосбережение Wi-Fi-модуля, не включён ли «режим моста» для вашего модема, " +
                "не перегружен ли восходящий канал загрузками в комнате. Эти шаги вы делаете сами — приложение их не выполняет.",
                TweakRiskLevel.Low));
        }

        if (context.UnresolvedFields.Count > 0)
        {
            warnings.Add(new InterferenceWarning(
                NetworkAnalyzer.WarningContextIncomplete,
                "Часть данных о сети прочитать не удалось",
                string.Join(" ", context.UnresolvedFields),
                "Связанные проверки пропущены: приложение не подставляет значения по умолчанию вместо непрочитанных.",
                TweakRiskLevel.Low));
        }
    }

    private async Task AddNicPriorityAdviceAsync(
        AdapterInventoryItem? active,
        List<TweakRecommendationItem> recommendations,
        List<string> skipped,
        List<string> notes,
        CancellationToken ct)
    {
        const string code = "NIC_PACKET_PRIORITY_ON";

        if (active is null)
        {
            skipped.Add($"{code}: активный сетевой адаптер не определён — приоритет пакетов не настраиваем.");
            return;
        }

        if (active.LooksLikeVpn || active.IsTunnel)
        {
            skipped.Add($"{code}: активный адаптер «{active.Name}» выглядит виртуальным (VPN/туннель) — " +
                        "приоритет пакетов на нём не настраивается.");
            return;
        }

        await AddAdviceAsync(
            code,
            $"Активный адаптер: {active.Name}. Приоритет пакетов в драйвере означает, что игра помечает свои пакеты, " +
            "а карта отправляет их раньше фоновых загрузок. Выигрыш работает только если роутер уважает приоритет.",
            recommendations,
            skipped,
            notes,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Одна проверка «прочитать → сравнить → предложить». Ни одного предложения без чтения:
    /// если значение прочитать нельзя, твик уходит в «пропущено» с причиной, а не в список изменений.
    /// </summary>
    private async Task AddAdviceAsync(
        string tweakCode,
        string reasonRu,
        List<TweakRecommendationItem> recommendations,
        List<string> skipped,
        List<string> notes,
        CancellationToken ct)
    {
        ITweak? tweak = _catalog?.GetByCode(tweakCode);
        if (tweak is null)
        {
            notes.Add($"{tweakCode}: твик отсутствует в каталоге этой сборки — предложение пропущено.");
            return;
        }

        TweakDescriptor descriptor = tweak.Descriptor;

        (TweakTarget target, TweakSupport support) = await tweak.ResolveTargetAsync(ct).ConfigureAwait(false);
        if (!support.IsSupported)
        {
            skipped.Add($"{descriptor.Code} — {descriptor.TitleRu}: {support.ReasonRu}");
            return;
        }

        CapturedParameterState captured = await tweak.CaptureAsync(target, ct).ConfigureAwait(false);
        string recommended = descriptor.RecommendedRawValue;

        if (captured.ExistedBefore
            && TweakValueCanonicalFormatter.AreEqual(descriptor.DataType, captured.CapturedRawValue, recommended))
        {
            skipped.Add($"{descriptor.Code} — {descriptor.TitleRu}: уже настроено — текущее значение " +
                        $"{TweakValueCanonicalFormatter.Describe(captured.CapturedRawValue)} совпадает с рекомендуемым.");
            return;
        }

        recommendations.Add(new TweakRecommendationItem(
            TweakCode: descriptor.Code,
            TitleRu: descriptor.TitleRu,
            Badge: descriptor.Badge,
            RequiresManualVerification: descriptor.RequiresManualVerification || support.RequiresManualVerification,
            CurrentValueDisplay: captured.ExistedBefore
                ? $"Сейчас: {TweakValueCanonicalFormatter.Describe(captured.CapturedRawValue)}"
                : "Параметр не задан — Windows использует значение по умолчанию",
            RecommendedValueDisplay: recommended,
            ReasonRu: reasonRu,
            Risk: descriptor.Risk));
    }
}
