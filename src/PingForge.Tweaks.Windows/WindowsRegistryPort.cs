using Microsoft.Win32;
using PingForge.Core.Abstractions;
using PingForge.Core.Models;

namespace PingForge.Tweaks.Windows;

/// <summary>
/// Реестр Windows. Все значения ходят в каноническом виде через TweakValueCanonicalFormatter,
/// поэтому «-1», «0xFFFFFFFF» и «4294967295» — одно и то же значение, а не три разных.
///
/// Проект собирается и на Linux (EnableWindowsTargeting) для проверки типов, но выполняется только на Windows.
/// </summary>
public sealed class WindowsRegistryPort : IRegistryPort
{
    private readonly RegistryView _view;

    public WindowsRegistryPort(RegistryView view = RegistryView.Registry64) => _view = view;

    public Task<RegistryValueSnapshot> ReadAsync(RegistryLocation location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ct.ThrowIfCancellationRequested();

        using RegistryKey? root = RegistryKey.OpenBaseKey(ToHive(location.Root), _view);
        using RegistryKey? key = root.OpenSubKey(location.SubKeyPath, writable: false);

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

        using RegistryKey root = RegistryKey.OpenBaseKey(ToHive(location.Root), _view);
        using RegistryKey key = root.CreateSubKey(location.SubKeyPath, writable: true)
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

        using RegistryKey root = RegistryKey.OpenBaseKey(ToHive(location.Root), _view);
        using RegistryKey? key = root.OpenSubKey(location.SubKeyPath, writable: true);

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
            root.DeleteSubKey(location.SubKeyPath, throwOnMissingSubKey: false);
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

    private static RegistryHive ToHive(RegistryRootKind root) => root switch
    {
        RegistryRootKind.LocalMachine => RegistryHive.LocalMachine,
        RegistryRootKind.CurrentUser => RegistryHive.CurrentUser,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "Неизвестная ветка реестра.")
    };
}
