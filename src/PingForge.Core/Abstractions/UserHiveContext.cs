namespace PingForge.Core.Abstractions;

/// <summary>
/// Чья ветка «пользователя» имеется в виду при работе с настройками HKCU.
///
/// Служба работает под LocalSystem, и её собственный HKCU — это профиль системы, а не профиль
/// человека. Настройка, записанная туда, выглядит «применённой» для проверки (служба читает свой же
/// раздел), но в интерфейсе пользователя её нет: обои, скорость мыши и задержка меню остаются прежними.
/// Поэтому тот, кто прислал команду, передаёт свой SID, а ветка пользователя читается и пишется
/// как HKEY_USERS\&lt;SID&gt; — от имени запросившего, а не от имени системы.
/// </summary>
public static class UserHiveContext
{
    private static readonly AsyncLocal<string?> SidOfCurrentCommand = new();

    /// <summary>SID пользователя, от имени которого выполняется текущая команда, или null.</summary>
    public static string? CurrentSid => SidOfCurrentCommand.Value;

    /// <summary>
    /// Годится ли SID как ветка человека. Служебные SID (система, служба, сеть, пакеты приложений)
    /// не годятся: запись в них — это не настройка пользователя, а тихая правка чужого профиля.
    /// </summary>
    public static bool IsUsableUserSid(string? sid) =>
        !string.IsNullOrWhiteSpace(sid)
        && (sid!.StartsWith(@"S-1-5-21-", StringComparison.Ordinal)
            || sid.StartsWith(@"S-1-12-1-", StringComparison.Ordinal));

    /// <summary>Задать SID на время одной команды. Возвращённое освобождать по завершении.</summary>
    public static IDisposable Use(string? sid)
    {
        string? previous = SidOfCurrentCommand.Value;
        SidOfCurrentCommand.Value = IsUsableUserSid(sid) ? sid : null;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SidOfCurrentCommand.Value = _previous;
        }
    }
}
