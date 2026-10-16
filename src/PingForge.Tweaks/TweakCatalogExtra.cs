using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks;

/// <summary>
/// Второй блок каталога: сеть для пинга, питание и фон для кадров, отзывчивость системы.
///
/// Здесь же лежат твики с честным вердиктом «не советуем»: показывать их нужно, потому что именно
/// их советуют в случайных подборках «ускорителей Windows», а разница между «можно» и «не надо»
/// нигде не объясняется. У каждого — причина отказа, а не просто серый цвет.
///
/// Форма записи компактнее основного каталога: те же поля, но через маленькие помощники,
/// чтобы таблицу было читаемо просматривать глазами.
/// </summary>
internal static class TweakCatalogExtra
{
    private const string TcpInterfaceParams =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + RegistryValueAccessor.AdapterGuidToken;

    private const string NetBtInterfaceParams =
        @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\Tcpip_" + RegistryValueAccessor.AdapterGuidToken;

    private const string PriorityControl = @"SYSTEM\CurrentControlSet\Control\PriorityControl";

    private const string GameConfigStore = @"System\GameConfigStore";

    private const string GameBarStore = @"SOFTWARE\Microsoft\GameBar";

    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private const string PowerThrottling = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";

    private const string ActivePowerScheme = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    private const string Personalize = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string PrefetchParameters = @"SYSTEM\CurrentControlSet\Control\Session Manager\PrefetchParameters";

    private const string Desktop = @"Control Panel\Desktop";

    private const string MouseStore = @"Control Panel\Mouse";

    /// <summary>High performance — штатная схема питания Windows (существует на всех редакциях).</summary>
    private const string HighPerformanceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static IReadOnlyList<TweakDefinition> All { get; } = new TweakDefinition[]
    {
        // ─────────────── Сеть: то, что влияет на пинг и потери ───────────────
        Reg(
            "TCP_DELAYED_ACK_TICKS", "Таймер задержанных подтверждений (TcpDelAckTicks)", "TCP delayed ACK ticks",
            TweakCategory.TcpIpStack, TweakFocus.Ping, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            RegistryRootKind.LocalMachine, TcpInterfaceParams, "TcpDelAckTicks", TweakValueDataType.DWord,
            "Убирает паузу ожидания перед подтверждением полученных данных: подтверждение уходит сразу.",
            "Работает в паре с частотой подтверждений: пока подтверждение ждёт таймер, отправитель не увеличивает окно. " +
            "Для игр с мелкими пакетами это лишние миллисекунды на ровном месте.",
            "По умолчанию параметр отсутствует, таймер работает по стандарту (2 тика).",
            "0", ping: -1, speed: 0, stability: 0, lowPing: true),

        Reg(
            "NETBT_OVER_TCPIP_OFF", "Отключить NetBIOS поверх TCP/IP", "NetBIOS over TCP/IP off",
            TweakCategory.TcpIpStack, TweakFocus.System, TweakRiskLevel.Medium, GameRecommendationBadge.Caution,
            RegistryRootKind.LocalMachine, NetBtInterfaceParams, "NetbiosOptions", TweakValueDataType.DWord,
            "Прекращает широковещательные рассылки NetBIOS от этого адаптера.",
            "Шум NetBIOS ничего не даёт современной игре, но занимает эфир в домашней сети — заметнее всего по Wi-Fi. " +
            "Цена: перестанут находиться компьютеры и сетевые папки по «сетевым именам» (ресурсы по IP работают).",
            "По умолчанию NetBIOS разрешён DHCP-сервером провайдера или роутера.",
            "2", ping: 0, speed: 0, stability: 1),

        Netsh(
            "TCP_RSC_DISABLED", "Отключить объединение сегментов приёма (RSC)", "Receive Segment Coalescing off",
            TweakCategory.TcpIpStack, TweakFocus.Ping, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            "rsc",
            "Запрещает сетевой карте склеивать несколько входящих сегментов в один перед передачей системе.",
            "Склейка экономит процессор, но добавляет задержку до момента обработки: для игр с редкими мелкими пакетами " +
            "это заметнее, чем экономия. На загрузке больших файлов при этом теряется немного скорости.",
            "На большинстве карт по умолчанию включено.",
            "disabled", ping: -1, speed: -1, stability: 0, lowPing: true, unconfirmed: true),

        Nic(
            "NIC_POWER_SAVING_MODE_OFF", "Отключить режим энергосбережения карты", "Power saving mode off",
            TweakCategory.NicDriver, TweakFocus.Ping, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            "Power Saving Mode",
            "Запрещает сетевой карте уходить в экономию между пакетами.",
            "В экономии карта может «просыпаться» с задержкой: заметно на редком игровом трафике и в лобби. " +
            "Цена — на ноутбуке карта потребляет чуть больше от батареи.",
            "Зависит от драйвера: у Realtek включено чаще, у Intel — реже.",
            "Disabled", ping: -1, speed: 0, stability: 1),

        Nic(
            "NIC_GREEN_ETHERNET_OFF", "Отключить Green Ethernet", "Green Ethernet off",
            TweakCategory.NicDriver, TweakFocus.Ping, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            "Green Ethernet",
            "Отключает снижение мощности передатчика на коротком кабеле — самый частый источник «плавающего» пинга.",
            "Экономия электричества даёт рост задержки на несколько миллисекунд, который пользователь принимает " +
            "за проблемы провайдера. Это одна из самых полезных для игр правок в драйвере карты.",
            "Часто включено по умолчанию на картах Realtek/Atheros.",
            "Disabled", ping: -1, speed: 0, stability: 1),

        Nic(
            "NIC_GIGABIT_LITE_OFF", "Отключить Gigabit Lite", "Gigabit Lite off",
            TweakCategory.NicDriver, TweakFocus.Speed, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            "Gigabit Lite",
            "Убирает «облегчённый» режим гигабита, при котором карта снижает запас по мощности канала.",
            "Полезно, если скорость упирается в карту, а не в тариф. На коротком кабеле разницы в скорости обычно нет, " +
            "но и вреда от отключения нет.",
            "Встречается у Realtek: по умолчанию обычно включено.",
            "Disabled", ping: 0, speed: 1, stability: 0),

        Reg(
            "DNS_CACHE_TTL_NEGATIVE", "Короткий кэш неудачных DNS-ответов (MaxNegativeCacheTtl)", "Negative DNS cache TTL",
            TweakCategory.DnsAndResolution, TweakFocus.Ping, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            RegistryRootKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl", TweakValueDataType.DWord,
            "Сокращает время, в течение которого Windows помнит неудачный ответ DNS-сервера.",
            "Если сервер игры сменил адрес, а система помнит старый отказ, вход в лобби «залипает» до перезагрузки. " +
            "Пяти секунд достаточно, чтобы переспросить DNS и не мешать нормальной работе.",
            "По умолчанию 5 секунд — параметр чаще всего не задан.",
            "5", ping: 0, speed: 0, stability: 1),

        // ─────────────── Питание и кадры ───────────────
        Reg(
            "POWER_SCHEME_HIGH_PERFORMANCE", "Схема питания «Высокая производительность»", "High performance power plan",
            TweakCategory.PowerAndScheduler, TweakFocus.Fps, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            RegistryRootKind.LocalMachine, ActivePowerScheme, "ActivePowerScheme", TweakValueDataType.String,
            "Переключает активную схему питания на «Высокую производительность».",
            "Схема не даёт процессору уходить в глубокие состояния энергосбережения: отклик и ровность кадра выше, " +
            "особенно на ноутбуках. Цена — температура, шум вентиляторов и расход от батареи. " +
            "Экономичные схемы отменяются целиком, поэтому твик помечен «по желанию» и не входит в пресеты автоматически.",
            "По умолчанию Windows использует «Сбалансированную» схему.",
            HighPerformanceSchemeGuid, ping: 0, speed: 0, stability: 1),

        Reg(
            "POWER_THROTTLING_OFF", "Отключить троттлинг фоновых процессов (PowerThrottlingOff)", "Power throttling off",
            TweakCategory.PowerAndScheduler, TweakFocus.Fps, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            RegistryRootKind.LocalMachine, PowerThrottling, "PowerThrottlingOff", TweakValueDataType.DWord,
            "Запрещает Windows снижать частоту процессов, которые система считает фоновыми.",
            "Помогает, когда игра и её лончер считаются «фоном» и получают меньше ресурсов. " +
            "Цена — выше расход энергии; на ноутбуке от батареи будет греться сильнее.",
            "По умолчанию троттлинг включён.",
            "1", ping: 0, speed: 0, stability: 1),

        Reg(
            "PRIORITY_SEPARATION_26", "Кванты планировщика для игр (Win32PrioritySeparation=38)", "Win32 priority separation 38",
            TweakCategory.PowerAndScheduler, TweakFocus.Fps, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            RegistryRootKind.LocalMachine, PriorityControl, "Win32PrioritySeparation", TweakValueDataType.DWord,
            "Задаёт 38 — режим, при котором активное окно получает длинные кванты времени, а фоновые задачи — короткие.",
            "Классическое значение для игр: кадры ровнее, фоновые обновления и индексация меньше мешают. " +
            "Не путать с «приоритетом реального времени»: этот параметр не даёт играм обгонять системные потоки.",
            "По умолчанию 2 (Windows сама выбирает пропорцию).",
            "38", ping: 0, speed: 0, stability: 1, lowPing: true),

        Reg(
            "GPU_HW_SCHEDULING_ON", "Аппаратное планирование GPU (HAGS)", "Hardware GPU scheduling on",
            TweakCategory.WindowsBackground, TweakFocus.Fps, TweakRiskLevel.Medium, GameRecommendationBadge.Optional,
            RegistryRootKind.LocalMachine, GraphicsDrivers, "HwSchMode", TweakValueDataType.DWord,
            "Передаёт управление очередями видеокарты драйверу и железу вместо планировщика Windows.",
            "На части систем снижает задержку вывода кадра и уменьшает статтеры; на части — не даёт ничего, " +
            "а иногда ухудшает работу старых драйверов. Поэтому «по желанию» и с обязательной перезагрузкой.",
            "По умолчанию Windows 10 — выключено, Windows 11 — включено.",
            "2", ping: 0, speed: 0, stability: 1, reboot: true, unconfirmed: true),

        Reg(
            "FULLSCREEN_OPTIMIZATION_OFF", "Отключить оптимизации полноэкранного режима", "Fullscreen optimizations off",
            TweakCategory.WindowsBackground, TweakFocus.Fps, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            RegistryRootKind.CurrentUser, GameConfigStore, "GameDVR_FSEBehaviorMode", TweakValueDataType.DWord,
            "Заставляет игры использовать настоящее монопольное полное окно вместо псевдо-полноэкранного.",
            "Псевдо-полноэкранный режим удобен для alt-tab, но добавляет проход через композитор: это лишний кадр задержки " +
            "и источник микрофризов. Монопольный режим отдаёт кадр напрямую в вывод.",
            "По умолчанию псевдо-полноэкранный режим включён.",
            "2", ping: 0, speed: 0, stability: 1),

        Reg(
            "GAME_MODE_ON", "Включить игровой режим Windows", "Game Mode on",
            TweakCategory.WindowsBackground, TweakFocus.Fps, TweakRiskLevel.Low, GameRecommendationBadge.Recommended,
            RegistryRootKind.CurrentUser, GameBarStore, "AutoGameModeEnabled", TweakValueDataType.DWord,
            "Включает автоматический игровой режим: Windows приостанавливает часть фоновых задач во время игры.",
            "Работает в связке с планировщиком: обновления, индексация и телеметрия получают меньше времени. " +
            "Не заменяет остальные твики, но вреда не несёт.",
            "По умолчанию включено в Windows 10/11.",
            "1", ping: 0, speed: 0, stability: 1),

        Reg(
            "TRANSPARENCY_EFFECTS_OFF", "Отключить эффекты прозрачности", "Transparency effects off",
            TweakCategory.WindowsBackground, TweakFocus.Fps, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            RegistryRootKind.CurrentUser, Personalize, "EnableTransparency", TweakValueDataType.DWord,
            "Убирает размытие фона в меню и панелях Windows.",
            "Сам по себе эффект косметический, но на слабых видеокартах размытие тратит видеопамять и время GPU " +
            "в моменты, когда игра отдаёт кадр через композитор. Выигрыш невелик, вреда нет.",
            "По умолчанию включено.",
            "0", ping: 0, speed: 0, stability: 1),

        // ─────────────── Отзывчивость интерфейса и ввода ───────────────
        Reg(
            "MENU_SHOW_DELAY_ZERO", "Мгновенные меню (MenuShowDelay)", "Instant menus",
            TweakCategory.InputLatency, TweakFocus.Input, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            RegistryRootKind.CurrentUser, Desktop, "MenuShowDelay", TweakValueDataType.String,
            "Убирает задержку перед раскрытием меню проводника и системных меню.",
            "Играм это не нужно, но задержка меню — это тоже задержка между нажатием и реакцией, и её замечают " +
            "как «система тормозит». Значение 0 убирает паузу полностью.",
            "По умолчанию 400 мс.",
            "0", ping: 0, speed: 0, stability: 0),

        Reg(
            "MOUSE_HOVER_TIME_MIN", "Минимальная задержка наведения (MouseHoverTime)", "Mouse hover time",
            TweakCategory.InputLatency, TweakFocus.Input, TweakRiskLevel.Low, GameRecommendationBadge.Optional,
            RegistryRootKind.CurrentUser, MouseStore, "MouseHoverTime", TweakValueDataType.String,
            "Сокращает время, которое Windows ждёт наведения курсора перед показом подсказки.",
            "Полезно в интерфейсах и лаунчерах: 10 мс вместо 400 мс. На саму игру не влияет, на «ощущение системы» — да.",
            "По умолчанию 400 мс.",
            "10", ping: 0, speed: 0, stability: 0),

        // ─────────────── Твики, которые НЕ надо включать (и почему) ───────────────
        Reg(
            "PREFETCH_DISABLED_NOT_RECOMMENDED", "Отключить Prefetch (не советуем)", "Prefetcher off",
            TweakCategory.WindowsBackground, TweakFocus.Fps, TweakRiskLevel.High, GameRecommendationBadge.NotRecommended,
            RegistryRootKind.LocalMachine, PrefetchParameters, "EnablePrefetcher", TweakValueDataType.DWord,
            "Запрещает Windows заранее читать файлы часто запускаемых программ.",
            "Этот совет ходит по гайдам «ускорения Windows», но на современных накопителях он только вредит: " +
            "игра и её карты грузятся дольше, подгрузки тексту в открытом мире чаще. Ни пинга, ни кадров это не улучшает — " +
            "поэтому твик есть в каталоге с вердиктом «не советуем», а не спрятан.",
            "По умолчанию включено (значение 3 — и приложения, и загрузка системы).",
            "0", ping: 0, speed: -1, stability: -1),

        Reg(
            "SUPERFETCH_DISABLED_NOT_RECOMMENDED", "Отключить SysMain/Superfetch (не советуем)", "SysMain off",
            TweakCategory.WindowsBackground, TweakFocus.Fps, TweakRiskLevel.High, GameRecommendationBadge.NotRecommended,
            RegistryRootKind.LocalMachine, PrefetchParameters, "EnableSuperfetch", TweakValueDataType.DWord,
            "Выключает службу обслуживания памяти, которая подгружает нужное заранее.",
            "Вместе с Prefetch это любимый совет в подборках «отключи службы — станет быстрее». На практике " +
            "страдает загрузка локаций и открытый мир, а выигрыш если и есть, то только на очень медленном жёстком диске в паре " +
            "с малым объёмом памяти. Для игры у нас есть более полезные твики.",
            "По умолчанию включено.",
            "0", ping: 0, speed: -1, stability: -1),

        Nic(
            "NIC_JUMBO_FRAMES_NOT_RECOMMENDED", "Увеличить кадр до 9 КБ (Jumbo Frames, не советуем)", "Jumbo frames on",
            TweakCategory.NicDriver, TweakFocus.Speed, TweakRiskLevel.High, GameRecommendationBadge.NotRecommended,
            "Jumbo Packet",
            "Разрешает карте отправлять большие кадры (обычно 9014 байт вместо 1500).",
            "Помогает только внутри локальной сети, где все устройства поддерживают тот же размер. В интернете кадр " +
            "всё равно разобьётся, а потери и фрагментация при ошибке вырастут: игровой трафик наоборот станет рваным. " +
            "Есть в каталоге как предупреждение: этот совет часто дают не глядя.",
            "По умолчанию 1514 байт.",
            "9014 Bytes", ping: 0, speed: -1, stability: -1)
    };

    // ───────────────────────────── помощники записи ─────────────────────────────

    private static TweakDefinition Reg(
        string code, string titleRu, string titleEn, TweakCategory category, TweakFocus focus,
        TweakRiskLevel risk, GameRecommendationBadge badge, RegistryRootKind root, string subKey,
        string valueName, TweakValueDataType dataType, string whatRu, string whyRu, string defaultRu,
        string recommended, int ping, int speed, int stability,
        bool lowPing = false, bool maxSpeed = false, bool reboot = false, bool unconfirmed = false) =>
        new(code, titleRu, titleEn, category, risk, badge,
            new TweakValueSource.RegistryValue(root, subKey, valueName, dataType),
            whatRu, whyRu, defaultRu, recommended, dataType, ping, speed, stability, lowPing, maxSpeed, reboot, unconfirmed,
            Focus: focus);

    private static TweakDefinition Netsh(
        string code, string titleRu, string titleEn, TweakCategory category, TweakFocus focus,
        TweakRiskLevel risk, GameRecommendationBadge badge, string settingKey, string whatRu, string whyRu,
        string defaultRu, string recommended, int ping, int speed, int stability,
        bool lowPing = false, bool maxSpeed = false, bool reboot = false, bool unconfirmed = false) =>
        new(code, titleRu, titleEn, category, risk, badge,
            new TweakValueSource.NetshTcpGlobal(settingKey),
            whatRu, whyRu, defaultRu, recommended, TweakValueDataType.CommandState, ping, speed, stability,
            lowPing, maxSpeed, reboot, unconfirmed, Focus: focus);

    private static TweakDefinition Nic(
        string code, string titleRu, string titleEn, TweakCategory category, TweakFocus focus,
        TweakRiskLevel risk, GameRecommendationBadge badge, string keyword, string whatRu, string whyRu,
        string defaultRu, string recommended, int ping, int speed, int stability,
        bool lowPing = false, bool maxSpeed = false, bool reboot = false, bool unconfirmed = false) =>
        new(code, titleRu, titleEn, category, risk, badge,
            new TweakValueSource.NicAdvancedProperty(keyword),
            whatRu, whyRu, defaultRu, recommended, TweakValueDataType.CimString, ping, speed, stability,
            lowPing, maxSpeed, reboot, unconfirmed, Focus: focus);
}
