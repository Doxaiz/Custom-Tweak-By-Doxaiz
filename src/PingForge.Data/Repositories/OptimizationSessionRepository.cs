using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PingForge.Data.Repositories;

/// <summary>Чем закончилась сессия оптимизации. «Не завершена» — это честный исход, а не сбой отчёта.</summary>
public static class SessionOutcomes
{
    /// <summary>Сессия завершена: «до» и «после» измерены.</summary>
    public const string Completed = "Completed";

    /// <summary>Сессия оборвалась или «после» не измерено: итог неизвестен, выдумывать его нельзя.</summary>
    public const string NotFinished = "NotFinished";

    /// <summary>Игру так и не нашли: оптимизировать было нечего, замер не состоялся.</summary>
    public const string NoGame = "NoGame";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed, NotFinished, NoGame
    };
}

/// <summary>Что стало с изменениями сессии: снялись полностью, частично или неизвестно.</summary>
public static class SessionRollbackStates
{
    public const string EverythingReverted = "EverythingReverted";
    public const string PartiallyReverted = "PartiallyReverted";
    public const string NotReverted = "NotReverted";

    /// <summary>Пометки нет — например, сессия оборвалась до остановки. Это «неизвестно», а не «снято».</summary>
    public const string Unknown = "Unknown";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EverythingReverted, PartiallyReverted, NotReverted, Unknown
    };
}

/// <summary>
/// Одна сессия живой оптимизации в том виде, в каком она хранится и показывается:
/// когда, какая игра и сервер, «до» и «после», действия, пометка откатa и честный статус.
/// Все числа nullable: не измерено — значит <c>null</c>, а не ноль.
/// </summary>
public sealed record OptimizationSessionRecord(
    string SessionId,
    DateTime StartedUtc,
    DateTime? StoppedUtc,
    string GameTitleRu,
    string ProcessExeName,
    string Endpoint,
    bool IsMeasurable,
    double? BaselineRttMs,
    double? FinalRttMs,
    double? ImprovementMs,
    IReadOnlyList<string> Actions,
    string RollbackState,
    bool IsCompleted,
    string Outcome,
    string StatusNoteRu)
{
    /// <summary>Игра была найдена: есть адрес сервера и название, а не «игра не найдена».</summary>
    public bool HasGame => !string.IsNullOrWhiteSpace(GameTitleRu)
        && !string.IsNullOrWhiteSpace(Endpoint)
        && Outcome != SessionOutcomes.NoGame;
}

/// <summary>
/// История сессий оптимизации в существующей базе <c>PingForge.Data</c> (задача J).
///
/// Правила честности, зашитые в этот класс:
///   • сессия без измеренного «после» сохраняется как незавершённая, а не получает выдуманное улучшение;
///   • «игра не найдена» — отдельный исход <see cref="SessionOutcomes.NoGame"/>, а не пустая строка;
///   • очистка истории удаляет только сессии и не трогает журнал изменений системы;
///   • схема обновляется миграцией (см. <see cref="SqliteDatabaseInitializer"/>), данные не теряются.
/// </summary>
public sealed class OptimizationSessionRepository
{
    private readonly SqliteDatabaseInitializer _database;

    public OptimizationSessionRepository(SqliteDatabaseInitializer database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Сохранить сессию (новую или обновлённую при остановке).</summary>
    public async Task SaveAsync(OptimizationSessionRecord session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!SessionOutcomes.All.Contains(session.Outcome))
        {
            throw new ArgumentException($"Неизвестный исход сессии «{session.Outcome}».", nameof(session));
        }

        if (!SessionRollbackStates.All.Contains(session.RollbackState))
        {
            throw new ArgumentException($"Неизвестная пометка откатa «{session.RollbackState}».", nameof(session));
        }

        string actionsJson = JsonSerializer.Serialize(session.Actions ?? Array.Empty<string>());

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO route_optimization_sessions (
                session_id, process_exe_name, game_title_ru, remote_game_endpoint, selected_mode,
                baseline_probe_method, optimized_probe_method, started_utc, stopped_utc,
                baseline_rtt_avg, optimized_rtt_avg, is_measurable, applied_actions_json,
                rollback_state, outcome, is_completed, verdict_comment
            ) VALUES (
                $id, $exe, $game, $endpoint, 'live',
                'LiveFlow', $optimizedMethod, $started, $stopped,
                $baseline, $final, $measurable, $actions,
                $rollback, $outcome, $completed, $note
            );
            """;
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$exe", session.ProcessExeName ?? string.Empty);
        command.Parameters.AddWithValue("$game", session.GameTitleRu ?? string.Empty);
        command.Parameters.AddWithValue("$endpoint", session.Endpoint ?? string.Empty);
        command.Parameters.AddWithValue("$optimizedMethod",
            session.FinalRttMs is null ? DBNull.Value : "LiveFlow");
        command.Parameters.AddWithValue("$started", ToIso(session.StartedUtc));
        command.Parameters.AddWithValue("$stopped",
            session.StoppedUtc is { } stopped ? ToIso(stopped) : DBNull.Value);
        command.Parameters.AddWithValue("$baseline", (object?)session.BaselineRttMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$final", (object?)session.FinalRttMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$measurable", session.IsMeasurable ? 1 : 0);
        command.Parameters.AddWithValue("$actions", actionsJson);
        command.Parameters.AddWithValue("$rollback", session.RollbackState);
        command.Parameters.AddWithValue("$outcome", session.Outcome);
        command.Parameters.AddWithValue("$completed", session.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$note", session.StatusNoteRu ?? string.Empty);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Последние сессии (новые сверху). Предел — честный: больше 500 не отдаём.</summary>
    public async Task<IReadOnlyList<OptimizationSessionRecord>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 500);

        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, started_utc, stopped_utc, game_title_ru, process_exe_name,
                   remote_game_endpoint, is_measurable, baseline_rtt_avg, optimized_rtt_avg,
                   applied_actions_json, rollback_state, is_completed, outcome, verdict_comment
            FROM route_optimization_sessions
            ORDER BY started_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", safeLimit);

        var result = new List<OptimizationSessionRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            double? baseline = reader.IsDBNull(7) ? null : reader.GetDouble(7);
            double? final = reader.IsDBNull(8) ? null : reader.GetDouble(8);

            result.Add(new OptimizationSessionRecord(
                reader.GetString(0),
                ReadDate(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ReadDate(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6) != 0,
                baseline,
                final,
                baseline is { } before && final is { } after ? Math.Round(before - after, 1) : null,
                ReadActions(reader.GetString(9)),
                reader.GetString(10),
                reader.GetInt32(11) != 0,
                reader.GetString(12),
                reader.IsDBNull(13) ? string.Empty : reader.GetString(13)));
        }

        return result;
    }

    /// <summary>
    /// Очистить историю сессий. Журнал изменений (<c>system_modification_log</c>) не трогается:
    /// «удалить историю замеров» не значит «забыть, что программа меняла в системе».
    /// </summary>
    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM route_optimization_sessions;";
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string ToIso(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ReadDate(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
            ? parsed
            : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    private static IReadOnlyList<string> ReadActions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // Битая строка действий не должна превращать сессию в «действий не было» молча —
            // вызывающий увидит пустой список, но статус и числа сессии останутся настоящими.
            return [];
        }
    }
}
