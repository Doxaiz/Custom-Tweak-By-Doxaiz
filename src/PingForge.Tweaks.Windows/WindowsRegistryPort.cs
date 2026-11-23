using System.Security.Principal;
using Microsoft.Win32;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Реестр Windows. Все значения ходят в каноническом виде через TweakValueCanonicalFormatter,
/// поэтому «-1», «0xFFFFFFFF» и «4294967295» — одно и то же значение, а не три разных.
///
/// Отдельная забота — ветка пользователя. Служба работает под LocalSystem: её «текущий пользователь»
/// это профиль системы, поэтому запись «в HKCU» из службы уходила бы в профиль службы, а человек
/// в Windows не видел бы никакой настройки. Поэтому для HKCU адрес профиля берётся явно: SID того,
/// кто прислал команду, а если его нет — SID активного сеанса. Открывается HKEY_USERS\&lt;SID&gt;,
/// то есть ровно та ветка, которую видит сам пользователь.
///
/// Проект собирается и на Linux (EnableWindowsTargeting) для проверки типов, но выполняется только на Windows.
/// </summary>
public sealed class WindowsRegistryPort : IRegistryPort
{
    /// <summary>Что сказать человеку, если ветку пользователя не к кому привязать.</summary>
    public const string UserHiveProblemRu =
        "Настройка живёт в вашем профиле пользователя, а служба работает от имени системы: её собственный HKCU — " +
        "это профиль системы, поэтому запись туда вы бы не увидели. Сейчас профиль определить не удалось " +
        "(у команды нет SID пользователя, и активный сеанс не нашёлся) — молча писать в профиль службы нельзя, " +
        "поэтому настройка не применяется. Запустите программу из своей учётной записи: тогда профиль известен.";

    private static readonly TimeSpan ActiveUserSidLifetime = TimeSpan.FromSeconds(30);

    private static readonly Lazy<bool> RunningAsSystemIdentity = new(() =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return WindowsIdentity.GetCurrent().User?.Value == "S-1-5-18";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or System.Security.SecurityException)
        {
            // Не смогли прочитать личность — значит и о профиле пользователя ничего не утверждаем.
            return false;
        }
    });

    private readonly RegistryView _view;
    private readonly Func<string?> _activeSessionUserSid;
    private readonly object _sidGate = new();
    private string? _cachedActiveUserSid;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public WindowsRegistryPort(RegistryView view = RegistryView.Registry64, Func<string?>? activeSessionUserSid = null)
    {
        _view = view;
        _activeSessionUserSid = activeSessionUserSid ?? InteractiveUserSession.TryGetActiveUserSid;
    }

    public Task<RegistryValueSnapshot> ReadAsync(RegistryLocation location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ct.ThrowIfCancellationRequested();

        (RegistryHive hive, string subKeyPath) = Resolve(location);
        using RegistryKey? root = RegistryKey.OpenBaseKey(hive, _view);
        using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: false);

        if (key is null)
        {
            return Task.FromResult(RegistryValueSnapshot.Missing(TweakValueDataType.DWord));
        }

        object? raw = key.GetValue(location.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return Task.FromResult(RegistryValueSnapshot.Missing(TweakValueDataType.DWord));
        }

        (TweakValueDataType dataType, string? canonical, string? unparsable) = Normalize(key, location.ValueName, raw);
        return Task.FromResult(unparsable is null
            ? new RegistryValueSnapshot(true, canonical, dataType)
            : RegistryValueSnapshot.Unparsable(unparsable, dataType));
    }

    public Task WriteAsync(RegistryLocation location, string canonicalValue, TweakValueDataType dataType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ct.ThrowIfCancellationRequested();

        (RegistryHive hive, string subKeyPath) = Resolve(location);
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, _view);
        using RegistryKey key = root.CreateSubKey(subKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Не удалось открыть раздел реестра «{location.SubKeyPath}» для записи.");

        object value = dataType switch
        {
            TweakValueDataType.DWord => TweakValueCanonicalFormatter.ParseDWord(canonicalValue),
            TweakValueDataType.QWord => TweakValueCanonicalFormatter.ParseQWord(canonicalValue),
            TweakValueDataType.MultiString => canonicalValue.Split('|', StringSplitOptions.TrimEntries),
            TweakValueDataType.Binary => Convert.FromHexString(canonicalValue),
            _ => canonicalValue
        };

        RegistryValueKind kind = dataType switch
        {
            TweakValueDataType.DWord => RegistryValueKind.DWord,
            TweakValueDataType.QWord => RegistryValueKind.QWord,
            TweakValueDataType.MultiString => RegistryValueKind.MultiString,
            TweakValueDataType.Binary => RegistryValueKind.Binary,
            _ => RegistryValueKind.String
        };

        key.SetValue(location.ValueName, value, kind);

        // Сброс кэша записи: без этого проверка «система подтвердила значение» может читать старое значение.
        key.Flush();
        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(RegistryLocation location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ct.ThrowIfCancellationRequested();

        (RegistryHive hive, string subKeyPath) = Resolve(location);
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, _view);
        using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: true);

        // Раздел может быть создан нами и быть пустым — тогда удалять нечего, и это не ошибка.
        if (key is null)
        {
            return Task.CompletedTask;
        }

        key.DeleteValue(location.ValueName, throwOnMissingValue: false);
        key.Flush();

        // Если раздел стал пустым и создавался только под наше значение — убираем и его,
        // но только для ветки интерфейсов Tcpip, чтобы не трогать чужие ключи.
        if (location.SubKeyPath.Contains(@"\Interfaces\", StringComparison.OrdinalIgnoreCase) && key.SubKeyCount == 0 &&
            key.ValueCount == 0)
        {
            key.Dispose();
            root.DeleteSubKey(subKeyPath, throwOnMissingSubKey: false);
        }

        return Task.CompletedTask;
    }

    private static (TweakValueDataType DataType, string? Canonical, string? Unparsable) Normalize(
        RegistryKey key,
        string valueName,
        object raw)
    {
        RegistryValueKind kind = key.GetValueKind(valueName);

        try
        {
            return kind switch
            {
                RegistryValueKind.DWord => (TweakValueDataType.DWord,
                    TweakValueCanonicalFormatter.Format(TweakValueDataType.DWord, raw), null),

                RegistryValueKind.QWord => (TweakValueDataType.QWord,
                    TweakValueCanonicalFormatter.Format(TweakValueDataType.QWord, raw), null),

                RegistryValueKind.MultiString => (TweakValueDataType.MultiString,
                    TweakValueCanonicalFormatter.Format(TweakValueDataType.MultiString, raw), null),

                RegistryValueKind.Binary => (TweakValueDataType.Binary,
                    TweakValueCanonicalFormatter.Format(TweakValueDataType.Binary, raw), null),

                RegistryValueKind.ExpandString or RegistryValueKind.String => (TweakValueDataType.String,
                    TweakValueCanonicalFormatter.Format(TweakValueDataType.String, raw), null),

                _ => (TweakValueDataType.None, null, raw.ToString())
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException)
        {
            // Тип значения в системе не совпал с ожидаемым: не выбрасываем и не подставляем «наугад»,
            // а отдаём сырой текст, чтобы интерфейс показал «значение не распознано».
            return (TweakValueDataType.None, null, raw.ToString());
        }
    }

    /// <summary>
    /// Где действительно лежит значение. Для HKCU из службы это HKEY_USERS\&lt;SID&gt;: та же ветка,
    /// что видит пользователь, но открытая не «от системы в свой профиль».
    /// </summary>
    private (RegistryHive Hive, string SubKeyPath) Resolve(RegistryLocation location)
    {
        if (location.Root == RegistryRootKind.LocalMachine)
        {
            return (RegistryHive.LocalMachine, location.SubKeyPath);
        }

        if (!RunningAsSystemIdentity.Value)
        {
            // Не служба (например, самотест или приложение без службы) — обычный HKCU и есть свой профиль.
            return (RegistryHive.CurrentUser, location.SubKeyPath);
        }

        string? sid = TargetUserSid();
        if (sid is null)
        {
            throw new InvalidOperationException(UserHiveProblemRu);
        }

        return (RegistryHive.Users, sid + "\\" + location.SubKeyPath);
    }

    /// <inheritdoc />
    public string? DescribeUserHiveProblem() =>
        RunningAsSystemIdentity.Value && TargetUserSid() is null ? UserHiveProblemRu : null;

    private string? TargetUserSid()
    {
        string? caller = UserHiveContext.CurrentSid;
        return UserHiveContext.IsUsableUserSid(caller) ? caller : ActiveSessionUserSid();
    }

    private string? ActiveSessionUserSid()
    {
        lock (_sidGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_cachedAt != DateTimeOffset.MinValue && now - _cachedAt < ActiveUserSidLifetime)
            {
                return _cachedActiveUserSid;
            }

            string? sid = _activeSessionUserSid();
            _cachedActiveUserSid = UserHiveContext.IsUsableUserSid(sid) ? sid : null;
            _cachedAt = now;
            return _cachedActiveUserSid;
        }
    }
}
