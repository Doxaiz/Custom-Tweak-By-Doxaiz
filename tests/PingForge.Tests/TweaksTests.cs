using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Tweaks;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

public sealed class NetshParserTests
{
    private const string RussianGlobalOutput = """
        Параметры TCP для текущего компьютера
        ----------------------------------------------
        Уровень автонастройки                     : обычный
        Получение по RSS                          : включено
        Возможность ECN                           : отключено
        Метки времени                             : отключено
        Устойчивость RTT без SACK                 : отключено
        Макс. число повторных передач SYN         : 4
        """;

    private const string EnglishGlobalOutput = """
        TCP Global Parameters
        ----------------------------------------------
        Receive-Side Scaling State          : enabled
        Receive Window Auto-Tuning Level    : normal
        Add-On Congestion Control Provider  : default
        ECN Capability                      : disabled
        RFC 1323 Timestamps                 : disabled
        Max SYN Retransmissions             : 2
        """;

    [Fact]
    public void RussianOutput_IsMappedToCanonicalTokens()
    {
        IReadOnlyDictionary<string, string> parsed = NetshOutputParser.ParseTcpGlobal(RussianGlobalOutput);

        Assert.Equal("normal", parsed["autotuninglevel"]);
        Assert.Equal("enabled", parsed["rss"]);
        Assert.Equal("disabled", parsed["ecncapability"]);
        Assert.Equal("4", parsed["maxsynretransmissions"]);
    }

    [Fact]
    public void EnglishOutput_IsMappedToCanonicalTokens()
    {
        IReadOnlyDictionary<string, string> parsed = NetshOutputParser.ParseTcpGlobal(EnglishGlobalOutput);

        Assert.Equal("enabled", parsed["rss"]);
        Assert.Equal("normal", parsed["autotuninglevel"]);
        Assert.Equal("disabled", parsed["ecncapability"]);
        Assert.Equal("2", parsed["maxsynretransmissions"]);
    }

    [Theory]
    [InlineData("высоко ограниченный", "highlyrestricted")]
    [InlineData("ограниченный", "restricted")]
    [InlineData("Отключено", "disabled")]
    [InlineData("enabled", "enabled")]
    [InlineData("normal (по умолчанию)", "normal")]
    public void Values_AreCanonicalizedRegardlessOfLanguageAndBrackets(string raw, string expected) =>
        Assert.Equal(expected, NetshOutputParser.CanonicalizeToken(raw));

    [Fact]
    public void SubinterfaceTable_FindsMtuByName()
    {
        const string table = """
            MTU     Состояние          Админ. состояние     Тип              Имя интерфейса
            ------  -----------------  -------------------  ---------------  -------------------
            1500    Подключено         Включено             Выделенный       Ethernet
            1492    Подключено         Включено             Выделенный       PPPoE-подключение
            """;

        Assert.Equal(1492, NetshOutputParser.ParseSubinterfaceMtu(table, "PPPoE-подключение"));
        Assert.Equal(1500, NetshOutputParser.ParseSubinterfaceMtu(table, "Ethernet"));
        Assert.Null(NetshOutputParser.ParseSubinterfaceMtu(table, "Wi-Fi"));
    }

    [Fact]
    public void DnsServers_AreParsedWithoutDuplicates()
    {
        const string output = """
            Настроенные DNS-серверы для интерфейса "Ethernet"
            DNS-серверы, настроенные статически:
            1.1.1.1
            8.8.8.8
            DNS-серверы, настроенные статически:
            1.1.1.1
            """;

        IReadOnlyList<string> servers = NetshOutputParser.ParseIpv4DnsServers(output);

        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, servers);
    }

    [Fact]
    public void RoundTrip_ServerListKeepsOrder() =>
        Assert.Equal(
            new[] { "9.9.9.9", "1.1.1.1" },
            NetshOutputParser.SplitServers(NetshOutputParser.JoinServers(new[] { "9.9.9.9", "1.1.1.1" })));

    [Fact]
    public void FirstMeaningfulLine_SkipsEmptyAndWhitespaceLines() =>
        Assert.Equal("Отказано в доступе.", NetshOutputParser.FirstMeaningfulLine("\r\n   \r\nОтказано в доступе.\r\n"));
}

public sealed class TweakCatalogTests
{
    private static TweakCatalog FullCatalog() => new(new TweakPlatformPorts(
        Registry: new FakeRegistryPort(),
        NicProperties: new FakeNicPropertyPort(),
        Command: new FakeCommandPort(),
        ActiveAdapter: new FakeActiveAdapterResolver(),
        MtuAdvice: new FakeMtuAdvice(),
        DnsAdvice: new FakeDnsAdvice(),
        NicOptions: new FakeNicOptions("128", "256", "512", "1024", "2048")));

    [Fact]
    public void Catalog_PassesPresetValidation()
    {
        IReadOnlyList<string> problems = PresetPolicy.ValidateCatalog(FullCatalog().GetAllTweaks());

        Assert.Empty(problems);
    }

    [Fact]
    public void Catalog_PassesSafetyAudit()
    {
        IReadOnlyList<string> problems = CatalogSafetyAudit.AuditCatalog(FullCatalog().GetAllTweaks());

        Assert.Empty(problems);
    }

    [Fact]
    public void SafetyAudit_RejectsForbiddenTweaks()
    {
        Assert.True(CatalogSafetyAudit.IsForbiddenTarget(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", out string reason));
        Assert.Contains("Defender", reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(CatalogSafetyAudit.IsForbiddenTarget(@"NIC∥NetworkAddress", out _));
        Assert.False(CatalogSafetyAudit.IsForbiddenTarget(@"net:MTU", out _));
    }

    [Fact]
    public void EveryTweak_HasRussianDescriptionsAndHonestGameAdvice()
    {
        foreach (ITweak tweak in FullCatalog().GetAllTweaks())
        {
            TweakDescriptor d = tweak.Descriptor;

            Assert.False(string.IsNullOrWhiteSpace(d.TitleRu), $"У {d.Code} нет русского названия.");
            Assert.False(string.IsNullOrWhiteSpace(d.WhatItDoesRu), $"У {d.Code} нет описания «что делает».");
            Assert.False(string.IsNullOrWhiteSpace(d.GamingJustificationRu), $"У {d.Code} нет совета «стоит ли для игр».");
            Assert.True(d.WhatItDoesRu.Length >= 40, $"Описание {d.Code} слишком короткое, чтобы быть честным.");
            Assert.True(d.GamingJustificationRu.Length >= 40, $"Совет {d.Code} слишком короткий.");
            Assert.DoesNotContain("пинг 0", d.GamingJustificationRu, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CatalogWithoutPlatformPorts_ReportsEveryTweakAsUnsupported()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts());

        foreach (ITweak tweak in catalog.GetAllTweaks())
        {
            (_, TweakSupport support) = await tweak.ResolveTargetAsync();
            Assert.False(support.IsSupported, $"Твик {tweak.Id} не должен считаться поддерживаемым без портов платформы.");
            Assert.False(string.IsNullOrWhiteSpace(support.ReasonRu));
        }
    }

    [Fact]
    public void LowLatencyPreset_ContainsOnlyHonestPingTweaks()
    {
        IReadOnlyList<ITweak> preset = FullCatalog().GetByPreset(TweakProfilePreset.LowLatencyGaming);

        Assert.NotEmpty(preset);
        Assert.All(preset, t => Assert.True(
            t.Descriptor.PingImpactDirection <= 0 && t.Descriptor.StabilityImpactDirection >= 0,
            $"В пресет «низкий пинг» попал твик {t.Id}, который пинг не снижает."));
        Assert.DoesNotContain(preset, t => t.Descriptor.Badge == GameRecommendationBadge.Caution);
        Assert.Contains(preset, t => t.Id == "TCP_ACK_FREQUENCY");
        Assert.DoesNotContain(preset, t => t.Id == "TCP_INITIAL_RTT");
    }

    [Fact]
    public void MaxThroughputPreset_TouchesOnlyThroughputParameters()
    {
        IReadOnlyList<ITweak> preset = FullCatalog().GetByPreset(TweakProfilePreset.MaxThroughput);

        Assert.NotEmpty(preset);
        Assert.All(preset, t => Assert.True(t.Descriptor.SpeedImpactDirection >= 0));
        Assert.Contains(preset, t => t.Id == "NIC_RECEIVE_BUFFERS");
    }

    [Fact]
    public async Task NicBufferTweaks_TakeMaximumFromDriverList_NotInventedValue()
    {
        var catalog = FullCatalog();
        ITweak tweak = catalog.GetByCode("NIC_RECEIVE_BUFFERS")!;

        (TweakTarget target, _) = await tweak.ResolveTargetAsync();
        ComputedTargetValue computed = await ((IComputedTargetValueTweak)tweak).ComputeTargetValueAsync(target);

        Assert.True(computed.IsAvailable);
        Assert.Equal("2048", computed.Value);
        Assert.Contains("2048", computed.ExplanationRu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MtuTweak_WithoutMeasurement_ReportsUnavailableInsteadOfGuessing()
    {
        var catalog = new TweakCatalog(new TweakPlatformPorts(Command: new FakeCommandPort()));
        ITweak tweak = catalog.GetByCode("MTU_SUBINTERFACE_OPTIMAL")!;

        ComputedTargetValue computed = await ((IComputedTargetValueTweak)tweak).ComputeTargetValueAsync(TweakTarget.Global("net"));

        Assert.False(computed.IsAvailable);
        Assert.Contains("не измерялся", computed.ExplanationRu, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ValueBackedTweakTests
{
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapter}";

    private static ValueBackedTweak CreateTweak(
        FakeRegistryPort port,
        string valueName = "TcpAckFrequency",
        string recommended = "1",
        bool rebootRequired = false,
        string code = "TCP_ACK_FREQUENCY")
    {
        var definition = new TweakDefinition(
            code, "Тестовый твик", "Test tweak", TweakCategory.TcpIpStack, TweakRiskLevel.Low,
            GameRecommendationBadge.Recommended,
            new TweakValueSource.RegistryValue(RegistryRootKind.LocalMachine, RegistryPath, valueName, TweakValueDataType.DWord),
            "Описание того, что делает твик, достаточно длинное для проверки каталога.",
            "Совет для игр, тоже достаточно длинный, чтобы пройти проверки каталога.",
            "По умолчанию отсутствует.", recommended, TweakValueDataType.DWord, -1, 0, 0, true, false,
            RebootRequired: rebootRequired);

        var accessor = new RegistryValueAccessor(port, RegistryRootKind.LocalMachine, RegistryPath, valueName, TweakValueDataType.DWord);
        return new ValueBackedTweak(definition.ToDescriptor(), accessor, new FakeActiveAdapterResolver());
    }

    private static TweakTarget AdapterA() => new("NIC Ethernet", "{GUID-ADAPTER-A}", "Ethernet");

    private static SnapshotItemEntry Entry(TweakTarget target, string planned, bool existedBefore = false, string? captured = null) =>
        new("TCP_ACK_FREQUENCY", TweakMechanismKind.Registry, target, existedBefore, captured, TweakValueDataType.DWord, planned);

    [Fact]
    public async Task Apply_WritesValueAndVerifiesInSystem()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);

        TweakOperationResult result = await tweak.ApplyAsync(Entry(AdapterA(), "1"));

        Assert.Equal(TweakOutcome.VerifiedEffective, result.Outcome);
        Assert.Equal("1", port.Get(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-A}"), "TcpAckFrequency")));
    }

    [Fact]
    public async Task Apply_WhenValueAlreadyOptimal_ReportsNoOp_AndDoesNotTouchSystem()
    {
        var port = new FakeRegistryPort();
        port.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-A}"), "TcpAckFrequency"), "1");
        ValueBackedTweak tweak = CreateTweak(port);

        TweakOperationResult result = await tweak.ApplyAsync(Entry(AdapterA(), "1"));

        Assert.Equal(TweakOutcome.SkippedNoOp, result.Outcome);
        Assert.Equal(0, port.WriteCount);
    }

    [Fact]
    public async Task Apply_WhenSystemKeepsOldValue_ReportsFailureInsteadOfSuccess()
    {
        var port = new FakeRegistryPort { IgnoreWrites = true };
        ValueBackedTweak tweak = CreateTweak(port);

        TweakOperationResult result = await tweak.ApplyAsync(Entry(AdapterA(), "1"));

        Assert.Equal(TweakOutcome.Failed, result.Outcome);
        Assert.Contains("не подтвердила", result.FailureReasonRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WithoutPlannedValue_RefusesToWriteAnything()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);

        TweakOperationResult result = await tweak.ApplyAsync(Entry(AdapterA(), string.Empty));

        Assert.Equal(TweakOutcome.SkippedUnsafeTarget, result.Outcome);
        Assert.Equal(0, port.WriteCount);
    }

    [Fact]
    public async Task Apply_WhenTweakUnsupported_ReportsSkipWithReason()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);
        TweakTarget withoutAdapter = TweakTarget.Global("registry");

        TweakOperationResult result = await tweak.ApplyAsync(Entry(withoutAdapter, "1"));

        Assert.Equal(TweakOutcome.SkippedUnsupported, result.Outcome);
        Assert.Contains("адаптер", result.FailureReasonRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_UsesAdapterGuidFromSnapshotEntry_NotFromCurrentResolver()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);
        var snapshotTarget = new TweakTarget("NIC Wi-Fi", "{GUID-ADAPTER-B}", "Wi-Fi");

        await tweak.ApplyAsync(Entry(snapshotTarget, "1"));

        // Резолвер по-прежнему указывает на адаптер A — запись обязана уйти на адаптер B из снапшота.
        Assert.Equal("1", port.Get(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-B}"), "TcpAckFrequency")));
        Assert.Null(port.Get(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-A}"), "TcpAckFrequency")));
    }

    [Fact]
    public async Task Rollback_DeletesValueThatDidNotExistBefore()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);
        TweakTarget target = AdapterA();

        await tweak.ApplyAsync(Entry(target, "1"));
        TweakOperationResult rollback = await tweak.RollbackAsync(Entry(target, "1"));

        Assert.Equal(TweakOutcome.RolledBack, rollback.Outcome);
        Assert.Null(port.Get(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-A}"), "TcpAckFrequency")));
    }

    [Fact]
    public async Task Rollback_RestoresCapturedValue()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);
        TweakTarget target = AdapterA();

        await tweak.ApplyAsync(Entry(target, "1", existedBefore: true, captured: "2"));
        TweakOperationResult rollback = await tweak.RollbackAsync(Entry(target, "1", existedBefore: true, captured: "2"));

        Assert.Equal(TweakOutcome.RolledBack, rollback.Outcome);
        Assert.Equal("2", port.Get(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-A}"), "TcpAckFrequency")));
    }

    [Fact]
    public async Task Rollback_WhenSystemRefuses_ReportsFailureNotSilentSuccess()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);
        TweakTarget target = AdapterA();

        await tweak.ApplyAsync(Entry(target, "1", existedBefore: true, captured: "2"));
        port.IgnoreWrites = true;

        TweakOperationResult rollback = await tweak.RollbackAsync(Entry(target, "1", existedBefore: true, captured: "2"));

        Assert.Equal(TweakOutcome.Failed, rollback.Outcome);
    }

    [Fact]
    public async Task Verify_DetectsExternalChangeAfterApply()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port);
        TweakTarget target = AdapterA();

        await tweak.ApplyAsync(Entry(target, "1"));
        port.Seed(new RegistryLocation(RegistryRootKind.LocalMachine, RegistryPath.Replace("{adapter}", "{GUID-ADAPTER-A}"), "TcpAckFrequency"), "2");

        TweakOperationResult verification = await tweak.VerifyAsync(Entry(target, "1"));

        Assert.Equal(TweakOutcome.Failed, verification.Outcome);
        Assert.Contains("не совпало", verification.FailureReasonRu, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TweakRequiringReboot_IsReportedAsPendingRestart_NotAsEffective()
    {
        var port = new FakeRegistryPort();
        ValueBackedTweak tweak = CreateTweak(port, rebootRequired: true);

        TweakOperationResult result = await tweak.ApplyAsync(Entry(AdapterA(), "1"));

        Assert.Equal(TweakOutcome.WrittenPendingRestart, result.Outcome);
        Assert.True(result.RebootOrAdapterRestartRequired);
    }

    [Fact]
    public async Task NetshWrite_RefusesValueThatLooksLikeExtraCommand()
    {
        var command = new FakeCommandPort();
        var accessor = new NetshGlobalAccessor(command, "autotuninglevel");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accessor.WriteAsync(TweakTarget.Global("netsh"), "normal; shutdown -r"));

        Assert.Empty(command.Invocations);
    }

    [Fact]
    public async Task NetshWrite_PassesValueAsSingleArgument()
    {
        var command = new FakeCommandPort();
        var accessor = new NetshGlobalAccessor(command, "autotuninglevel");

        await accessor.WriteAsync(TweakTarget.Global("netsh"), "normal");

        Assert.Single(command.Invocations);
        Assert.Equal("netsh interface tcp set global autotuninglevel=normal", command.Invocations[0]);
    }
}
