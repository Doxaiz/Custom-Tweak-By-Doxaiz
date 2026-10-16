using System.Net;
using PingForge.Analysis;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Data.Repositories;
using PingForge.Probing;

namespace PingForge.Tests;

/// <summary>
/// Тестовые двойники. Принципиально реализуют ИНТЕРФЕЙСЫ, а не наследуются от продакшн-классов:
/// иначе тест проверяет реализацию бэкенда, а не контракт.
/// </summary>
public abstract class TweakStub : ITweak
{
    public abstract string Id { get; }
    public abstract TweakDescriptor Descriptor { get; }
    public abstract Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default);
    public abstract Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default);
    public abstract Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default);
    public abstract Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default);
    public abstract Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default);
}

public sealed class FakeTweak : TweakStub
{
    public FakeTweak(
        string code,
        string defaultValue = "0",
        bool existingValuePresent = false,
        bool isSupported = true,
        bool failOnApply = false,
        bool failOnVerify = false,
        bool isAlreadyOptimal = false,
        bool requiresRestart = false,
        int applyDelayMs = 0,
        TweakMechanismKind mechanism = TweakMechanismKind.Registry)
    {
        Code = code;
        DefaultValue = defaultValue;
        ExistingValuePresent = existingValuePresent;
        IsSupported = isSupported;
        FailOnApply = failOnApply;
        FailOnVerify = failOnVerify;
        IsAlreadyOptimal = isAlreadyOptimal;
        RequiresRestart = requiresRestart;
        ApplyDelayMs = applyDelayMs;
        Mechanism = mechanism;

        Descriptor = new TweakDescriptor(
            code,
            $"Тестовый твик {code}",
            $"Test tweak {code}",
            TweakCategory.TcpIpStack,
            mechanism,
            $@"HKLM\SOFTWARE\PingForge\Test\{code}",
            "Описание тестового твика.",
            GameRecommendationBadge.Recommended,
            "Тестовое обоснование.",
            TweakRiskLevel.Low,
            -1, 0, +1, false, true, false, "0", "1", TweakValueDataType.DWord, false, false);
    }

    public string Code { get; }
    public TweakMechanismKind Mechanism { get; }
    public string DefaultValue { get; }
    public bool ExistingValuePresent { get; }
    public bool IsSupported { get; set; }
    public bool FailOnApply { get; set; }
    public bool FailOnVerify { get; set; }
    public bool IsAlreadyOptimal { get; set; }
    public bool RequiresRestart { get; }
    public int ApplyDelayMs { get; }

    /// <summary>«Фактическое» значение в системе. Живёт между вызовами Apply/Verify/Rollback.</summary>
    public string? CurrentValue { get; private set; }

    /// <summary>Если true — ApplyAsync записывает значение в ДРУГУЮ цель (имитация смены адаптера в середине батча).</summary>
    public bool SimulateTargetChangeOnApply { get; set; }

    /// <summary>Адаптер, в который реально ушла запись при последнем Apply (может отличаться от цели снапшота).</summary>
    public string? LastWrittenTargetGuid { get; private set; }

    public int ApplyCalls { get; private set; }
    public int RollbackCalls { get; private set; }
    public int VerifyCalls { get; private set; }

    /// <summary>Записи, которые реально получили Apply / Verify / Rollback — для проверки контракта.</summary>
    public SnapshotItemEntry? LastApplyEntry { get; private set; }
    public SnapshotItemEntry? LastVerifyEntry { get; private set; }
    public SnapshotItemEntry? LastRollbackEntry { get; private set; }

    public override string Id => Code;

    public override TweakDescriptor Descriptor { get; }

    public override Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default)
    {
        var target = new TweakTarget($@"HKLM\SOFTWARE\PingForge\Test\{Code}", "{GUID-ADAPTER-A}", "Адаптер A");
        TweakSupport support = IsSupported
            ? TweakSupport.Supported("Тестовая цель доступна.")
            : TweakSupport.Unsupported("Тестовая цель недоступна.");

        return Task.FromResult((target, support));
    }

    public override Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default)
    {
        string? value = IsAlreadyOptimal ? "1" : (ExistingValuePresent ? DefaultValue : null);

        return Task.FromResult(new CapturedParameterState(
            Code, Mechanism, target,
            ExistedBefore: value is not null,
            CapturedRawValue: value,
            ValueDataType: TweakValueDataType.DWord,
            IsSupported: IsSupported,
            IsUnparsableOutput: false,
            SupportReasonRu: IsSupported ? "Поддерживается" : "Не поддерживается"));
    }

    public override async Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        ApplyCalls++;
        LastApplyEntry = entry;

        if (ApplyDelayMs > 0)
        {
            await Task.Delay(ApplyDelayMs, ct).ConfigureAwait(false);
        }

        if (!IsSupported)
        {
            return TweakOperationResult.Skip(Code, Descriptor.TitleRu, entry.ResourceLocator,
                TweakOutcome.SkippedUnsupported, "Твик не поддерживается на этой системе.", "<н/д>");
        }

        if (SimulateTargetChangeOnApply)
        {
            entry = entry with { Target = entry.Target with { AdapterInterfaceGuid = "{GUID-ADAPTER-B}" } };
        }

        LastWrittenTargetGuid = entry.Target.AdapterInterfaceGuid;

        if (FailOnApply)
        {
            return new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator, TweakOutcome.Failed,
                TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue), entry.PlannedNewValue, "ошибка",
                "Тестовый сбой применения.", false, DateTime.UtcNow);
        }

        if (IsAlreadyOptimal)
        {
            CurrentValue = "1";
            return new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator, TweakOutcome.SkippedNoOp,
                "1", entry.PlannedNewValue, "1", "Уже оптимально.", false, DateTime.UtcNow);
        }

        CurrentValue = string.IsNullOrEmpty(entry.PlannedNewValue) ? "1" : entry.PlannedNewValue;

        TweakOutcome outcome = FailOnVerify
            ? TweakOutcome.Failed
            : RequiresRestart ? TweakOutcome.WrittenPendingRestart : TweakOutcome.VerifiedEffective;

        return new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator, outcome,
            TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue), entry.PlannedNewValue, CurrentValue,
            FailOnVerify ? "Тестовый провал верификации." : null, RequiresRestart, DateTime.UtcNow);
    }

    public override Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        VerifyCalls++;
        LastVerifyEntry = entry;

        bool match = string.Equals(CurrentValue, entry.PlannedNewValue, StringComparison.Ordinal);
        TweakOutcome outcome = match
            ? (RequiresRestart ? TweakOutcome.WrittenPendingRestart : TweakOutcome.VerifiedEffective)
            : TweakOutcome.Failed;

        return Task.FromResult(new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator, outcome,
            TweakValueCanonicalFormatter.Describe(entry.CapturedRawValue), entry.PlannedNewValue,
            CurrentValue ?? "<не задано>",
            match ? null : "Фактическое значение не совпало с ожидаемым.", RequiresRestart, DateTime.UtcNow));
    }

    public override Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        RollbackCalls++;
        LastRollbackEntry = entry;

        if (!IsSupported)
        {
            return Task.FromResult(TweakOperationResult.Skip(Code, Descriptor.TitleRu, entry.ResourceLocator,
                TweakOutcome.SkippedUnsupported, "Откат не требуется: цель не поддерживается.", "<н/д>"));
        }

        if (!entry.ExistedBefore || entry.CapturedRawValue is null)
        {
            CurrentValue = null;
            return Task.FromResult(new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator,
                TweakOutcome.RolledBack, "<удалено>", "<не задано>", "<не задано>", null, false, DateTime.UtcNow));
        }

        CurrentValue = entry.CapturedRawValue;
        return Task.FromResult(new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator,
            TweakOutcome.RolledBack, entry.CapturedRawValue, entry.CapturedRawValue, entry.CapturedRawValue,
            null, false, DateTime.UtcNow));
    }
}

/// <summary>Твик с вычисляемым целевым значением — проверяет работу TargetValueResolver.</summary>
public sealed class FakeComputedTweak : TweakStub, IComputedTargetValueTweak
{
    public FakeComputedTweak(string code, string? computedValue, string computeExplanation = "Вычислено на основе замеров.")
    {
        Code = code;
        ComputedValue = computedValue;
        ComputeExplanation = computeExplanation;

        Descriptor = new TweakDescriptor(
            code, $"Вычисляемый твик {code}", $"Computed tweak {code}",
            TweakCategory.TcpIpStack, TweakMechanismKind.MtuSubinterface,
            $@"HKLM\SOFTWARE\PingForge\Test\{code}",
            "Целевое значение определяется замерами.", GameRecommendationBadge.Recommended,
            "Рекомендуется.", TweakRiskLevel.Low, -1, +1, +1, false, true, true, "1500", string.Empty,
            TweakValueDataType.DWord, true, true)
        {
            HasComputedTarget = true
        };
    }

    public string Code { get; }
    public string? ComputedValue { get; }
    public string ComputeExplanation { get; }
    public string? CurrentValue { get; private set; }

    public override string Id => Code;

    public override TweakDescriptor Descriptor { get; }

    public override Task<(TweakTarget Target, TweakSupport Support)> ResolveTargetAsync(CancellationToken ct = default) =>
        Task.FromResult((new TweakTarget($@"HKLM\SOFTWARE\PingForge\Test\{Code}", "{GUID-ADAPTER-A}", "Адаптер A"),
            TweakSupport.Supported("Цель доступна.", manual: true)));

    public override Task<CapturedParameterState> CaptureAsync(TweakTarget target, CancellationToken ct = default) =>
        Task.FromResult(new CapturedParameterState(Code, Descriptor.Mechanism, target, true, "1500",
            TweakValueDataType.DWord, true, false, "Поддерживается"));

    public Task<ComputedTargetValue> ComputeTargetValueAsync(TweakTarget target, CancellationToken ct = default) =>
        Task.FromResult(ComputedValue is null
            ? ComputedTargetValue.Unavailable("Определить значение не удалось.")
            : new ComputedTargetValue(ComputedValue, TweakValueDataType.DWord, ComputeExplanation));

    public override Task<TweakOperationResult> ApplyAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.PlannedNewValue))
        {
            return Task.FromResult(TweakOperationResult.Skip(Code, Descriptor.TitleRu, entry.ResourceLocator,
                TweakOutcome.SkippedUnsafeTarget, "Целевое значение не определено — применение отменено.", "<н/д>"));
        }

        CurrentValue = entry.PlannedNewValue;
        return Task.FromResult(new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator,
            TweakOutcome.VerifiedEffective, "1500", entry.PlannedNewValue, CurrentValue, null, true, DateTime.UtcNow));
    }

    public override Task<TweakOperationResult> VerifyAsync(SnapshotItemEntry entry, CancellationToken ct = default) =>
        Task.FromResult(new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator,
            string.Equals(CurrentValue, entry.PlannedNewValue, StringComparison.Ordinal) ? TweakOutcome.VerifiedEffective : TweakOutcome.Failed,
            "1500", entry.PlannedNewValue, CurrentValue ?? "<не задано>", null, true, DateTime.UtcNow));

    public override Task<TweakOperationResult> RollbackAsync(SnapshotItemEntry entry, CancellationToken ct = default)
    {
        CurrentValue = entry.CapturedRawValue;
        return Task.FromResult(new TweakOperationResult(Code, Descriptor.TitleRu, entry.ResourceLocator,
            TweakOutcome.RolledBack, entry.CapturedRawValue ?? "<не задано>", entry.CapturedRawValue ?? "<не задано>",
            CurrentValue ?? "<не задано>", null, true, DateTime.UtcNow));
    }
}

public sealed class FakeTweakCatalog : ITweakCatalog
{
    private readonly List<ITweak> _tweaks;
    private readonly List<IActionTask> _actions;

    public FakeTweakCatalog(IEnumerable<ITweak> tweaks, IEnumerable<IActionTask>? actions = null)
    {
        _tweaks = tweaks.ToList();
        _actions = actions?.ToList() ?? new List<IActionTask>();
    }

    public IReadOnlyList<ITweak> GetAllTweaks() => _tweaks;

    public IReadOnlyList<IActionTask> GetAllActionTasks() => _actions;

    public ITweak? GetByCode(string tweakCode) =>
        _tweaks.FirstOrDefault(t => string.Equals(t.Id, tweakCode, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ITweak> GetByPreset(TweakProfilePreset preset) => _tweaks;
}

public sealed class FakeCrashRecoveryHandler : ICrashRecoveryHandler
{
    public List<string> UndoneOperations { get; } = new();

    public bool FailEverything { get; set; }

    public Task<(bool Succeeded, string MessageRu)> UndoPendingOperationAsync(
        string operationCategory, string targetKey, string undoDescriptorJson, CancellationToken ct = default)
    {
        UndoneOperations.Add($"{operationCategory}:{targetKey}");

        return Task.FromResult(FailEverything
            ? (false, $"Не удалось снять {operationCategory} {targetKey}.")
            : (true, $"Снято: {operationCategory} {targetKey}."));
    }
}

public sealed class FakeRestorePointService : ISystemRestorePointService
{
    public FakeRestorePointService(RestorePointResultKind outcome = RestorePointResultKind.CreatedSuccessfully, string? message = null)
    {
        Outcome = outcome;
        Message = message ?? "Точка восстановления создана.";
    }

    public RestorePointResultKind Outcome { get; set; }

    public string Message { get; }

    public int CallCount { get; private set; }

    public (RestorePointResultKind Outcome, string MessageRu) TryCreateRestorePoint(string description)
    {
        CallCount++;
        return (Outcome, Message);
    }
}

// ----- Двойники транспорта для проверки измерений -----

public sealed class StubIcmpProbe : IIcmpProbe
{
    /// <summary>Функция: (размер полезной нагрузки, TTL) → ответ.</summary>
    public Func<int, int, IcmpProbeReply> Handler { get; set; } =
        (_, _) => new IcmpProbeReply(IcmpProbeStatus.Success, 12.0, IPAddress.Parse("1.2.3.4"));

    public bool RequiresAdministratorRights => true;

    public Task<IcmpProbeReply> SendAsync(IPAddress target, int payloadBytes, bool dontFragment, int ttl, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(Handler(payloadBytes, ttl));
}

public sealed class StubTcpProbe : ITcpHandshakeProbe
{
    public Func<IPEndPoint, TcpProbeReply> Handler { get; set; } =
        _ => new TcpProbeReply(TcpProbeStatus.ConnectionRefused, 8.5);

    public Task<TcpProbeReply> TryConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(Handler(target));
}

public sealed class StubHostResolver : IHostResolver
{
    public Dictionary<string, IPAddress> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default) =>
        Map.TryGetValue(host, out IPAddress? address)
            ? Task.FromResult<IReadOnlyList<IPAddress>>(new[] { address })
            : Task.FromResult<IReadOnlyList<IPAddress>>(Array.Empty<IPAddress>());
}

public sealed class StubDnsTransport : IDnsQueryTransport
{
    /// <summary>Функция: (сервер, домен, ID) → сырой ответ.</summary>
    public Func<IPAddress, string, ushort, DnsRawResponse?> Handler { get; set; } =
        (server, domain, id) => new DnsRawResponse(new IPEndPoint(server, 53), id, true, domain, 0, 1);

    public int QueriesSent { get; private set; }

    public Task<DnsRawResponse?> QueryAsync(IPAddress server, string domain, ushort transactionId, TimeSpan timeout, CancellationToken ct = default)
    {
        QueriesSent++;
        return Task.FromResult(Handler(server, domain, transactionId));
    }
}

/// <summary>Чтение состояния системы: все поля задаются тестом, null означает «прочитать не удалось».</summary>
public sealed class FakeSystemStateReader : ISystemStateReader
{
    public SystemStateReading Reading { get; set; } = new(
        WindowsEditionAndBuild: "Windows 11 Pro 26100",
        IsRunningAsAdministrator: true,
        ActiveAdapter: new NicAdapterRef("{GUID-ADAPTER-A}", "Ethernet", true, true, false),
        LinkSpeedMbps: 1000,
        CurrentMtuBytes: 1500,
        PowerSchemeGuid: "{GUID-POWER}",
        PowerSchemeName: "Сбалансированная",
        TcpAutoTuningLevel: "normal",
        CongestionProvider: "cubic",
        IsRssEnabled: false,
        IsRscEnabled: true,
        IsEcnEnabled: false,
        IsNagleDisabledOnActiveNic: false,
        IsGameModeEnabled: true,
        IsDeliveryOptimizationP2pEnabled: true,
        IsSystemRestoreEnabledOnSystemDrive: true,
        IsTimerResolutionRaised: true,
        ConfiguredDnsServers: new[] { "192.168.1.1" },
        AttachedNdisFilters: new[] { "Npcap" },
        UnresolvedFields: Array.Empty<string>());

    public int CallCount { get; private set; }

    public Task<SystemStateReading> ReadAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(Reading);
    }
}

/// <summary>Бенчмарк DNS без сети: тест сам задаёт «измеренные» ответы резолверов.</summary>
public sealed class FakeDnsBenchmarkRunner : IDnsBenchmarkRunner
{
    private readonly IReadOnlyList<DnsResolverBenchmarkResult> _results;

    public FakeDnsBenchmarkRunner(IReadOnlyList<DnsResolverBenchmarkResult> results) => _results = results;

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<DnsResolverBenchmarkResult>> RunAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_results);
    }
}
