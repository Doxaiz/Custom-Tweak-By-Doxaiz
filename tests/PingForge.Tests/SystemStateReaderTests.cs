using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.SystemState;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Чтение состояния Windows и сети. Проверяется на двойниках портов, поэтому важно не «вызвали команду»,
/// а три вещи: настройки читаются ровно там, где их пишет снапшот; неизвестное остаётся неизвестным
/// (null и причина на русском), а не превращается в ноль; падение любого одного чтения не роняет всё.
/// </summary>
public sealed class SystemStateReaderTests
{
    private const string AdapterGuid = "{GUID-ADAPTER-A}";
    private const string AdapterName = "Ethernet";

    // ── вспомогательный стенд ───────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public FakeRegistryPort Registry { get; } = new();

        public FakeNetshEmulator Netsh { get; } = new();

        public FakeNicPropertyPort Nic { get; } = new();

        public FakeActiveAdapterResolver Adapter { get; } = new(AdapterGuid, AdapterName);

        public ScriptedCommandPort Command { get; }

        public TweakCatalog Catalog { get; }

        public Harness()
        {
            Command = new ScriptedCommandPort(Netsh);
            Catalog = new TweakCatalog(new TweakPlatformPorts(
                Registry: Registry,
                Command: Command,
                NicProperties: Nic,
                ActiveAdapter: Adapter));
        }

        public SystemStateReader Reader => new(Catalog, Registry, Command, Nic, Adapter);

        /// <summary>Набор настроек «как на обычной игровой машине» — то, что читается в happy-path тестах.</summary>
        public Harness SeedHealthySystem()
        {
            Adapter.ResolveActiveAdapterAsync().GetAwaiter().GetResult();
            Nic.SeedAdapter(AdapterGuid, AdapterName);

            Netsh.SeedInterface(AdapterName, 1492, "1.1.1.1", "8.8.8.8");
            Netsh.SeedTcpGlobal("receivesegmentcoalescing", "enabled");
            Netsh.SeedTcpGlobal("congestioncontrolprovider", "cubic");

            Seed(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "Windows 11 Pro");
            Seed(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "24H2");
            Seed(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "26100");
            Seed(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", "1742");
            Seed(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", "1");
            Seed(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", "1");
            Seed(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "DisableSR", "0");
            Seed(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme",
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

            Registry.Seed(new RegistryLocation(RegistryRootKind.CurrentUser,
                @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled"), "1");
            Command.Seed("whoami", "Обязательный уровень контроля доступа\\Высокий уровень  S-1-16-12288");
            Command.Seed("Get-NetAdapter -Name", "2.5 Gbps");
            Command.Seed("Get-NetAdapterBinding", "ms_server\nms_pacer\nnpc_av");
            return this;
        }

        public void Seed(string subKey, string valueName, string? value) =>
            Registry.Seed(Local(subKey, valueName), value);

        private static RegistryLocation Local(string subKey, string valueName) =>
            new(RegistryRootKind.LocalMachine, subKey, valueName);
    }

    /// <summary>
    /// Команды, которые читатель вызывает отдельно от netsh: whoami (права) и powershell (скорость, фильтры).
    /// netsh уходит в эмулятор — он же отвечает твикам каталога, поэтому состояние и снапшот читают одно место.
    /// </summary>
    private sealed class ScriptedCommandPort : ICommandPort
    {
        private readonly ICommandPort _netsh;
        private readonly List<(string Fragment, CommandResult Result)> _scripted = new();

        public ScriptedCommandPort(ICommandPort netsh) => _netsh = netsh;

        /// <summary>Поведение «powershell недоступен» — проверка честного сообщения об отказе.</summary>
        public bool ThrowOnPowerShell { get; set; }

        public IReadOnlyList<string> Invocations => _invocations;

        private readonly List<string> _invocations = new();

        public void Seed(string commandFragment, string stdout, int exitCode = 0) =>
            _scripted.Add((commandFragment, new CommandResult(exitCode, stdout, string.Empty)));

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            _invocations.Add($"{fileName} {string.Join(' ', arguments)}");

            if (fileName.StartsWith("netsh", StringComparison.OrdinalIgnoreCase))
            {
                return _netsh.RunAsync(fileName, arguments, ct);
            }

            if (ThrowOnPowerShell && fileName.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("powershell недоступен (так бывает в урезанных сборках Windows)");
            }

            string line = $"{fileName} {string.Join(' ', arguments)}";
            foreach ((string fragment, CommandResult result) in _scripted)
            {
                if (line.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(result);
                }
            }

            return Task.FromResult(CommandResult.Failed($"Тест не задал ответ для «{fileName}»."));
        }
    }

    // ── полное чтение ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Читает_полное_состояние_системы()
    {
        Harness harness = new Harness().SeedHealthySystem();

        SystemStateReading reading = await harness.Reader.ReadAsync();

        Assert.Equal("Windows 11 Pro · 24H2 · сборка 26100.1742", reading.WindowsEditionAndBuild);
        Assert.True(reading.IsRunningAsAdministrator);
        Assert.NotNull(reading.ActiveAdapter);
        Assert.Equal(AdapterGuid, reading.ActiveAdapter!.InterfaceGuid);
        Assert.Equal(AdapterName, reading.ActiveAdapter.DisplayName);
        Assert.True(reading.ActiveAdapter.IsUp);
        Assert.Equal(2500, reading.LinkSpeedMbps);
        Assert.Equal(1492, reading.CurrentMtuBytes);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", reading.PowerSchemeGuid);
        Assert.Equal("Высокая производительность", reading.PowerSchemeName);
        Assert.Equal("normal", reading.TcpAutoTuningLevel);
        Assert.Equal("cubic", reading.CongestionProvider);
        Assert.True(reading.IsRssEnabled);
        Assert.True(reading.IsRscEnabled);
        Assert.False(reading.IsEcnEnabled);
        Assert.True(reading.IsGameModeEnabled);
        Assert.True(reading.IsDeliveryOptimizationP2pEnabled);
        Assert.True(reading.IsSystemRestoreEnabledOnSystemDrive);
        Assert.True(reading.IsTimerResolutionRaised);
        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, reading.ConfiguredDnsServers);
        Assert.Equal(new[] { "ms_server", "ms_pacer", "npc_av" }, reading.AttachedNdisFilters);
    }

    [Fact]
    public async Task Читает_версию_Windows_без_номера_обновления()
    {
        Harness harness = new Harness().SeedHealthySystem();
        harness.Registry.Seed(Local(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR"), null);

        SystemStateReading reading = await harness.Reader.ReadAsync();

        Assert.Equal("Windows 11 Pro · 24H2 · сборка 26100", reading.WindowsEditionAndBuild);
    }

    // ── «неизвестно» не превращается в «ноль» ───────────────────────────────────────────────────────

    [Fact]
    public async Task Пустая_система_даёт_неизвестное_и_причины_а_не_нули()
    {
        Harness harness = new();   // ничего не задано: ни адаптера, ни реестра, ни netsh

        SystemStateReading reading = await harness.Reader.ReadAsync();

        Assert.Null(reading.ActiveAdapter);
        Assert.Null(reading.LinkSpeedMbps);
        Assert.Null(reading.CurrentMtuBytes);
        Assert.Null(reading.PowerSchemeGuid);
        Assert.Null(reading.PowerSchemeName);
        Assert.Null(reading.IsGameModeEnabled);
        Assert.Null(reading.IsTimerResolutionRaised);
        Assert.Null(reading.IsDeliveryOptimizationP2pEnabled);
        Assert.Null(reading.IsSystemRestoreEnabledOnSystemDrive);

        // netsh — исключение: он всегда отвечает значениями, поэтому уровень автонастройки прочитан.
        // Это не «выдумка», а реальный ответ утилиты; всё, чего утилита не сказала, осталось неизвестным.
        Assert.Equal("normal", reading.TcpAutoTuningLevel);
        Assert.Null(reading.CongestionProvider);
        Assert.False(reading.IsRunningAsAdministrator);   // прав не видно — значит «нет», но с причиной ниже
        Assert.Empty(reading.ConfiguredDnsServers);
        Assert.Empty(reading.AttachedNdisFilters);
        Assert.NotEmpty(reading.UnresolvedFields);
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("адаптера", StringComparison.Ordinal));
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("не выставлено", StringComparison.Ordinal));
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("Права администратора", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Непонятный_ответ_системы_попадает_в_причины()
    {
        Harness harness = new Harness().SeedHealthySystem();
        harness.Netsh.SeedTcpGlobal("receivewindowautotuninglevel", "кто-его-знает");
        harness.Registry.Seed(
            Local(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme"),
            "00000000-0000-0000-0000-000000000000");

        SystemStateReading reading = await harness.Reader.ReadAsync();

        // Уровень автонастройки читается как есть: netsh — источник правды, подменять его «normal» нельзя.
        Assert.Equal("кто-его-знает", reading.TcpAutoTuningLevel);
        Assert.Null(reading.PowerSchemeName);
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("не из стандартных", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Права_проверяются_по_уровню_доступа_и_не_зависят_от_языка()
    {
        Harness elevated = new();
        elevated.Command.Seed("whoami", "Уровень  S-1-16-12288");
        Assert.True((await elevated.Reader.ReadAsync()).IsRunningAsAdministrator);

        Harness system = new();
        system.Command.Seed("whoami", "Уровень  S-1-16-16384");
        Assert.True((await system.Reader.ReadAsync()).IsRunningAsAdministrator);

        Harness usual = new();
        usual.Command.Seed("whoami", "Уровень  S-1-16-8192");
        Assert.False((await usual.Reader.ReadAsync()).IsRunningAsAdministrator);

        Harness unknown = new();
        unknown.Command.Seed("whoami", "вывод без уровня обязательного контроля");
        SystemStateReading reading = await unknown.Reader.ReadAsync();
        Assert.False(reading.IsRunningAsAdministrator);
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("Права администратора", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Падение_powershell_не_роняет_чтение()
    {
        Harness harness = new Harness().SeedHealthySystem();
        harness.Command.ThrowOnPowerShell = true;

        SystemStateReading reading = await harness.Reader.ReadAsync();

        Assert.Null(reading.LinkSpeedMbps);
        Assert.Empty(reading.AttachedNdisFilters);
        Assert.Equal(1492, reading.CurrentMtuBytes);       // netsh-часть прочитана
        Assert.Equal(2, reading.ConfiguredDnsServers.Count);
        Assert.True(reading.UnresolvedFields.Count(reason => reason.Contains("Скорость соединения", StringComparison.Ordinal)) == 1);
    }

    [Fact]
    public async Task Падение_netsh_оставляет_настройки_неизвестными()
    {
        Harness harness = new Harness().SeedHealthySystem();
        harness.Netsh.FailWhen = arguments => arguments.Contains("tcp")
            ? "Служба netsh недоступна."
            : null;

        SystemStateReading reading = await harness.Reader.ReadAsync();

        Assert.Null(reading.TcpAutoTuningLevel);
        Assert.Null(reading.CongestionProvider);
        Assert.Equal(1492, reading.CurrentMtuBytes);   // MTU читается другой командой и уцелел
        Assert.NotEmpty(reading.UnresolvedFields);
    }

    [Fact]
    public async Task Отсутствующий_в_каталоге_твик_честно_объявляется()
    {
        var registry = new FakeRegistryPort();
        var netsh = new FakeNetshEmulator();
        var command = new ScriptedCommandPort(netsh);
        var nic = new FakeNicPropertyPort();
        var adapter = new FakeActiveAdapterResolver(AdapterGuid, AdapterName);
        var emptyCatalog = new FakeTweakCatalog(Array.Empty<ITweak>());

        var reader = new SystemStateReader(emptyCatalog, registry, command, nic, adapter);
        SystemStateReading reading = await reader.ReadAsync();

        Assert.Null(reading.TcpAutoTuningLevel);
        Assert.Null(reading.IsRssEnabled);
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("TCP_RSS_ENABLED", StringComparison.Ordinal));
        Assert.Contains(reading.UnresolvedFields, reason => reason.Contains("чтобы не выдумывать", StringComparison.Ordinal));
    }

    // ── Nagle ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1", "1", true)]
    [InlineData("1", "0", false)]
    [InlineData("0", "1", false)]
    [InlineData("0", "0", false)]
    [InlineData(null, "1", null)]
    [InlineData("1", "нет", null)]
    public void Nagle_выключен_только_при_двух_единицах(string? ack, string? noDelay, bool? expected) =>
        Assert.Equal(expected, SystemStateParsing.CombineNagleFlags(ack, noDelay));

    [Fact]
    public async Task Nagle_читается_из_ключей_адаптера()
    {
        Harness harness = new Harness().SeedHealthySystem();
        string adapterKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + AdapterGuid;
        harness.Registry.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, adapterKey, "TcpAckFrequency"), "1");
        harness.Registry.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, adapterKey, "TCPNoDelay"), "1");

        SystemStateReading reading = await harness.Reader.ReadAsync();

        Assert.True(reading.IsNagleDisabledOnActiveNic);
    }

    [Fact]
    public async Task Выставленное_значение_против_отсутствующего_различаются()
    {
        // Одно значение выставлено, второго нет вовсе. Отсутствие — это «неизвестно», а не «ноль»:
        // именно поэтому вывод серый с причиной, а не зелёная галочка «Nagle выключен».
        Harness absent = new Harness().SeedHealthySystem();
        string adapterKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + AdapterGuid;
        absent.Registry.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, adapterKey, "TcpAckFrequency"), "1");

        SystemStateReading unknown = await absent.Reader.ReadAsync();

        Assert.Null(unknown.IsNagleDisabledOnActiveNic);
        Assert.Contains(unknown.UnresolvedFields, reason => reason.Contains("не выставлено", StringComparison.Ordinal));

        // А вот явный ноль во втором значении — это уже прочитанное состояние: Nagle включён.
        Harness explicitZero = new Harness().SeedHealthySystem();
        explicitZero.Registry.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, adapterKey, "TcpAckFrequency"), "1");
        explicitZero.Registry.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, adapterKey, "TCPNoDelay"), "0");

        Assert.False((await explicitZero.Reader.ReadAsync()).IsNagleDisabledOnActiveNic);
    }

    // ── разбор ответов системы ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 Gbps", 1000)]
    [InlineData("2.5 Gbps", 2500)]
    [InlineData("100 Mbps", 100)]
    [InlineData("10 Mbps", 10)]
    [InlineData("1,5 Gbps", 1500)]
    [InlineData("", null)]
    [InlineData("скорость неизвестна", null)]
    [InlineData("0 Mbps", null)]
    public void Скорость_соединения_разбирается(string? raw, int? expected) =>
        Assert.Equal(expected, SystemStateParsing.ParseLinkSpeedMbps(raw));

    [Fact]
    public void Схема_электропитания_разбирается_на_русском_выводе()
    {
        // Русская Windows печатает подпись по-русски, а GUID и имя схемы остаются узнаваемыми.
        (string? guid, string? name) = SystemStateParsing.ParsePowerScheme(
            "Схема электропитания GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Сбалансированная)");

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", guid);
        Assert.Equal("Сбалансированная", name);

        (string? englishGuid, string? englishName) = SystemStateParsing.ParsePowerScheme(
            "Power Scheme GUID: e9a42b02-d5df-448d-aa00-03f14749eb61  (Ultimate Performance)");

        Assert.Equal("e9a42b02-d5df-448d-aa00-03f14749eb61", englishGuid);
        Assert.Equal("Ultimate Performance", englishName);

        Assert.Equal((null, null), SystemStateParsing.ParsePowerScheme("вывод без идентификатора"));
    }

    [Theory]
    [InlineData("enabled", true)]
    [InlineData("Disabled", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("default", null)]
    [InlineData("", null)]
    public void Флаги_системы_разбираются_без_догадок(string? raw, bool? expected) =>
        Assert.Equal(expected, SystemStateParsing.TryParseFlag(raw));

    [Theory]
    [InlineData("0", false)]     // только обычная загрузка, обмена между компьютерами нет
    [InlineData("1", true)]      // обмен внутри локальной сети
    [InlineData("2", true)]      // обмен внутри своей группы
    [InlineData("3", true)]      // обмен через интернет
    [InlineData("", null)]
    [InlineData("много", null)]
    public void Режим_оптимизации_доставки_разбирается(string? raw, bool? expected) =>
        Assert.Equal(expected, SystemStateParsing.ParseDeliveryOptimizationP2p(raw));

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("", null)]
    public void Восстановление_системы_разбирается(string? raw, bool? expected) =>
        Assert.Equal(expected, SystemStateParsing.ParseSystemRestoreEnabled(raw));

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", null)]
    public void Разрешение_таймера_разбирается(string? raw, bool? expected) =>
        Assert.Equal(expected, SystemStateParsing.ParseTimerResolutionRaised(raw));

    [Fact]
    public void Версия_Windows_собирается_только_из_прочитанных_частей()
    {
        Assert.Equal("Windows 11 Pro · 24H2 · сборка 26100.1742",
            SystemStateParsing.DescribeWindows("Windows 11 Pro", "24H2", "26100", "1742"));
        Assert.Equal("Windows 10 Pro · сборка 19045",
            SystemStateParsing.DescribeWindows("Windows 10 Pro", null, "19045", null));
        Assert.Equal("сборка 26100", SystemStateParsing.DescribeWindows(null, null, "26100", null));
        Assert.Null(SystemStateParsing.DescribeWindows(null, null, null, null));
    }

    private static RegistryLocation Local(string subKey, string valueName) =>
        new(RegistryRootKind.LocalMachine, subKey, valueName);
}
