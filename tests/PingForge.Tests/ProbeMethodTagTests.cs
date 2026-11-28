using Microsoft.Data.Sqlite;
using PingForge.Core.Models;
using PingForge.Data;
using PingForge.Data.Repositories;
using PingForge.Orchestration.Realtime;
using PingForge.Service;
using Xunit;

namespace PingForge.Tests;

/// <summary>
/// Журнал сессий обязан называть способ, которым получены числа. Раньше в колонки способа
/// писалось одно и то же слово на любую сессию — и это перестало быть правдой, когда способов стало четыре.
/// </summary>
public sealed class ProbeMethodTagTests
{
    [Fact]
    public void Каждый_способ_замера_получает_свою_метку()
    {
        string liveTcp = ProbeMethodTags.Tag(ProbeMethodUsed.LiveTcpEstats);
        string liveUdp = ProbeMethodTags.Tag(ProbeMethodUsed.LiveUdpPassiveFlow);
        string icmp = ProbeMethodTags.Tag(ProbeMethodUsed.ActiveIcmpEcho);
        string handshake = ProbeMethodTags.Tag(ProbeMethodUsed.ActiveTcpHandshake);

        Assert.Equal("live-tcp", liveTcp);
        Assert.Equal("live-udp", liveUdp);
        Assert.Equal("icmp", icmp);
        Assert.Equal("tcp-handshake", handshake);
        Assert.Equal(4, new[] { liveTcp, liveUdp, icmp, handshake }.Distinct(StringComparer.Ordinal).Count());

        // Устаревшая строка не должна выдаваться за способ ни одного из четырёх замеров.
        Assert.DoesNotContain(
            ProbeMethodTags.LegacyLiveFlow,
            new[] { liveTcp, liveUdp, icmp, handshake },
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Неизмеренный_путь_не_получает_способа_живого_потока()
    {
        Assert.Equal(ProbeMethodTags.Unmeasurable, ProbeMethodTags.Tag(ProbeMethodUsed.Unmeasurable));
        Assert.Equal("не измерено", ProbeMethodTags.Ru(ProbeMethodTags.Unmeasurable));
    }

    [Fact]
    public void Живой_udp_называется_отдельно_от_живого_tcp()
    {
        Assert.Contains("TCP", ProbeMethodTags.Ru(ProbeMethodTags.LiveTcp), StringComparison.Ordinal);
        Assert.Contains("пассивные метки", ProbeMethodTags.Ru(ProbeMethodTags.LiveUdp), StringComparison.Ordinal);
        Assert.NotEqual(ProbeMethodTags.Ru(ProbeMethodTags.LiveTcp), ProbeMethodTags.Ru(ProbeMethodTags.LiveUdp));
    }

    [Fact]
    public void Старая_запись_честно_называется_старой()
    {
        string ru = ProbeMethodTags.Ru(ProbeMethodTags.LegacyLiveFlow);

        Assert.Contains("старая запись", ru, StringComparison.Ordinal);
        Assert.NotEqual(ProbeMethodTags.Ru(ProbeMethodTags.LiveTcp), ru);
        Assert.NotEqual(ProbeMethodTags.Ru(ProbeMethodTags.LiveUdp), ru);
    }

    [Fact]
    public void Незнакомая_метка_не_притворяется_известной()
    {
        string ru = ProbeMethodTags.Ru("tcpx-info");

        Assert.Contains("незнакомый", ru, StringComparison.Ordinal);
        Assert.Contains("tcpx-info", ru, StringComparison.Ordinal);
    }

    [Fact]
    public void Пустая_метка_значит_что_способ_не_записан()
    {
        Assert.Equal("способ замера не записан", ProbeMethodTags.Ru(null));
        Assert.Equal("способ замера не записан", ProbeMethodTags.Ru(string.Empty));
        Assert.Equal("способ замера не записан", ProbeMethodTags.DescribePairRu(string.Empty, null));
    }

    [Fact]
    public void Пара_с_разными_способами_называет_оба()
    {
        string ru = ProbeMethodTags.DescribePairRu(ProbeMethodTags.Icmp, ProbeMethodTags.LiveUdp);

        Assert.Contains("«до»", ru, StringComparison.Ordinal);
        Assert.Contains("«после»", ru, StringComparison.Ordinal);
        Assert.Contains("ICMP", ru, StringComparison.Ordinal);
        Assert.Contains("пассивные метки", ru, StringComparison.Ordinal);
    }

    [Fact]
    public void Одинаковый_способ_в_паре_называется_один_раз()
    {
        string ru = ProbeMethodTags.DescribePairRu(ProbeMethodTags.LiveUdp, ProbeMethodTags.LiveUdp);

        Assert.Equal("способ: " + ProbeMethodTags.Ru(ProbeMethodTags.LiveUdp), ru);
        Assert.DoesNotContain("«до»", ru, StringComparison.Ordinal);
    }

    [Fact]
    public void Разные_способы_в_паре_видны_как_разные()
    {
        Assert.True(ProbeMethodTags.PairDiffers(ProbeMethodTags.Icmp, ProbeMethodTags.LiveUdp));
        Assert.False(ProbeMethodTags.PairDiffers(ProbeMethodTags.Icmp, ProbeMethodTags.Icmp));

        // «После» не измерено — это отсутствие замера, а не другой способ.
        Assert.False(ProbeMethodTags.PairDiffers(ProbeMethodTags.Icmp, ProbeMethodTags.Unmeasurable));
        Assert.False(ProbeMethodTags.PairDiffers(string.Empty, ProbeMethodTags.LiveUdp));
    }

    [Fact]
    public async Task Способы_переживают_круговой_путь_в_базу()
    {
        string path = TempDatabaseFile.Create("pf_tags_roundtrip");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var repository = new OptimizationSessionRepository(database);

            await repository.SaveAsync(Record("tags", ProbeMethodTags.Icmp, ProbeMethodTags.LiveUdp));

            OptimizationSessionRecord stored = Assert.Single(await repository.GetRecentAsync(10));
            Assert.Equal(ProbeMethodTags.Icmp, stored.BaselineMethodTag);
            Assert.Equal(ProbeMethodTags.LiveUdp, stored.FinalMethodTag);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    [Fact]
    public async Task Незаписанный_способ_становится_не_измерено_а_не_пустой_строкой()
    {
        string path = TempDatabaseFile.Create("pf_tags_unknown");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var repository = new OptimizationSessionRepository(database);

            await repository.SaveAsync(Record("unknown-tags", string.Empty, string.Empty));

            await using SqliteConnection connection = await database.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT baseline_probe_method, optimized_probe_method FROM route_optimization_sessions WHERE session_id = 'unknown-tags'";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(ProbeMethodTags.Unmeasurable, reader.GetString(0));
            Assert.Equal(ProbeMethodTags.Unmeasurable, reader.GetString(1));
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    [Fact]
    public async Task Старые_записи_с_прежней_строкой_читаются_и_называются_старыми()
    {
        string path = TempDatabaseFile.Create("pf_tags_legacy");
        try
        {
            var database = new SqliteDatabaseInitializer(path);
            await database.EnsureInitializedAsync();
            var repository = new OptimizationSessionRepository(database);

            await repository.SaveAsync(Record("legacy", ProbeMethodTags.LegacyLiveFlow, ProbeMethodTags.LegacyLiveFlow));

            OptimizationSessionRecord stored = Assert.Single(await repository.GetRecentAsync(10));
            Assert.Equal(ProbeMethodTags.LegacyLiveFlow, stored.BaselineMethodTag);
            Assert.Contains("старая запись", ProbeMethodTags.DescribePairRu(stored.BaselineMethodTag, stored.FinalMethodTag), StringComparison.Ordinal);
        }
        finally
        {
            TempDatabaseFile.Delete(path);
        }
    }

    [Fact]
    public void Начатая_сессия_берёт_способ_из_снимка()
    {
        OptimizationSessionRecord record = OptimizationSessionMapping.FromStart(
            "start-tags", DateTime.UtcNow, Snapshot(ProbeMethodTags.Icmp, ProbeMethodTags.Icmp));

        Assert.Equal(ProbeMethodTags.Icmp, record.BaselineMethodTag);
        Assert.Equal(string.Empty, record.FinalMethodTag);
    }

    [Fact]
    public void Завершённая_сессия_берёт_способы_из_отчёта()
    {
        OptimizationSessionRecord started = OptimizationSessionMapping.FromStart(
            "stop-tags", DateTime.UtcNow, Snapshot(ProbeMethodTags.Icmp, string.Empty));

        OptimizationSessionRecord finished = OptimizationSessionMapping.Complete(
            started, Report(ProbeMethodTags.Icmp, ProbeMethodTags.TcpHandshake), DateTime.UtcNow);

        Assert.Equal(ProbeMethodTags.Icmp, finished.BaselineMethodTag);
        Assert.Equal(ProbeMethodTags.TcpHandshake, finished.FinalMethodTag);
        Assert.Contains("«после»", ProbeMethodTags.DescribePairRu(finished.BaselineMethodTag, finished.FinalMethodTag), StringComparison.Ordinal);
    }

    [Fact]
    public void Отчёт_без_своей_метки_не_затирает_способ_базовой_линии()
    {
        OptimizationSessionRecord started = OptimizationSessionMapping.FromStart(
            "keep-tags", DateTime.UtcNow, Snapshot(ProbeMethodTags.LiveTcp, string.Empty));

        OptimizationSessionRecord finished = OptimizationSessionMapping.Complete(
            started, Report(string.Empty, string.Empty), DateTime.UtcNow);

        Assert.Equal(ProbeMethodTags.LiveTcp, finished.BaselineMethodTag);
        Assert.Equal(string.Empty, finished.FinalMethodTag);
    }

    private static OptimizationSessionRecord Record(string id, string baselineTag, string finalTag) =>
        new(
            SessionId: id,
            StartedUtc: new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc),
            StoppedUtc: new DateTime(2026, 11, 15, 12, 20, 0, DateTimeKind.Utc),
            GameTitleRu: "Fortnite",
            ProcessExeName: "FortniteClient-Win64-Shipping.exe",
            Endpoint: "203.0.113.9",
            IsMeasurable: true,
            BaselineRttMs: 120,
            FinalRttMs: 95,
            ImprovementMs: 25,
            Actions: [],
            RollbackState: SessionRollbackStates.EverythingReverted,
            IsCompleted: true,
            Outcome: SessionOutcomes.Completed,
            StatusNoteRu: "Сессия завершена.",
            BaselineMethodTag: baselineTag,
            FinalMethodTag: finalTag);

    private static RealtimeSnapshot Snapshot(string baselineTag, string finalTag) =>
        new(
            RealtimeOptimizationState.ActiveMonitoring,
            "Идёт наблюдение.",
            "Fortnite",
            "203.0.113.9",
            BaselineRttMs: 120,
            CurrentRttMs: 100,
            FloorRttMs: 90,
            CurrentJitterMs: 2,
            CurrentLossPercent: 0,
            ImprovementMs: 20,
            QualityLabelRu: "Хорошо",
            AppliedActions: [],
            Warnings: [],
            UpdatedUtc: DateTime.UtcNow,
            BaselineMethodTag: baselineTag,
            FinalMethodTag: finalTag);

    private static RealtimeStopReport Report(string baselineTag, string finalTag) =>
        new(
            GameTitleRu: "Fortnite",
            Endpoint: "203.0.113.9",
            BaselineRttMs: 120,
            FinalRttMs: 95,
            FloorRttMs: 90,
            ImprovementMs: 25,
            QualityLabelRu: "Хорошо",
            Duration: TimeSpan.FromMinutes(20),
            AppliedActionTitles: [],
            RevertMessagesRu: [],
            EverythingReverted: true,
            HonestSummaryRu: "Свои изменения сняты.",
            BaselineMethodTag: baselineTag,
            FinalMethodTag: finalTag);
}
