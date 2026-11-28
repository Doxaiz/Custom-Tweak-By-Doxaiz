using PingForge.Core.Abstractions;
using PingForge.Core.Models;
using PingForge.Tweaks;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Настройки ветки пользователя из службы.
///
/// Служба работает под LocalSystem, поэтому её «текущий пользователь» — это профиль системы.
/// Записать туда скорость мыши, задержку меню или отключение ускорения указателя и рапортовать
/// «применено» — значит показать изменение, которого человек в Windows не увидит: проверка
/// подтвердила бы запись в собственном профиле службы.
///
/// Здесь проверяется договор: если профиль пользователя привязать не к кому, твик честно
/// отказывается с причиной и не пишет ничего; машинные (HKLM) настройки при этом работают как обычно.
/// </summary>
public sealed class UserHiveHonestyTests
{
    private const string UserHiveProblem = "профиль пользователя определить не удалось (тест)";

    private static TweakCatalog CatalogWith(FakeRegistryPort port) => new(new TweakPlatformPorts(
        Registry: port,
        NicProperties: new FakeNicPropertyPort(),
        Command: new FakeCommandPort(),
        ActiveAdapter: new FakeActiveAdapterResolver(),
        MtuAdvice: new FakeMtuAdvice(),
        DnsAdvice: new FakeDnsAdvice(),
        NicOptions: new FakeNicOptions("128", "256", "512", "1024", "2048")));

    private static SnapshotItemEntry Entry(string code, TweakValueDataType dataType, string planned) =>
        new(code, TweakMechanismKind.Registry, TweakTarget.Global("registry"), false, null, dataType, planned);

    [Fact]
    public async Task Настройка_пользователя_отказывается_если_профиль_неизвестен()
    {
        var port = new FakeRegistryPort { UserHiveProblem = UserHiveProblem };
        ITweak tweak = CatalogWith(port).GetByCode("MENU_SHOW_DELAY_ZERO")!;

        (TweakTarget Target, TweakSupport Support) resolved = await tweak.ResolveTargetAsync();
        TweakOperationResult result = await tweak.ApplyAsync(Entry("MENU_SHOW_DELAY_ZERO", TweakValueDataType.String, "0"));

        Assert.False(resolved.Support.IsSupported);
        Assert.Contains("профиль", resolved.Support.ReasonRu, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(TweakOutcome.SkippedUnsupported, result.Outcome);
        Assert.Contains(UserHiveProblem, result.FailureReasonRu, StringComparison.Ordinal);
        // Главное: ни одной записи. Молчаливая запись в профиль службы — это и есть то враньё, от которого уходим.
        Assert.Equal(0, port.WriteCount);
    }

    [Fact]
    public async Task Машинная_настройка_не_зависит_от_профиля_пользователя()
    {
        var port = new FakeRegistryPort { UserHiveProblem = UserHiveProblem };
        ITweak tweak = CatalogWith(port).GetByCode("MMCSS_NETWORK_THROTTLING")!;

        (TweakTarget Target, TweakSupport Support) resolved = await tweak.ResolveTargetAsync();
        TweakOperationResult result = await tweak.ApplyAsync(
            Entry("MMCSS_NETWORK_THROTTLING", TweakValueDataType.DWord, "4294967295"));

        Assert.True(resolved.Support.IsSupported);
        Assert.NotEqual(TweakOutcome.SkippedUnsupported, result.Outcome);
        // Для этого значения документация требует перезагрузку, поэтому исход честный:
        // запись подтверждена, эффект наступит после перезапуска. Фальшивого «работает» здесь нет.
        Assert.Contains(result.Outcome, new[] { TweakOutcome.VerifiedEffective, TweakOutcome.WrittenPendingRestart });
        Assert.Equal(1, port.WriteCount);
    }

    [Fact]
    public async Task Настройка_пользователя_применяется_когда_профиль_известен()
    {
        var port = new FakeRegistryPort();
        ITweak tweak = CatalogWith(port).GetByCode("MENU_SHOW_DELAY_ZERO")!;

        TweakOperationResult result = await tweak.ApplyAsync(Entry("MENU_SHOW_DELAY_ZERO", TweakValueDataType.String, "0"));

        Assert.Equal(TweakOutcome.VerifiedEffective, result.Outcome);
        Assert.Equal("0", port.Get(new RegistryLocation(RegistryRootKind.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay")));
    }

    [Fact]
    public void Контекст_ветки_пользователя_задаётся_на_время_команды_и_снимается()
    {
        Assert.Null(UserHiveContext.CurrentSid);

        using (UserHiveContext.Use("S-1-5-21-1-2-3-1001"))
        {
            Assert.Equal("S-1-5-21-1-2-3-1001", UserHiveContext.CurrentSid);

            using (UserHiveContext.Use("S-1-5-21-1-2-3-1005"))
            {
                Assert.Equal("S-1-5-21-1-2-3-1005", UserHiveContext.CurrentSid);
            }

            Assert.Equal("S-1-5-21-1-2-3-1001", UserHiveContext.CurrentSid);
        }

        Assert.Null(UserHiveContext.CurrentSid);
    }

    [Fact]
    public void Служебные_SID_не_считаются_профилем_человека()
    {
        // Система, служба, сеть, пакеты приложений — это не ветка пользователя.
        Assert.False(UserHiveContext.IsUsableUserSid("S-1-5-18"));
        Assert.False(UserHiveContext.IsUsableUserSid("S-1-5-19"));
        Assert.False(UserHiveContext.IsUsableUserSid("S-1-5-20"));
        Assert.False(UserHiveContext.IsUsableUserSid("S-1-5-80-3880718306-3832830129-1677859214-2598158968-1052248003"));
        Assert.False(UserHiveContext.IsUsableUserSid("не определён"));
        Assert.False(UserHiveContext.IsUsableUserSid(string.Empty));
        Assert.False(UserHiveContext.IsUsableUserSid(null));

        // Локальная или доменная учётная запись и учётная запись Microsoft Entra — годятся.
        Assert.True(UserHiveContext.IsUsableUserSid("S-1-5-21-100-200-300-1001"));
        Assert.True(UserHiveContext.IsUsableUserSid("S-1-12-1-1234567890-1234567890-1234567890-1234567890"));
    }

    [Fact]
    public void Служебный_SID_в_контексте_не_подменяет_профиль()
    {
        using (UserHiveContext.Use("S-1-5-18"))
        {
            Assert.Null(UserHiveContext.CurrentSid);
        }
    }
}
