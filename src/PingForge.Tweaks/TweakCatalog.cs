using System.Globalization;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Tweaks;

namespace PingForge.Tweaks;

/// <summary>Порты платформы, которые каталогу нужны для работы. Любой может отсутствовать.</summary>
public sealed record TweakPlatformPorts(
    IRegistryPort? Registry = null,
    INicPropertyPort? NicProperties = null,
    ICommandPort? Command = null,
    IActiveAdapterResolver? ActiveAdapter = null,
    IMtuAdviceProvider? MtuAdvice = null,
    IDnsAdviceProvider? DnsAdvice = null,
    INicPropertyOptionProvider? NicOptions = null);

/// <summary>
/// Каталог твиков. Здесь только данные + сборка: логика применения и проверки лежит в ValueBackedTweak.
/// Если порт платформы не подключён, твик честно отвечает «не поддерживается», а не имитирует успех.
/// </summary>
public sealed class TweakCatalog : ITweakCatalog
{
    private const string TcpInterfaceParams =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + RegistryValueAccessor.AdapterGuidToken;

    private const string MultimediaProfile =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private const string MultimediaGames = MultimediaProfile + @"\Tasks\Games";

    private static readonly IReadOnlyList<TweakDefinition> Definitions = BuildDefinitions();

    private readonly TweakPlatformPorts _ports;
    private readonly List<ITweak> _tweaks;

    public TweakCatalog(TweakPlatformPorts ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _tweaks = Definitions.Select(Build).ToList();
    }

    public IReadOnlyList<TweakDefinition> GetDefinitions() => Definitions;

    public IReadOnlyList<ITweak> GetAllTweaks() => _tweaks;

    public IReadOnlyList<IActionTask> GetAllActionTasks() => Array.Empty<IActionTask>();

    public ITweak? GetByCode(string tweakCode) =>
        string.IsNullOrWhiteSpace(tweakCode)
            ? null
            : _tweaks.FirstOrDefault(t => string.Equals(t.Id, tweakCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Состав пресета. Пометки автора каталога решают, ЧТО предлагается, инвариант пресета —
    /// что МОЖНО предлагать: твик с бейджем «не советуем» не попадёт в пресет, даже если помечен.
    /// Отсюда и разные размеры пресетов: «низкий пинг» — это сетевые твики, «скорость» — буферы и окно,
    /// «кадры» — питание, фон, планировщик и ввод.
    /// </summary>
    public IReadOnlyList<ITweak> GetByPreset(TweakProfilePreset preset)
    {
        bool Marked(TweakDescriptor d) => preset switch
        {
            TweakProfilePreset.LowLatencyGaming => d.IncludedInLowPingPreset,
            TweakProfilePreset.MaxThroughput => d.IncludedInMaxSpeedPreset,
            TweakProfilePreset.FpsAndResponsiveness => d.IncludedInFpsPreset,
            _ => true
        };

        return _tweaks
            .Where(t => Marked(t.Descriptor) && PresetPolicy.IsAllowedInPreset(t.Descriptor, preset))
            .ToList();
    }

    private ITweak Build(TweakDefinition definition)
    {
        IValueAccessor accessor = CreateAccessor(definition);
        TweakDescriptor descriptor = definition.ToDescriptor();

        return definition.IsComputedTarget
            ? new ComputedValueTweak(descriptor, accessor, (target, ct) => ComputeAsync(definition, target, ct), _ports.ActiveAdapter)
            : new ValueBackedTweak(descriptor, accessor, _ports.ActiveAdapter);
    }

    private IValueAccessor CreateAccessor(TweakDefinition definition) => definition.Source switch
    {
        TweakValueSource.RegistryValue r => _ports.Registry is null
            ? Missing("Реестр недоступен: порт платформы не подключён.")
            : new RegistryValueAccessor(_ports.Registry, r.Root, r.SubKeyTemplate, r.ValueName, r.DataType),

        TweakValueSource.NicAdvancedProperty n => _ports.NicProperties is null
            ? Missing("Свойства сетевого адаптера недоступны: порт платформы не подключён.")
            : new NicPropertyAccessor(_ports.NicProperties, n.Keyword),

        TweakValueSource.NetshTcpGlobal g => _ports.Command is null
            ? Missing("netsh недоступен: порт платформы не подключён.")
            : new NetshGlobalAccessor(_ports.Command, g.SettingKey),

        TweakValueSource.NetshSubinterfaceMtu => _ports.Command is null
            ? Missing("netsh недоступен: порт платформы не подключён.")
            : new NetshSubinterfaceMtuAccessor(_ports.Command),

        TweakValueSource.NetshDnsServers => _ports.Command is null
            ? Missing("netsh недоступен: порт платформы не подключён.")
            : new NetshDnsServersAccessor(_ports.Command),

        _ => Missing("Источник значения не описан.")
    };

    private Task<ComputedTargetValue> ComputeAsync(TweakDefinition definition, TweakTarget target, CancellationToken ct)
    {
        if (definition.Source is TweakValueSource.NetshSubinterfaceMtu)
        {
            return _ports.MtuAdvice is null
                ? Task.FromResult(ComputedTargetValue.Unavailable(
                    "MTU не измерялся: нет доступа к пробам с флагом DF. Сначала выполните диагностику сети."))
                : _ports.MtuAdvice.GetRecommendedMtuAsync(target, ct);
        }

        if (definition.Source is TweakValueSource.NetshDnsServers)
        {
            return _ports.DnsAdvice is null
                ? Task.FromResult(ComputedTargetValue.Unavailable(
                    "DNS не измерялся: нет доступа к бенчмарку DNS. Сначала выполните диагностику сети."))
                : _ports.DnsAdvice.GetRecommendedDnsAsync(target, ct);
        }

        if (definition.Source is TweakValueSource.NicAdvancedProperty nic)
        {
            return ComputeNicOptionAsync(nic.Keyword, target, ct);
        }

        return Task.FromResult(ComputedTargetValue.Unavailable("Для этого твика вычисляемое значение не предусмотрено."));
    }

    private async Task<ComputedTargetValue> ComputeNicOptionAsync(string keyword, TweakTarget target, CancellationToken ct)
    {
        if (_ports.NicOptions is null)
        {
            return ComputedTargetValue.Unavailable(
                "Драйвер не сообщил допустимые значения свойства — применить наугад запрещено.");
        }

        IReadOnlyList<string> allowed = await _ports.NicOptions.GetAllowedValuesAsync(target, keyword, ct).ConfigureAwait(false);
        var numeric = allowed
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? (int?)n : null)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToList();

        if (numeric.Count == 0)
        {
            return ComputedTargetValue.Unavailable(
                $"Драйвер не сообщил числовые варианты «{keyword}» — значение не выбрано.");
        }

        int max = numeric.Max();
        return new ComputedTargetValue(
            max.ToString(CultureInfo.InvariantCulture),
            TweakValueDataType.DWord,
            $"Выбрано максимальное значение «{keyword}», которое разрешает драйвер: {max} (варианты драйвера: {string.Join(", ", numeric.Distinct().OrderBy(n => n))}).");
    }

    private static IValueAccessor Missing(string reason) => new MissingPortAccessor(reason);

    private static IReadOnlyList<TweakDefinition> BuildDefinitions()
    {
        TweakDefinition[] all = BuiltInDefinitions().Concat(TweakCatalogExtra.All).ToArray();

        // Пресет «отзывчивость и кадры» собирается правилом, а не ручными пометками: в него входит всё,
        // что относится к кадрам, вводу или системе и при этом не ухудшает стабильность. Так пресет
        // не разъезжается с каталогом, когда добавляется новый твик.
        return all
            .Select(d => PresetPolicy.IsAllowedInPreset(d.ToDescriptor(), TweakProfilePreset.FpsAndResponsiveness)
                         && d.EffectiveFocus is TweakFocus.Fps or TweakFocus.Input or TweakFocus.System
                ? d with { IncludedInFpsPreset = true }
                : d)
            .ToArray();
    }

    private static IReadOnlyList<TweakDefinition> BuiltInDefinitions() => new TweakDefinition[]
    {
        // ─────────────── TCP/IP: параметры конкретного интерфейса ───────────────
        new(
            "TCP_ACK_FREQUENCY", "Частота подтверждений TCP (TcpAckFrequency)", "TcpAckFrequency",
            TweakCategory.TcpIpStack, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, TcpInterfaceParams, "TcpAckFrequency", TweakValueDataType.DWord),
            "Заставляет стек подтверждать каждый второй принятый сегмент вместо ожидания таймера задержанных подтверждений.",
            "Стоит применять для игр: уменьшает задержку подтверждений и убирает микрорывки при обмене мелкими пакетами. " +
            "Побочный эффект — чуть больше служебного трафика.",
            "По умолчанию параметр отсутствует (Windows использует таймер задержанных подтверждений).",
            "1", TweakValueDataType.DWord, PingImpactDirection: -1, SpeedImpactDirection: 0, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "TCP_NO_DELAY", "Отключение алгоритма Нейгла на интерфейсе (TCPNoDelay)", "TCPNoDelay",
            TweakCategory.TcpIpStack, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, TcpInterfaceParams, "TCPNoDelay", TweakValueDataType.DWord),
            "Запрещает стеку копить мелкие отправки перед отправкой: пакет уходит сразу, а не через таймер.",
            "Стоит применять для игр и голосовой связи: убирает задержку «склеивания» мелких пакетов. " +
            "Для скачивания файлов и торрентов это значение не нужно.",
            "По умолчанию параметр отсутствует.",
            "1", TweakValueDataType.DWord, PingImpactDirection: -1, SpeedImpactDirection: 0, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "TCP_INITIAL_RTT", "Начальное значение RTT (InitialRtt)", "InitialRtt",
            TweakCategory.TcpIpStack, TweakRiskLevel.Medium, GameRecommendationBadge.Caution,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, TcpInterfaceParams, "InitialRtt", TweakValueDataType.DWord),
            "Задаёт оценку круговой задержки до первого измерения. Влияет на стартовый таймаут и скорость разгона окна.",
            "Применять не рекомендую без конкретной проблемы: на современной Windows значение влияет слабо, " +
            "а занижение приводит к лишним повторным передачам на дальних серверах.",
            "По умолчанию 3000 мс.",
            "300", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: -1, StabilityImpactDirection: -1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false,
            EffectUnconfirmedOnModernWindows: true),

        new(
            "TCP_SYN_RETRANSMISSIONS", "Повторы SYN при подключении (SynRetransmissions)", "SynRetransmissions",
            TweakCategory.TcpIpStack, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, TcpInterfaceParams, "SynRetransmissions", TweakValueDataType.DWord),
            "Сколько раз повторять запрос на подключение, прежде чем сообщить об ошибке.",
            "Полезно для игр: при обрыве сессии система быстрее отдаёт ошибку, и клиент раньше начинает переподключение " +
            "вместо долгого ожидания. На пинг внутри матча не влияет.",
            "По умолчанию Windows использует 2 попытки; значение 1 заметно ускоряет отказ, но на плохом канале чаще будет ошибка соединения.",
            "2", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        // ─────────────── Планировщик мультимедиа и приоритеты игр ───────────────
        new(
            "MMCSS_NETWORK_THROTTLING", "Снятие троттлинга сети (NetworkThrottlingIndex)", "NetworkThrottlingIndex",
            TweakCategory.PowerAndScheduler, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, MultimediaProfile, "NetworkThrottlingIndex", TweakValueDataType.DWord),
            "Пока активна задача MMCSS, сетевой стек принимает не больше 10 пакетов за миллисекунду для трафика, " +
            "не относящегося к этой задаче. Значение 4294967295 снимает ограничение полностью. В документации MMCSS " +
            "на Microsoft Learn этого параметра нет — он описан в материалах поддержки и документации производителей " +
            "сетевых адаптеров.",
            "Снимать ограничение имеет смысл, когда в фоне идёт загрузка или стрим: именно фон упирается в потолок " +
            "по пакетам. На задержку в матче это не влияет — она определяется маршрутом до сервера, а не этим " +
            "ограничением. Действует после перезагрузки.",
            "По умолчанию 10; допустимый диапазон 1–70, 4294967295 — выключено.",
            "4294967295", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: true, RebootRequired: true, IncludedInFpsPreset: true),

        new(
            "MMCSS_SYSTEM_RESPONSIVENESS", "Отзывчивость системы для игр (SystemResponsiveness)", "SystemResponsiveness",
            TweakCategory.PowerAndScheduler, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, MultimediaProfile, "SystemResponsiveness", TweakValueDataType.DWord),
            "Доля процессорного времени, которую планировщик мультимедиа оставляет фоновым задачам.",
            "Стоит применять для игр: меньше времени отдаётся фоновым задачам, игровой поток реже ждёт процессор. " +
            "Значения ниже 10 бессмысленны: документация MMCSS говорит, что всё, что ниже 10, система поднимает до 20.",
            "По умолчанию 20; значения ниже 10 поднимаются до 20, а 100 отключает MMCSS целиком.",
            "10", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "GAME_TASK_SCHEDULING_CATEGORY", "Категория планирования для игр (Scheduling Category)", "Scheduling Category",
            TweakCategory.PowerAndScheduler, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, MultimediaGames, "Scheduling Category", TweakValueDataType.String),
            "Помечает игры как высокоприоритетную задачу мультимедиа (MMCSS).",
            "Стоит применять для игр: Windows иначе распределяет кванты времени для игрового процесса.",
            "По умолчанию «Medium».",
            "High", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "GAME_TASK_PRIORITY", "Приоритет игровой задачи (Priority)", "Priority",
            TweakCategory.PowerAndScheduler, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, MultimediaGames, "Priority", TweakValueDataType.DWord),
            "Базовый приоритет потока для задачи MMCSS «Games».",
            "Стоит применять для игр: игровой поток реже вытесняется фоновыми задачами. Держите значения 6, а не 8: " +
            "слишком высокий приоритет приводит к фризам, когда игровой поток не отдаёт процессор системе ввода. " +
            "Важно: при «Scheduling Category» = High документация MMCSS говорит, что приоритет всегда трактуется " +
            "как 2, поэтому пара «High + 6» ничего не меняет — либо категория Medium, либо этот параметр не трогаем.",
            "По умолчанию 2; при категории High значение игнорируется (система подставляет 2).",
            "6", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "GAME_TASK_SFIO_PRIORITY", "Приоритет операций с файлами (SFIO Priority)", "SFIO Priority",
            TweakCategory.PowerAndScheduler, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, MultimediaGames, "SFIO Priority", TweakValueDataType.String),
            "Приоритет фоновых файловых операций игрового процесса.",
            "В документации MMCSS этот параметр помечен как неиспользуемый (not used): Windows его пишет, но ни на что " +
            "не влияет. Поэтому твик лежит в каталоге как справочный — на подгрузку текстур и на сетевую задержку " +
            "он не действует, и обещать эффект мы не будем.",
            "По умолчанию «Normal»; значение ни на что не влияет — так написано в документации MMCSS.",
            "High", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false, EffectUnconfirmedOnModernWindows: true),

        // ─────────────── Дополнительные свойства сетевого адаптера ───────────────
        new(
            "NIC_ENERGY_EFFICIENT_ETHERNET", "Отключить Energy-Efficient Ethernet", "Energy-Efficient Ethernet",
            TweakCategory.NicDriver, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.NicAdvancedProperty("Energy-Efficient Ethernet"),
            "Отключает энергосберегающий режим PHY, который добавляет задержку при переходе в активное состояние.",
            "Стоит применять для игр на проводном подключении: EEE экономит ватты, но добавляет задержку на первом пакете.",
            "Драйвер обычно оставляет EEE включённым.",
            "Disabled", TweakValueDataType.String, PingImpactDirection: -1, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "NIC_INTERRUPT_MODERATION", "Отключить объединение прерываний", "Interrupt Moderation",
            TweakCategory.NicDriver, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            new TweakValueSource.NicAdvancedProperty("Interrupt Moderation"),
            "Каждый пакет обрабатывается сразу, без ожидания накопления пакетов драйвером.",
            "Может снизить задержку, но заметно поднимает нагрузку на процессор. Ставьте только если процессор не загружен " +
            "и вы готовы к росту CPU при большом трафике.",
            "По умолчанию объединение прерываний включено.",
            "Disabled", TweakValueDataType.String, PingImpactDirection: -1, SpeedImpactDirection: 0, StabilityImpactDirection: -1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false),

        new(
            "NIC_FLOW_CONTROL", "Отключить управление потоком (Flow Control)", "Flow Control",
            TweakCategory.NicDriver, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            new TweakValueSource.NicAdvancedProperty("Flow Control"),
            "Запрещает сетевой карте отправлять паузы партнёру при заполнении буфера.",
            "Иногда убирает «залипания» на домашних роутерах, которые неправильно обрабатывают кадры паузы. " +
            "На коммутаторах провайдера отключение пауз может увеличивать потери — меняйте осознанно.",
            "По умолчанию «Rx & Tx Enabled».",
            "Disabled", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: -1, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false),

        new(
            "NIC_RECEIVE_BUFFERS", "Буферы приёма по максимуму драйвера", "Receive Buffers",
            TweakCategory.NicDriver, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.NicAdvancedProperty("Receive Buffers"),
            "Ставит максимально допустимый драйвером размер буфера приёма.",
            "Помогает на гигабитных тарифах и при пиковых всплесках трафика в больших замесах: пакеты не теряются " +
            "на переполнении буфера. Значение выбирает не программа, а сам драйвер — программа берёт максимум из его списка.",
            "По умолчанию 512 (зависит от драйвера).",
            string.Empty, TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 1, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: true, IsComputedTarget: true),

        new(
            "NIC_TRANSMIT_BUFFERS", "Буферы передачи по максимуму драйвера", "Transmit Buffers",
            TweakCategory.NicDriver, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.NicAdvancedProperty("Transmit Buffers"),
            "Ставит максимально допустимый драйвером размер буфера передачи.",
            "Полезно при стриминге и большой отдаче. Значение берётся из списка драйвера, а не выдумывается.",
            "По умолчанию 128 (зависит от драйвера).",
            string.Empty, TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 1, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: true, IsComputedTarget: true),

        // ─────────────── Глобальные параметры TCP (netsh) ───────────────
        new(
            "TCP_RSS_ENABLED", "Включить RSS (масштабирование приёма)", "Receive Side Scaling",
            TweakCategory.TcpIpStack, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.NetshTcpGlobal("rss"),
            "Распределяет обработку входящего трафика между ядрами процессора.",
            "Стоит применять на многоядерном процессоре: один поток перестаёт быть узким местом при высокой скорости. " +
            "На задержку в матче почти не влияет, поэтому это твик «для скорости», а не «для пинга».",
            "Обычно включено по умолчанию.",
            "enabled", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 1, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: true),

        new(
            "TCP_AUTOTUNING_NORMAL", "Вернуть автонастройку окна TCP в обычный режим", "Receive Window Auto-Tuning Level",
            TweakCategory.TcpIpStack, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.NetshTcpGlobal("autotuninglevel"),
            "Возвращает автоматический подбор размера окна приёма вместо урезанного режима.",
            "Полезно, если раньше ставили «ограниченный» режим по чужому совету: на современном интернете это чаще режет скорость. " +
            "Для игр значение нейтрально, для загрузок — плюс.",
            "По умолчанию «normal».",
            "normal", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 1, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: true),

        new(
            "TCP_ECN_DISABLED", "Отключить ECN (уведомление о перегрузке)", "ECN Capability",
            TweakCategory.TcpIpStack, TweakRiskLevel.Medium, GameRecommendationBadge.Caution,
            new TweakValueSource.NetshTcpGlobal("ecncapability"),
            "Запрещает стек помечать исходящие пакеты флагом явного уведомления о перегрузке (ECN).",
            "Применять не рекомендую по умолчанию: часть домашних роутеров и провайдерских путей некорректно обрабатывает ECN " +
            "и теряет такие пакеты. Отключение помогает как лечение конкретной проблемы, а не как «оптимизация вообще».",
            "В новых версиях Windows ECN включён по умолчанию.",
            "disabled", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false),

        // ─────────────── Вычисляемые значения ───────────────
        new(
            "MTU_SUBINTERFACE_OPTIMAL", "Оптимальный MTU интерфейса", "Optimal MTU",
            TweakCategory.TcpIpStack, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.NetshSubinterfaceMtu(),
            "Подбирает MTU пробами с запретом фрагментации и записывает найденное значение для сетевого подключения.",
            "Стоит применять, если провайдер использует PPPoE или туннель: при завышенном MTU часть пакетов дробится или гибнет, " +
            "что выглядит как «пинг скачет» и обрывы в матче. Значение вычисляется замерами до игрового сервера, а не берётся из интернета.",
            "MTU задаёт провайдер; программа лишь приводит интерфейс в соответствие с реально проходящим размером пакета.",
            string.Empty, TweakValueDataType.DWord, PingImpactDirection: -1, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false, IsComputedTarget: true),

        new(
            "DNS_SERVERS_FASTEST", "Самые быстрые DNS-серверы", "Fastest DNS",
            TweakCategory.DnsAndResolution, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            new TweakValueSource.NetshDnsServers(),
            "Ставит DNS-серверы, которые быстрее всех ответили в честном бенчмарке с проверкой на подмену ответов.",
            "Влияет на поиск матча, вход в лобби и загрузку списка серверов, а НЕ на пинг внутри матча. " +
            "Если раньше адреса приходили от роутера или провайдера, откат вернёт автоматическое получение.",
            "Адреса подбираются бенчмарком; программа не навязывает «модные» публичные DNS.",
            string.Empty, TweakValueDataType.MultiString, PingImpactDirection: 0, SpeedImpactDirection: 1, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false, IsComputedTarget: true),

        // ─────────────── Задержка ввода: мышь, клавиатура, отрисовка кадра ───────────────
        // Это НЕ сетевые правки: они убирают задержку между движением руки и кадром.
        // Все три параметра ускорения мыши обязаны идти вместе — иначе акселерация останется частично.

        new(
            "MOUSE_ACCEL_SPEED", "Ускорение мыши (MouseSpeed)", "Mouse acceleration",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, MouseControlPanel, "MouseSpeed", TweakValueDataType.String),
            "Выключает системное ускорение указателя: движение мыши начинает 1:1 совпадать с движением руки.",
            "Для игр это критично: с ускорением привыкание к прицелу невозможно, а ощущение «мышь живёт своей жизнью» — " +
            "это и есть акселерация. Относится к настройке «Повышение точности указателя».",
            "По умолчанию Windows включает ускорение (MouseSpeed = 1).",
            "0", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "MOUSE_ACCEL_THRESHOLD1", "Порог ускорения мыши #1 (MouseThreshold1)", "Mouse threshold 1",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, MouseControlPanel, "MouseThreshold1", TweakValueDataType.String),
            "Обнуляет первый порог ускорения: до него указатель движется линейно, без «разгона».",
            "Вместе с MouseSpeed и вторым порогом полностью убирает нелинейность прицела — её замечают все, кто играет в шутеры.",
            "По умолчанию порог равен 6 условным единицам.",
            "0", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "MOUSE_ACCEL_THRESHOLD2", "Порог ускорения мыши #2 (MouseThreshold2)", "Mouse threshold 2",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, MouseControlPanel, "MouseThreshold2", TweakValueDataType.String),
            "Обнуляет второй порог ускорения: выше него Windows раньше добавляла ещё больше скорости указателю.",
            "Третий и последний параметр акселерации. Пока он не равен нулю, «Повышение точности указателя» " +
            "остаётся включённым, даже если первый порог обнулён.",
            "По умолчанию порог равен 10 условным единицам.",
            "0", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "MOUSE_SENSITIVITY_NEUTRAL", "Нейтральная скорость указателя 1:1 (6/11)", "Neutral pointer speed",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, MouseControlPanel, "MouseSensitivity", TweakValueDataType.String),
            "Ставит системную скорость указателя в нейтральное положение: Windows не домножает движение мыши.",
            "Любое значение кроме нейтрального — это системное масштабирование поверх чувствительности в игре. " +
            "Нейтральное положение принято за стандарт в киберспорте.",
            "По умолчанию Windows ставит 10 (нейтральное положение на шкале 1–20).",
            "10", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "KEYBOARD_FILTERKEYS_OFF", "Отключить FilterKeys (задержка клавиатуры)", "FilterKeys off",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, KeyboardResponse, "Flags", TweakValueDataType.String),
            "Выключает «фильтр ввода» Windows: система перестаёт игнорировать короткие и повторные нажатия.",
            "Если фильтр включён, часть нажатий пропадает, а срабатывания приходят с задержкой — для игр это худший вариант. " +
            "Обычно он выключен, но включается сам после случайного удержания правого Shift.",
            "Значение по умолчанию — включённый фильтр (Flags = 126 для выключенного состояния).",
            "126", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "KEYBOARD_REPEAT_DELAY_MIN", "Минимальная задержка повтора клавиш", "Keyboard repeat delay",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, KeyboardClass, "KeyboardDelay", TweakValueDataType.String),
            "Убирает паузу перед автоповтором клавиши.",
            "На игровой отклик не влияет: это поведение автоповтора при удержании. Полезно, если вы используете " +
            "клавиатуру для макросов или быстрого набора в чате матча.",
            "По умолчанию Windows ставит среднюю задержку (1).",
            "0", TweakValueDataType.String, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false),

        new(
            "GAME_DVR_OFF", "Отключить Game DVR (запись в фоне)", "Game DVR off",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, GameConfigStore, "GameDVR_Enabled", TweakValueDataType.DWord),
            "Выключает фоновую запись игр Xbox Game DVR, которая отбирает кадры и добавляет задержку отрисовки.",
            "Фоновая запись конкурирует с игрой за GPU: кадры приходят с опозданием, инпут ощущается «тяжёлым». " +
            "Сама Xbox Game Bar при этом не удаляется — её можно включить обратно в любой момент.",
            "По умолчанию фоновая запись включена в Windows 10/11.",
            "0", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "GAME_DVR_CAPTURE_OFF", "Отключить запись клипов (AppCaptureEnabled)", "App capture off",
            TweakCategory.InputLatency, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.CurrentUser, GameDvrStore, "AppCaptureEnabled", TweakValueDataType.DWord),
            "Выключает захват игровых клипов сочетанием клавиш Win+Alt+R — второй параметр той же фоновой записи.",
            "Пока этот параметр включён, Windows держит в памяти буфер последних секунд игры: это заметно " +
            "на слабых видеокартах и в моменты резких поворотов камеры.",
            "По умолчанию захват включён.",
            "0", TweakValueDataType.DWord, PingImpactDirection: 0, SpeedImpactDirection: 0, StabilityImpactDirection: 1,
            IncludedInLowPingPreset: true, IncludedInMaxSpeedPreset: false),

        new(
            "NIC_PACKET_PRIORITY_ON", "Приоритет пакетов на сетевой карте (802.1p/VLAN)", "Packet priority & VLAN",
            TweakCategory.NicDriver, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            new TweakValueSource.NicAdvancedProperty("Packet Priority & VLAN"),
            "Включает поддержку приоритета пакетов драйвером карты: помеченные пакеты игры уходят без общей очереди.",
            "Работает только если ваш роутер уважает приоритет (DSCP/802.1p) — иначе выигрыша не будет. " +
            "Драйверы по-разному называют это свойство, поэтому значение подставляется с проверкой поддержки.",
            "Часть драйверов по умолчанию держит свойство включённым, часть — выключенным.",
            "Packet Priority & VLAN Enabled", TweakValueDataType.CimString, PingImpactDirection: -1, SpeedImpactDirection: 0, StabilityImpactDirection: 0,
            IncludedInLowPingPreset: false, IncludedInMaxSpeedPreset: false, EffectUnconfirmedOnModernWindows: true)
    };

    private const string MouseControlPanel = @"Control Panel\Mouse";

    private const string KeyboardResponse = @"Control Panel\Accessibility\Keyboard Response";

    private const string KeyboardClass = @"Control Panel\Keyboard";

    private const string GameConfigStore = @"System\GameConfigStore";

    private const string GameDvrStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
}
