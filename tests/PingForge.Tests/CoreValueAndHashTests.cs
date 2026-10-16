using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Core.Security;
using PingForge.Core.Tweaks;
using Xunit;

namespace PingForge.Tests;

public sealed class CanonicalFormatterTests
{
    [Theory]
    [InlineData("-1", "0xFFFFFFFF")]
    [InlineData("-1", "4294967295")]
    [InlineData("0", "0")]
    [InlineData("24", "0x18")]
    public void DWord_IsComparedNumerically_NotAsText(string left, string right) =>
        Assert.True(TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.DWord, left, right));

    [Fact]
    public void DWord_DifferentValues_AreNotEqual() =>
        Assert.False(TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.DWord, "10", "11"));

    [Fact]
    public void DWord_NonNumericValue_DoesNotThrow_AndReturnsFalse() =>
        Assert.False(TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.DWord, "restricted", "normal"));

    [Fact]
    public void MultiString_JoinsWithPipe_AndIsCaseSensitive() =>
        Assert.Equal("A|B", TweakValueCanonicalFormatter.Format(TweakValueDataType.MultiString, new[] { "A", "B" }));

    [Fact]
    public void Binary_IsUppercaseHex() =>
        Assert.Equal("CAFE", TweakValueCanonicalFormatter.Format(TweakValueDataType.Binary, new byte[] { 0xCA, 0xFE }));

    [Fact]
    public void RoundTrip_AllValueTypes_KeepsEquality()
    {
        Assert.True(TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.DWord, "4294967295", TweakValueCanonicalFormatter.Format(TweakValueDataType.DWord, -1)));
        Assert.True(TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.QWord, "18446744073709551615", TweakValueCanonicalFormatter.Format(TweakValueDataType.QWord, ulong.MaxValue)));
        Assert.True(TweakValueCanonicalFormatter.AreEqual(TweakValueDataType.String, "Enabled", TweakValueCanonicalFormatter.Format(TweakValueDataType.String, "Enabled ")));
    }

    [Theory]
    // Значение, пришедшее из системы, НЕ должно попасть в аргументы внешней утилиты, если содержит текст или разделители.
    [InlineData("0 (IPv6 включён)", false)]
    [InlineData("2 или 3", false)]
    [InlineData("не задано", false)]
    [InlineData("normal; shutdown -r", false)]
    [InlineData("highlyrestricted", true)]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", true)]
    [InlineData("1.1.1.1,1.0.0.1", true)]
    public void SafeCliToken_RejectsHumanTextAndInjection(string value, bool expected) =>
        Assert.Equal(expected, TweakValueCanonicalFormatter.IsSafeCliToken(value));
}

public sealed class SnapshotHasherTests
{
    private static SnapshotItemEntry Entry(string code, string? captured, string planned = "1") =>
        new(code, TweakMechanismKind.Registry, new TweakTarget($@"HKLM\SOFTWARE\Test\{code}", "{GUID-A}", "Адаптер A"),
            captured is not null, captured, TweakValueDataType.DWord, planned);

    [Fact]
    public void Hash_IsIndependentOfItemOrder()
    {
        SnapshotItemEntry[] items = { Entry("TCP_NO_DELAY", "0"), Entry("NIC_ITR", "1\r\n"), Entry("PWR_PLAN", null) };

        string first = SnapshotHasher.ComputeCanonicalHash("snap-1", items);
        string second = SnapshotHasher.ComputeCanonicalHash("snap-1", items.Reverse());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Hash_ChangesWhenCapturedValueIsTampered()
    {
        SnapshotItemEntry original = Entry("TCP_NO_DELAY", "0");
        SnapshotItemEntry tampered = original with { CapturedRawValue = "1" };

        Assert.NotEqual(
            SnapshotHasher.ComputeCanonicalHash("snap-1", new[] { original }),
            SnapshotHasher.ComputeCanonicalHash("snap-1", new[] { tampered }));
    }

    [Fact]
    public void Hash_IgnoresLineEndingStyleInCapturedText()
    {
        string withCrLf = SnapshotHasher.ComputeCanonicalHash("snap-1", new[] { Entry("X", "line1\r\nline2") });
        string withLf = SnapshotHasher.ComputeCanonicalHash("snap-1", new[] { Entry("X", "line1\nline2") });

        Assert.Equal(withCrLf, withLf);
    }

    [Fact]
    public void VerifyIntegrity_DetectsModification()
    {
        var entries = new[] { Entry("TCP_NO_DELAY", "0") };
        string hash = SnapshotHasher.ComputeCanonicalHash("snap-1", entries);

        var snapshot = new SystemSnapshot("snap-1", DateTime.UtcNow, "Тест", TweakProfilePreset.LowLatencyGaming,
            false, hash, false, null, entries);

        Assert.True(SnapshotHasher.VerifyIntegrity(snapshot));
        Assert.False(SnapshotHasher.VerifyIntegrity(snapshot with { Entries = new[] { Entry("TCP_NO_DELAY", "9") } }));
    }
}

public sealed class PresetPolicyTests
{
    private static ITweak Stub(string code, GameRecommendationBadge badge, int pingImpact, int speedImpact,
        int stabilityImpact = 0, bool lowPing = false, bool maxSpeed = false, bool computed = false,
        string? staticRecommendedValue = null) =>
        new PolicyProbeTweak(code, badge, pingImpact, speedImpact, stabilityImpact, lowPing, maxSpeed, computed, staticRecommendedValue);

    [Fact]
    public void CautionTweak_IsNeverAllowedInLowPingPreset()
    {
        ITweak tweak = Stub("MMCSS_RESP", GameRecommendationBadge.Caution, -1, 0);
        Assert.False(PresetPolicy.IsAllowedInPreset(tweak.Descriptor, TweakProfilePreset.LowLatencyGaming));
    }

    [Fact]
    public void TweakThatReducesThroughput_IsNeverAllowedInMaxSpeedPreset()
    {
        ITweak tweak = Stub("NIC_LSO_OFF", GameRecommendationBadge.Optional, -1, -1);
        Assert.False(PresetPolicy.IsAllowedInPreset(tweak.Descriptor, TweakProfilePreset.MaxThroughput));
    }

    [Fact]
    public void NotRecommendedTweak_IsExcludedFromAllAutomaticPresets()
    {
        ITweak tweak = Stub("NET_PREFER_IPV4", GameRecommendationBadge.NotRecommended, 0, 0, -1);
        Assert.False(PresetPolicy.IsAllowedInPreset(tweak.Descriptor, TweakProfilePreset.LowLatencyGaming));
        Assert.False(PresetPolicy.IsAllowedInPreset(tweak.Descriptor, TweakProfilePreset.MaxThroughput));
    }

    [Fact]
    public void ValidateCatalog_ReportsInconsistentPresetFlagsAndMissingJustification()
    {
        var bad = Stub("BAD_TWEAK", GameRecommendationBadge.Caution, -1, -1, lowPing: true, maxSpeed: true);
        // Вычисляемый твик, у которого всё же задано статичное значение — ловим такую ошибку каталога.
        var computedWithStaticValue = Stub("COMPUTED", GameRecommendationBadge.Recommended, -1, +1,
            computed: true, staticRecommendedValue: "1500");

        IReadOnlyList<string> problems = PresetPolicy.ValidateCatalog(new[] { bad, computedWithStaticValue });

        Assert.Contains(problems, p => p.Contains("BAD_TWEAK", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("COMPUTED", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateCatalog_ReportsDuplicateCodes()
    {
        ITweak first = Stub("DUP", GameRecommendationBadge.Recommended, -1, 0);
        ITweak second = Stub("DUP", GameRecommendationBadge.Recommended, -1, 0);

        Assert.Contains(PresetPolicy.ValidateCatalog(new[] { first, second }), p => p.Contains("Дублирующийся", StringComparison.Ordinal));
    }

    private sealed class PolicyProbeTweak : TweakStub
    {
        public PolicyProbeTweak(string code, GameRecommendationBadge badge, int pingImpact, int speedImpact,
            int stabilityImpact, bool lowPing, bool maxSpeed, bool computed, string? staticRecommendedValue = null)
        {
            Id = code;
            Descriptor = new TweakDescriptor(
                code, $"Проба {code}", $"Probe {code}", TweakCategory.TcpIpStack, TweakMechanismKind.Registry,
                @"HKLM\SOFTWARE\Test", "Описание.", badge, "Обоснование для игр.", TweakRiskLevel.Low,
                pingImpact, speedImpact, stabilityImpact, false, true, false, "0",
                staticRecommendedValue ?? (computed ? string.Empty : "1"),
                TweakValueDataType.DWord, lowPing, maxSpeed)
            {
                HasComputedTarget = computed
            };
        }

        public override string Id { get; }

        public override TweakDescriptor Descriptor { get; }

        public override Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default) =>
            Task.FromResult((TweakTarget.Global(@"HKLM\SOFTWARE\Test"), TweakSupport.Supported("ok")));

        public override Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default) =>
            Task.FromResult(new CapturedParameterState(Id, Descriptor.Mechanism, target, false, null, TweakValueDataType.DWord, true, false, "ok"));

        public override Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default) =>
            Task.FromResult(TweakOperationResult.Skip(Id, Descriptor.TitleRu, entry.ResourceLocator, TweakOutcome.SkippedNoOp, "тест", "0"));

        public override Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default) =>
            Task.FromResult(TweakOperationResult.Skip(Id, Descriptor.TitleRu, entry.ResourceLocator, TweakOutcome.SkippedNoOp, "тест", "0"));

        public override Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default) =>
            Task.FromResult(TweakOperationResult.Skip(Id, Descriptor.TitleRu, entry.ResourceLocator, TweakOutcome.SkippedNoOp, "тест", "0"));
    }
}
