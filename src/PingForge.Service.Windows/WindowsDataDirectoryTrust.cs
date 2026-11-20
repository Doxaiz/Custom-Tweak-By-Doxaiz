using System.Security.AccessControl;
using System.Security.Principal;
using PingForge.Core.Security;

namespace PingForge.Service.Windows;

/// <summary>
/// Чтение прав Windows и перевод их в правило <see cref="DataDirectoryTrust"/>. Здесь только чтение:
/// служба ничего не меняет в чужих правах — она говорит, что нашла, а решение остаётся человеку.
///
/// Права читаются через GetAccessControl у каталога и файла (System.IO.FileSystem.AccessControl).
/// Любая ошибка чтения (нет прав, не Windows, особая файловая система) — это <see cref="DataTrustVerdict.Unknown"/>,
/// а не «всё хорошо»: молча считать проверку пройденной нельзя.
/// </summary>
public static class WindowsDataDirectoryTrust
{
    /// <summary>Проверить каталог данных службы.</summary>
    public static DataTrustReport InspectDirectory(string directory) =>
        Inspect(
            directory,
            path => new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
            "Каталог данных службы");

    /// <summary>Проверить один файл службы (базу, копию). Владелец и права у файла свои.</summary>
    public static DataTrustReport InspectFile(string filePath) =>
        Inspect(
            filePath,
            path => new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
            "Файл службы");

    private static DataTrustReport Inspect(string path, Func<string, FileSystemSecurity> read, string whatRu)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string subject = $"{whatRu} «{path}»";
        if (!PathExists(path))
        {
            // Файла ещё нет — проверять нечего. Это не «доверяю»: скажет вызывающий.
            return new DataTrustReport(DataTrustVerdict.Unknown, $"{subject}: проверить не удалось, его ещё нет.");
        }

        FileSystemSecurity security;
        try
        {
            security = read(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                       or PlatformNotSupportedException or IOException or ArgumentException
                                       or NotSupportedException)
        {
            return new DataTrustReport(
                DataTrustVerdict.Unknown,
                $"{subject}: права прочитать не удалось ({ex.Message}). Это не «всё хорошо» — проверьте вручную (icacls).");
        }

        try
        {
            string? ownerSid = security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                ? owner.Value
                : null;

            var entries = new List<DataAclEntry>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference is not SecurityIdentifier sid)
                {
                    continue;
                }

                entries.Add(new DataAclEntry(
                    NameOf(sid),
                    sid.Value,
                    rule.AccessControlType == AccessControlType.Deny,
                    CanWrite(rule.FileSystemRights)));
            }

            return DataDirectoryTrust.Evaluate(subject, ownerSid, entries);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IdentityNotMappedException)
        {
            return new DataTrustReport(
                DataTrustVerdict.Unknown,
                $"{subject}: записи прав не разобрались ({ex.Message}). Это не «всё хорошо» — проверьте вручную (icacls).");
        }
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>Даёт ли право менять содержимое: запись, создание, удаление, смена прав или владельца.</summary>
    private static bool CanWrite(FileSystemRights rights)
    {
        // Synchronize присутствует почти всегда и ничего не разрешает — её отбрасываем заранее.
        const FileSystemRights anythingThatChanges =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.CreateFiles |
            FileSystemRights.CreateDirectories |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership |
            FileSystemRights.Modify |
            FileSystemRights.FullControl;

        return (rights & anythingThatChanges) != 0;
    }

    /// <summary>Имя по идентификатору; на неизвестном SID честно оставляем сам SID.</summary>
    private static string NameOf(SecurityIdentifier sid)
    {
        if (string.Equals(sid.Value, DataDirectoryTrust.SystemSid, StringComparison.OrdinalIgnoreCase))
        {
            return "Система";
        }

        if (string.Equals(sid.Value, DataDirectoryTrust.AdministratorsSid, StringComparison.OrdinalIgnoreCase))
        {
            return "Администраторы";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }
}
