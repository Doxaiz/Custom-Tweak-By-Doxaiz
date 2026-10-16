using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;

namespace PingForge.SystemState;

/// <summary>
/// Чтение текущего состояния Windows и сети.
/// </summary>
/// <remarks>
/// Главное решение: настройки читаются через те же твики каталога, которыми они меняются. Так состояние
/// системы не может разойтись с тем, что программа применяет, проверяет и откатывает: и снапшот, и это
/// чтение ходят в одно и то же место (реестр, netsh, свойство NIC). Читать те же ключи вторым, «своим»
/// кодом — верный способ однажды показать пользователю одно, а изменить другое.
///
/// Второе решение: ничего не выдумывается. Если значение не выставлено, система ответила не тем, что мы
/// умеем разобрать, или чтение упало — поле остаётся «неизвестно» (null), а причина попадает в
/// <see cref="SystemStateReading.UnresolvedFields"/> на русском, чтобы интерфейс показал её серым текстом,
/// а не нулём и не «по умолчанию». Настройка «0 мс» и «не измерено» — разные вещи, и это правило здесь
/// соблюдается так же строго, как в замерах пинга.
/// </remarks>
public sealed class SystemStateReader : ISystemStateReader
{
    /// <summary>Код твика в каталоге (значение читается там же, где его пишет снапшот).</summary>
    public const string AutotuningTweakCode = "TCP_AUTOTUNING_NORMAL";
    public const string RssTweakCode = "TCP_RSS_ENABLED";
    public const string RscTweakCode = "TCP_RSC_DISABLED";
    public const string EcnTweakCode = "TCP_ECN_DISABLED";
    public const string MtuTweakCode = "MTU_SUBINTERFACE_OPTIMAL";
    public const string DnsTweakCode = "DNS_SERVERS_FASTEST";
    public const string TcpAckFrequencyTweakCode = "TCP_ACK_FREQUENCY";
    public const string TcpNoDelayTweakCode = "TCP_NO_DELAY";
    public const string GameModeTweakCode = "GAME_MODE_ON";

    private static readonly RegistryLocation WindowsProductName =
        Local(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");

    private static readonly RegistryLocation WindowsDisplayVersion =
        Local(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");

    private static readonly RegistryLocation WindowsBuild =
        Local(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");

    private static readonly RegistryLocation WindowsUpdateBuildRevision =
        Local(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");

    private static readonly RegistryLocation DeliveryOptimizationDownloadMode =
        Local(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode");

    private static readonly RegistryLocation TimerResolution =
        Local(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests");

    private static readonly RegistryLocation SystemRestoreDisabled =
        Local(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "DisableSR");

    private static readonly RegistryLocation ActivePowerScheme =
        Local(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme");

    /// <summary>
    /// Имена стандартных схем электропитания. По GUID из реестра имя получить больше негде:
    /// powercfg печатает его на языке системы, а подставлять «Сбалансированная» для чужой схемы нельзя.
    /// </summary>
    private static readonly Dictionary<string, string> KnownPowerSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Сбалансированная",
        ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "Высокая производительность",
        ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Экономия энергии",
        ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Максимальная производительность",
    };

    private readonly ITweakCatalog _catalog;
    private readonly IRegistryPort _registry;
    private readonly ICommandPort _command;
    private readonly INicPropertyPort _nic;
    private readonly IActiveAdapterResolver _adapter;

    public SystemStateReader(
        ITweakCatalog catalog,
        IRegistryPort registry,
        ICommandPort command,
        INicPropertyPort nic,
        IActiveAdapterResolver adapter)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _nic = nic ?? throw new ArgumentNullException(nameof(nic));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async Task<SystemStateReading> ReadAsync(CancellationToken ct = default)
    {
        var unresolved = new List<string>();

        (TweakTarget? adapterTarget, NicAdapterRef? activeAdapter) = await ReadAdapterAsync(unresolved, ct)
            .ConfigureAwait(false);

        // ── настройки, которые меняются твиками ────────────────────────────────────────────────────────
        string? autotuning = await ReadTweakValueAsync(AutotuningTweakCode, adapterTarget, unresolved, ct)
            .ConfigureAwait(false);

        string? rss = await ReadTweakValueAsync(RssTweakCode, adapterTarget, unresolved, ct).ConfigureAwait(false);
        string? rsc = await ReadTweakValueAsync(RscTweakCode, adapterTarget, unresolved, ct).ConfigureAwait(false);
        string? ecn = await ReadTweakValueAsync(EcnTweakCode, adapterTarget, unresolved, ct).ConfigureAwait(false);
        string? mtu = await ReadTweakValueAsync(MtuTweakCode, adapterTarget, unresolved, ct).ConfigureAwait(false);
        string? dns = await ReadTweakValueAsync(DnsTweakCode, adapterTarget, unresolved, ct).ConfigureAwait(false);
        string? gameMode = await ReadTweakValueAsync(GameModeTweakCode, adapterTarget, unresolved, ct).ConfigureAwait(false);

        string? ackFrequency = await ReadTweakValueAsync(TcpAckFrequencyTweakCode, adapterTarget, unresolved, ct)
            .ConfigureAwait(false);
        string? noDelay = await ReadTweakValueAsync(TcpNoDelayTweakCode, adapterTarget, unresolved, ct)
            .ConfigureAwait(false);

        // ── то, чего в каталоге нет: читаем прямо из системы ───────────────────────────────────────────
        string? edition = await ReadWindowsEditionAsync(unresolved, ct).ConfigureAwait(false);
        bool? isElevated = await ReadElevationAsync(unresolved, ct).ConfigureAwait(false);
        string? congestion = await ReadCongestionProviderAsync(unresolved, ct).ConfigureAwait(false);
        string? powerSchemeGuid = await ReadTextAsync(ActivePowerScheme, "Схема электропитания", unresolved, ct)
            .ConfigureAwait(false);
        int? linkSpeed = await ReadLinkSpeedAsync(activeAdapter, unresolved, ct).ConfigureAwait(false);
        IReadOnlyList<string> ndisFilters = await ReadNdisFiltersAsync(activeAdapter, unresolved, ct)
            .ConfigureAwait(false);

        string? deliveryMode = await ReadTextAsync(
            DeliveryOptimizationDownloadMode, "Оптимизация доставки (обмен между компьютерами)", unresolved, ct)
            .ConfigureAwait(false);

        string? timerResolution = await ReadTextAsync(
            TimerResolution, "Повышенное разрешение таймера", unresolved, ct).ConfigureAwait(false);

        string? restoreDisabled = await ReadTextAsync(
            SystemRestoreDisabled, "Восстановление системы", unresolved, ct).ConfigureAwait(false);

        return new SystemStateReading(
            WindowsEditionAndBuild: edition ?? "Версия Windows не прочитана.",
            IsRunningAsAdministrator: isElevated ?? false,
            ActiveAdapter: activeAdapter,
            LinkSpeedMbps: linkSpeed,
            CurrentMtuBytes: SystemStateParsing.TryParseInt(mtu),
            PowerSchemeGuid: powerSchemeGuid,
            PowerSchemeName: ResolvePowerSchemeName(powerSchemeGuid, unresolved),
            TcpAutoTuningLevel: autotuning,
            CongestionProvider: congestion,
            IsRssEnabled: SystemStateParsing.TryParseFlag(rss),
            IsRscEnabled: SystemStateParsing.TryParseFlag(rsc),
            IsEcnEnabled: SystemStateParsing.TryParseFlag(ecn),
            IsNagleDisabledOnActiveNic: SystemStateParsing.CombineNagleFlags(ackFrequency, noDelay),
            IsGameModeEnabled: SystemStateParsing.TryParseFlag(gameMode),
            IsDeliveryOptimizationP2pEnabled: SystemStateParsing.ParseDeliveryOptimizationP2p(deliveryMode),
            IsSystemRestoreEnabledOnSystemDrive: SystemStateParsing.ParseSystemRestoreEnabled(restoreDisabled),
            IsTimerResolutionRaised: SystemStateParsing.ParseTimerResolutionRaised(timerResolution),
            ConfiguredDnsServers: NetshOutputParser.SplitServers(dns),
            AttachedNdisFilters: ndisFilters,
            UnresolvedFields: unresolved);
    }

    /// <summary>
    /// Активный адаптер: сначала кто им признан, потом — его паспорт (имя, «физический», «поднят»).
    /// Паспорт нужен интерфейсу, а чтение настроек работает и без него, поэтому отсутствие строки
    /// в списке адаптеров не мешает прочитать MTU: об этом честно пишем и продолжаем.
    /// </summary>
    private async Task<(TweakTarget? Target, NicAdapterRef? Adapter)> ReadAdapterAsync(
        List<string> unresolved, CancellationToken ct)
    {
        TweakTarget? target = null;

        try
        {
            target = await _adapter.ResolveActiveAdapterAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"Активный адаптер не определён ({ex.GetType().Name}): настройки адаптера не читаем.");
        }

        if (target is null || string.IsNullOrWhiteSpace(target.AdapterInterfaceGuid))
        {
            unresolved.Add("Активный адаптер не определён: настройки адаптера (MTU, RSS, DNS, Nagle) не читаем.");
            return (null, null);
        }

        try
        {
            IReadOnlyList<NicAdapterRef> adapters = await _nic.ListAdaptersAsync(ct).ConfigureAwait(false);
            NicAdapterRef? match = adapters.FirstOrDefault(a =>
                string.Equals(a.InterfaceGuid, target.AdapterInterfaceGuid, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                unresolved.Add("Активного адаптера нет в списке адаптеров системы: паспорт адаптера не читаем.");
            }

            return (target, match);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"Список адаптеров не прочитан ({ex.GetType().Name}).");
            return (target, null);
        }
    }

    /// <summary>Значение настройки — через твик каталога, то есть ровно там, где его пишет снапшот.</summary>
    private async Task<string?> ReadTweakValueAsync(
        string tweakCode, TweakTarget? target, List<string> unresolved, CancellationToken ct)
    {
        ITweak? tweak = _catalog.GetByCode(tweakCode);
        if (tweak is null)
        {
            unresolved.Add($"В каталоге нет твика «{tweakCode}»: значение по нему не читаем, чтобы не выдумывать.");
            return null;
        }

        string title = tweak.Descriptor.TitleRu;

        try
        {
            CapturedParameterState captured = await tweak
                .CaptureAsync(target ?? TweakTarget.Global(title), ct)
                .ConfigureAwait(false);

            if (!captured.IsSupported)
            {
                unresolved.Add($"{title}: прочитать не удалось — {captured.SupportReasonRu}");
                return null;
            }

            if (captured.IsUnparsableOutput)
            {
                unresolved.Add($"{title}: система ответила не тем, что мы умеем разобрать ({captured.CapturedRawValue}).");
                return null;
            }

            if (!captured.ExistedBefore)
            {
                unresolved.Add($"{title}: значение сейчас не выставлено — работают настройки по умолчанию, " +
                               "и выдавать их за прочитанное мы не будем.");
                return null;
            }

            return captured.CapturedRawValue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"{title}: чтение упало ({ex.GetType().Name}) — значение считается неизвестным.");
            return null;
        }
    }

    /// <summary>
    /// Версия Windows: четыре значения из одного ключа реестра. Обновление (UBR) добавляется к сборке,
    /// потому что именно по нему видно, есть ли свежие исправления сетевого стека.
    /// </summary>
    private async Task<string?> ReadWindowsEditionAsync(List<string> unresolved, CancellationToken ct)
    {
        string? product = await ReadTextAsync(WindowsProductName, "Название Windows", unresolved, ct).ConfigureAwait(false);
        string? version = await ReadTextAsync(WindowsDisplayVersion, "Версия Windows (24H2 и подобное)", unresolved, ct)
            .ConfigureAwait(false);

        string? build = await ReadTextAsync(WindowsBuild, "Номер сборки Windows", unresolved, ct).ConfigureAwait(false);
        string? ubr = await ReadTextAsync(WindowsUpdateBuildRevision, "Номер обновления сборки", unresolved, ct)
            .ConfigureAwait(false);

        return SystemStateParsing.DescribeWindows(product, version, build, ubr);
    }

    /// <summary>
    /// Права администратора проверяются по уровню обязательного контроля доступа в ответе <c>whoami /groups</c>.
    /// Так проверка не зависит от языка системы: идентификаторы SID одинаковы везде, а подписи у них разные.
    /// «Высокий» (12288), «Система» (16384) и «Защищённый процесс» (20480) означают, что менять систему можно;
    /// «Средний» (8192) — обычный пользователь.
    /// </summary>
    private async Task<bool?> ReadElevationAsync(List<string> unresolved, CancellationToken ct)
    {
        try
        {
            CommandResult result = await _command
                .RunAsync("whoami", new[] { "/groups" }, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                unresolved.Add($"Права администратора: whoami ответил кодом {result.ExitCode} — считаем неизвестными.");
                return null;
            }

            string output = result.StandardOutput + "\n" + result.StandardError;

            if (output.Contains("S-1-16-12288", StringComparison.Ordinal) ||
                output.Contains("S-1-16-16384", StringComparison.Ordinal) ||
                output.Contains("S-1-16-20480", StringComparison.Ordinal))
            {
                return true;
            }

            if (output.Contains("S-1-16-8192", StringComparison.Ordinal) ||
                output.Contains("S-1-16-4096", StringComparison.Ordinal))
            {
                return false;
            }

            unresolved.Add("Права администратора: в ответе whoami нет уровня обязательного контроля — не выдумываем.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"Права администратора: проверить не удалось ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>
    /// Провайдер управления перегрузкой (CUBIC, CTCP, NewReno). Отдельного твика для него в каталоге нет,
    /// поэтому читаем ту же команду, что и остальные netsh-настройки, и разбираем её тем же разборщиком:
    /// подписи система печатает на своём языке, а значения — английскими словами.
    /// </summary>
    private async Task<string?> ReadCongestionProviderAsync(List<string> unresolved, CancellationToken ct)
    {
        try
        {
            CommandResult result = await _command
                .RunAsync("netsh", new[] { "int", "tcp", "show", "global" }, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                unresolved.Add($"Провайдер управления перегрузкой: netsh ответил кодом {result.ExitCode}.");
                return null;
            }

            IReadOnlyDictionary<string, string> values = NetshOutputParser.ParseTcpGlobal(result.StandardOutput);
            if (!values.TryGetValue("dca", out string? provider) || string.IsNullOrWhiteSpace(provider))
            {
                unresolved.Add("Провайдер управления перегрузкой: в ответе netsh нет этой строки — не выдумываем.");
                return null;
            }

            return NetshOutputParser.CanonicalizeToken(provider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"Провайдер управления перегрузкой: чтение упало ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Скорость соединения. Её отдаёт только сам адаптер: в свойствах NIC её нет.</summary>
    private async Task<int?> ReadLinkSpeedAsync(NicAdapterRef? adapter, List<string> unresolved, CancellationToken ct)
    {
        if (adapter is null)
        {
            unresolved.Add("Скорость соединения: активный адаптер не определён — читать нечего.");
            return null;
        }

        string? raw = await RunPowerShellAsync(
            $"Get-NetAdapter -Name '{adapter.DisplayName}' | Select-Object -ExpandProperty LinkSpeed",
            "Скорость соединения",
            unresolved,
            ct).ConfigureAwait(false);

        int? speed = SystemStateParsing.ParseLinkSpeedMbps(raw);
        if (speed is null && raw is not null)
        {
            unresolved.Add($"Скорость соединения: «{raw.Trim()}» разобрать не удалось.");
        }

        return speed;
    }

    /// <summary>
    /// Фильтры NDIS, навешанные на адаптер (антивирус, QoS, виртуальные коммутаторы). Именно они чаще всего
    /// «удлиняют» путь пакета, поэтому список нужен для анализа помех, а не для галочки.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadNdisFiltersAsync(
        NicAdapterRef? adapter, List<string> unresolved, CancellationToken ct)
    {
        if (adapter is null)
        {
            unresolved.Add("Фильтры NDIS: активный адаптер не определён — читать нечего.");
            return Array.Empty<string>();
        }

        string? raw = await RunPowerShellAsync(
            $"Get-NetAdapterBinding -Name '{adapter.DisplayName}' | " +
            "Where-Object { $_.Enabled } | Select-Object -ExpandProperty ComponentID",
            "Фильтры NDIS",
            unresolved,
            ct).ConfigureAwait(false);

        if (raw is null)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<string> filters = SystemStateParsing.MeaningfulLines(raw);
        if (filters.Count == 0)
        {
            unresolved.Add("Фильтры NDIS: система вернула пустой список — возможно, командлет недоступен.");
        }

        return filters;
    }

    private async Task<string?> RunPowerShellAsync(
        string script, string whatRu, List<string> unresolved, CancellationToken ct)
    {
        try
        {
            CommandResult result = await _command
                .RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, ct)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                unresolved.Add($"{whatRu}: powershell ответил кодом {result.ExitCode} — значение неизвестно.");
                return null;
            }

            return result.StandardOutput;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"{whatRu}: чтение упало ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Чтение значения реестра с честной причиной, если прочитать не удалось.</summary>
    private async Task<string?> ReadTextAsync(
        RegistryLocation location, string whatRu, List<string> unresolved, CancellationToken ct)
    {
        try
        {
            RegistryValueSnapshot value = await _registry.ReadAsync(location, ct).ConfigureAwait(false);

            if (!value.Exists)
            {
                unresolved.Add($"{whatRu}: значение не выставлено — работают настройки по умолчанию.");
                return null;
            }

            if (value.IsUnparsable)
            {
                unresolved.Add($"{whatRu}: прочитано «{value.UnparsableRaw}» — свести к числу не удалось.");
                return null;
            }

            return value.CanonicalValue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unresolved.Add($"{whatRu}: чтение упало ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Имя схемы электропитания по её GUID. Неизвестный GUID честно остаётся без имени.</summary>
    private static string? ResolvePowerSchemeName(string? guid, List<string> unresolved)
    {
        if (guid is null)
        {
            return null;
        }

        if (KnownPowerSchemes.TryGetValue(guid, out string? name))
        {
            return name;
        }

        unresolved.Add($"Схема электропитания: GUID {guid} не из стандартных — имя схемы не выдумываем.");
        return null;
    }

    private static RegistryLocation Local(string subKeyPath, string valueName) =>
        new(RegistryRootKind.LocalMachine, subKeyPath, valueName);
}
