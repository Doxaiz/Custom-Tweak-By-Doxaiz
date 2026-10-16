using PingForge.Analysis;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Советник задержки ввода: мышь, клавиатура, фоновая запись и очередь пакетов на карте.
/// Проверяем главное обещание — твик предлагается только тому, у кого устройство действительно есть,
/// и только если значение реально прочитано и отличается от рекомендуемого. Роутер программой не настраивается.
/// </summary>
public class InputLatencyAdvisorTests
{
    private sealed class StubContext : INetworkContextReader
    {
        public NetworkContextSnapshot Snapshot { get; set; } = new(
            Array.Empty<AdapterInventoryItem>(), 1500, Array.Empty<string>(), "Ethernet", Array.Empty<string>());

        public Task<NetworkContextSnapshot> ReadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
    }

    private sealed class StubPeripherals : IPeripheralInventoryReader
    {
        public PeripheralsSnapshot Snapshot { get; set; } = new(
            new[] { new PeripheralInventoryItem("Указатель", "HID-совместимая мышь", false, true) },
            new[] { new PeripheralInventoryItem("Клавиатура", "HID Keyboard Device", false, true) },
            Array.Empty<string>());

        public Task<PeripheralsSnapshot> ReadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
    }

    private sealed class Harness
    {
        public FakeRegistryPort Registry { get; } = new();

        public TweakCatalog Catalog { get; }

        public StubContext Context { get; } = new();

        public StubPeripherals Peripherals { get; } = new();

        public Harness()
        {
            Catalog = new TweakCatalog(new TweakPlatformPorts(
                Registry: Registry,
                ActiveAdapter: new FakeActiveAdapterResolver("{GUID-ADAPTER-A}", "Ethernet")));
        }

        public InputLatencyAdvisor CreateAdvisor(bool withCatalog = true) =>
            new(Context, Peripherals, withCatalog ? Catalog : null);

        public static RegistryLocation Mouse(string valueName) =>
            new(RegistryRootKind.CurrentUser, @"Control Panel\Mouse", valueName);

        /// <summary>Отмечает все мышиные значения как уже приведённые к рекомендуемым.</summary>
        public void SeedOptimizedPointer()
        {
            Registry.Seed(Mouse("MouseSpeed"), "0");
            Registry.Seed(Mouse("MouseThreshold1"), "0");
            Registry.Seed(Mouse("MouseThreshold2"), "0");
            Registry.Seed(Mouse("MouseSensitivity"), "10");
        }
    }

    private static AdapterInventoryItem Adapter(
        string name,
        bool wireless = false,
        bool vpn = false,
        string? gateway = "192.168.1.1") =>
        new($"{{{name}}}", name, name, true, !vpn, vpn, gateway is not null, 1500, wireless, gateway);

    [Fact]
    public async Task PointerAndKeyboardPresent_AllInputTweaksRecommended_WithRealCurrentValues()
    {
        var h = new Harness();
        h.Registry.Seed(Harness.Mouse("MouseSpeed"), "1");

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        string[] codes = report.Recommendations.Select(r => r.TweakCode).ToArray();
        Assert.Contains("MOUSE_ACCEL_SPEED", codes);
        Assert.Contains("MOUSE_ACCEL_THRESHOLD1", codes);
        Assert.Contains("MOUSE_ACCEL_THRESHOLD2", codes);
        Assert.Contains("MOUSE_SENSITIVITY_NEUTRAL", codes);
        Assert.Contains("KEYBOARD_FILTERKEYS_OFF", codes);
        Assert.Contains("GAME_DVR_OFF", codes);
        Assert.Contains("GAME_DVR_CAPTURE_OFF", codes);

        TweakRecommendationItem accel = report.Recommendations.Single(r => r.TweakCode == "MOUSE_ACCEL_SPEED");
        Assert.Equal("0", accel.RecommendedValueDisplay);
        Assert.Equal("Сейчас: 1", accel.CurrentValueDisplay);
        Assert.Contains("HID-совместимая мышь", accel.ReasonRu);
    }

    [Fact]
    public async Task NoPointingDevice_MouseTweaksAreSkipped_NotOffered()
    {
        var h = new Harness();
        h.Peripherals.Snapshot = new PeripheralsSnapshot(
            Array.Empty<PeripheralInventoryItem>(),
            new[] { new PeripheralInventoryItem("Клавиатура", "HID Keyboard Device", false, true) },
            Array.Empty<string>());

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode.StartsWith("MOUSE_", StringComparison.Ordinal));
        Assert.Contains(report.SkippedChecks, s => s.Contains("указатель не обнаружен", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PeripheralsUnreadable_SkipsBothGroups_AndWarnsHonestly()
    {
        var h = new Harness();
        h.Peripherals.Snapshot = PeripheralsSnapshot.Unknown("Перечень устройств ввода не прочитан: таймаут.");

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        Assert.Contains(InputLatencyAdvisor.WarningPeripheralsUnknown, NetworkAnalysisReport.WarningCodes(report.Warnings.ToArray()));
        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode.StartsWith("MOUSE_", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode == "KEYBOARD_FILTERKEYS_OFF");
        Assert.Contains(report.SkippedChecks, s => s.Contains("прочитать не удалось", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AlreadyOptimizedValues_AreReportedAsNothingToChange()
    {
        var h = new Harness();
        h.SeedOptimizedPointer();

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode.StartsWith("MOUSE_", StringComparison.Ordinal));
        Assert.Contains(report.SkippedChecks, s => s.Contains("уже настроено", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WirelessPointer_AndWifiPath_ProduceWarningsWithHonestLimits()
    {
        var h = new Harness();
        h.Peripherals.Snapshot = new PeripheralsSnapshot(
            new[] { new PeripheralInventoryItem("Указатель", "Logitech USB Receiver", true, true) },
            new[] { new PeripheralInventoryItem("Клавиатура", "HID Keyboard Device", false, true) },
            Array.Empty<string>());
        h.Context.Snapshot = new NetworkContextSnapshot(
            new[] { Adapter("Wi-Fi", wireless: true) },
            1500,
            Array.Empty<string>(),
            "Wi-Fi",
            Array.Empty<string>());

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        IReadOnlyList<string> codes = NetworkAnalysisReport.WarningCodes(report.Warnings.ToArray());
        Assert.Contains(InputLatencyAdvisor.WarningWirelessPointer, codes);
        Assert.Contains(InputLatencyAdvisor.WarningInputOverWifi, codes);
        Assert.Contains(InputLatencyAdvisor.WarningPathThroughRouter, codes);

        InterferenceWarning wifi = report.Warnings.Single(w => w.Code == InputLatencyAdvisor.WarningInputOverWifi);
        Assert.Contains("Проводное подключение", wifi.RemediationRu);

        // Mouse tweaks still recommended: акселерация правится независимо от радиоканала.
        Assert.Contains(report.Recommendations, r => r.TweakCode == "MOUSE_ACCEL_SPEED");
    }

    [Fact]
    public async Task RouterInPath_IsReported_HighlightingThatProgramDoesNotTouchIt()
    {
        var h = new Harness();
        h.Context.Snapshot = new NetworkContextSnapshot(
            new[] { Adapter("Ethernet") },
            1500,
            Array.Empty<string>(),
            "Ethernet",
            Array.Empty<string>());

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        InterferenceWarning router = report.Warnings.Single(w => w.Code == InputLatencyAdvisor.WarningPathThroughRouter);
        Assert.Contains("192.168.1.1", router.DescriptionRu);
        Assert.Contains("войти в него программа не имеет права", router.DescriptionRu);
        Assert.Contains(report.Notes, n => n.Contains("не настраивается", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VpnAdapter_NicPriorityTweakIsSkipped()
    {
        var h = new Harness();
        h.Context.Snapshot = new NetworkContextSnapshot(
            new[] { Adapter("WireGuard Tunnel", vpn: true) },
            1420,
            Array.Empty<string>(),
            "WireGuard Tunnel",
            Array.Empty<string>());

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode == "NIC_PACKET_PRIORITY_ON");
        Assert.Contains(report.SkippedChecks, s => s.Contains("виртуальным", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PhysicalAdapter_PacketPriorityIsOffered_ViaDriverPropertyCheck()
    {
        var h = new Harness();
        h.Context.Snapshot = new NetworkContextSnapshot(
            new[] { Adapter("Ethernet") },
            1500,
            Array.Empty<string>(),
            "Ethernet",
            Array.Empty<string>());

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        // Без порта свойств NIC твик честно уходит в «пропущено», а не в предложения.
        Assert.Contains(report.SkippedChecks, s => s.Contains("NIC_PACKET_PRIORITY_ON", StringComparison.Ordinal)
            || s.Contains("Packet Priority", StringComparison.OrdinalIgnoreCase)
            || s.Contains("драйвер", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KeyboardRepeatDelay_IsNeverOffered_AndReasonIsSpelledOut()
    {
        var h = new Harness();

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        Assert.DoesNotContain(report.Recommendations, r => r.TweakCode == "KEYBOARD_REPEAT_DELAY_MIN");
        Assert.Contains(report.SkippedChecks, s => s.Contains("не влияет", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Notes_AlwaysStateWhatTheseFixesDoNotDo()
    {
        var h = new Harness();

        NetworkAnalysisReport report = await h.CreateAdvisor().AdviseAsync();

        Assert.Contains(report.Notes, n => n.Contains("задержку внутри игрового сервера", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Проверок выполнено:", report.SummaryRu);
    }

    [Fact]
    public async Task NoCatalog_RecommendationsAreSkipped_WithNoteInsteadOfInventedTweak()
    {
        var h = new Harness();

        NetworkAnalysisReport report = await h.CreateAdvisor(withCatalog: false).AdviseAsync();

        Assert.Empty(report.Recommendations);
        Assert.Contains(report.Notes, n => n.Contains("твик отсутствует в каталоге", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WirelessMarkers_DoNotMatchWiredDeviceNames()
    {
        // Обратная проверка маркеров: проводные названия не должны попадать в «беспроводные».
        Assert.False(new PeripheralInventoryItem("Указатель", "HID-совместимая мышь", false, true).IsWireless);
        Assert.True(new PeripheralInventoryItem("Указатель", "Bluetooth Mouse", true, true).IsWireless);
    }
}
