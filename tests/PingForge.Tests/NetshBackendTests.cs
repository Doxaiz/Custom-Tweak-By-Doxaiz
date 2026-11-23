using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// netsh-бэкенд твиков: глобальные параметры TCP.
///
/// Эти твики не ходят в реестр — они выполняют `netsh interface tcp set global`, а потом перечитывают
/// `show global` и сверяют значение. Здесь проверяется, что запись идёт именно тем параметром, который
/// назван в каталоге, что чужое значение в командную строку не проскочит, и что «система не подтвердила»
/// остаётся отказом, а не зелёным результатом.
///
/// Живая половина — в `tools/c7-live-checks.ps1` (шаг «netsh-бэкенд») и в самопроверке программы
/// (чтение свойств NIC): там значения проверяются сторонним наблюдателем, а не ответом службы.
/// </summary>
public sealed class NetshBackendTests
{
    private const string EnglishGlobalOutput = """
        TCP Global Parameters
        ----------------------------------------------
        Receive-Side Scaling State          : enabled
        Receive Window Auto-Tuning Level    : restricted
        Add-On Congestion Control Provider  : default
        ECN Capability                      : disabled
        RFC 1323 Timestamps                 : disabled
        Max SYN Retransmissions             : 2
        """;

    private static TweakCatalog CatalogWith(FakeCommandPort command) => new(new TweakPlatformPorts(
        Registry: new FakeRegistryPort(),
        NicProperties: new FakeNicPropertyPort(),
        Command: command,
        ActiveAdapter: new FakeActiveAdapterResolver(),
        MtuAdvice: new FakeMtuAdvice(),
        DnsAdvice: new FakeDnsAdvice(),
        NicOptions: new FakeNicOptions("128", "256", "512", "1024", "2048")));

    private static SnapshotItemEntry Entry(string code, string planned) =>
        new(code, TweakMechanismKind.NetshCommand, TweakTarget.Global("netsh"), false, null, TweakValueDataType.String, planned);

    [Fact]
    public async Task Применение_пишет_ровно_тот_параметр_что_назван_в_каталоге()
    {
        string current = "restricted";
        var command = new FakeCommandPort
        {
            Handler = (_, arguments) =>
            {
                if (arguments.Contains("set"))
                {
                    // Служба пишет значение, система его принимает.
                    string assignment = arguments[^1];
                    current = assignment.Contains('=') ? assignment[(assignment.IndexOf('=') + 1)..] : current;
                    return CommandResult.Ok(string.Empty);
                }

                return CommandResult.Ok(EnglishGlobalOutput.Replace(
                    "Receive Window Auto-Tuning Level    : restricted",
                    $"Receive Window Auto-Tuning Level    : {current}",
                    StringComparison.Ordinal));
            }
        };

        ITweak tweak = CatalogWith(command).GetByCode("TCP_AUTOTUNING_NORMAL")!;
        TweakOperationResult result = await tweak.ApplyAsync(Entry("TCP_AUTOTUNING_NORMAL", "normal"));

        Assert.Equal(TweakOutcome.VerifiedEffective, result.Outcome);
        Assert.Contains(
            command.Invocations,
            call => call.Contains("interface tcp set global autotuninglevel=normal", StringComparison.Ordinal));
        Assert.Equal("normal", current);
    }

    [Fact]
    public async Task Значение_которое_не_похоже_на_параметр_netsh_не_выполняется()
    {
        var command = new FakeCommandPort { Handler = (_, _) => CommandResult.Ok(EnglishGlobalOutput) };
        ITweak tweak = CatalogWith(command).GetByCode("TCP_AUTOTUNING_NORMAL")!;

        TweakOperationResult result = await tweak.ApplyAsync(
            Entry("TCP_AUTOTUNING_NORMAL", "normal & shutdown /s /t 0"));

        Assert.Equal(TweakOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(command.Invocations, call => call.Contains("shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Система_не_подтвердила_значение_это_отказ_а_не_успех()
    {
        // netsh «принимает» команду, но значение не меняется: так бывает при политике или старом стеке.
        var command = new FakeCommandPort
        {
            Handler = (_, arguments) => CommandResult.Ok(
                arguments.Contains("set") ? string.Empty : EnglishGlobalOutput)
        };

        ITweak tweak = CatalogWith(command).GetByCode("TCP_AUTOTUNING_NORMAL")!;
        TweakOperationResult result = await tweak.ApplyAsync(Entry("TCP_AUTOTUNING_NORMAL", "normal"));

        Assert.Equal(TweakOutcome.Failed, result.Outcome);
        Assert.Contains("не подтвердила", result.FailureReasonRu, StringComparison.OrdinalIgnoreCase);
    }
}
