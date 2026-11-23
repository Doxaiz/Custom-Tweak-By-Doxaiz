using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PingForge.App.ViewModels;

/// <summary>
/// Что известно о подписи файла: подписан он или нет и чьим сертификатом.
/// <paramref name="ProblemRu"/> заполняется, только если подпись прочитать не удалось
/// (например, файл вообще не Portable Executable) — «не прочитали» и «не подписано» это разные вещи.
/// </summary>
public sealed record AppSignatureState(bool IsSigned, string? PublisherName, string? ProblemRu)
{
    public static AppSignatureState Signed(string publisherName) => new(true, publisherName, null);

    public static AppSignatureState Unsigned() => new(false, null, null);

    public static AppSignatureState Unreadable(string problemRu) => new(false, null, problemRu);
}

/// <summary>
/// Подпись собственного файла — без обещаний, которых нет.
///
/// Раньше программа говорила «не подписан сертификатом EV, поэтому SmartScreen предупреждает» — и это
/// неправда дважды: во-первых, тип сертификата на предупреждение больше не влияет (Microsoft убрала
/// автоматическое доверие для EV в 2024 году), во-вторых, состояние подписи — факт о конкретном файле,
/// а не свойство сборки. Поэтому здесь файл действительно читается, а текст строится по факту.
///
/// Основание: Microsoft Learn, «SmartScreen reputation for Windows app developers» и
/// «Code signing options for Windows app developers» (см. docs/ИСТОЧНИКИ.md).
/// </summary>
public static class AppSignature
{
    /// <summary>Прочитать подпись файла. Ничего не меняет и ничего не скачивает.</summary>
    public static AppSignatureState Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return AppSignatureState.Unreadable("файл не найден");
        }

        if (!OperatingSystem.IsWindows())
        {
            // Подпись Authenticode — свойство PE-файла, а читает её Windows (crypt32). Не выдумываем ответ.
            return AppSignatureState.Unreadable("чтение подписи PE-файла доступно только в Windows");
        }

        try
        {
            // SYSLIB0057 советует X509CertificateLoader, но он загружает сертификат из файла сертификата,
            // а не подпись Authenticode из PE-файла: замены этому вызову в .NET нет. Оставляем его осознанно.
#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            string subject = certificate.Subject?.Trim() ?? string.Empty;
            return AppSignatureState.Signed(subject.Length > 0 ? subject : "издатель не назван");
        }
        catch (CryptographicException)
        {
            // Самая частая причина — подписи просто нет. Это не ошибка и не сбой.
            return AppSignatureState.Unsigned();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return AppSignatureState.Unreadable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Что сказать человеку. Текст называет только то, что подтверждено документацией Microsoft:
    /// что именно увидит Windows и что подпись даёт, а что — нет.
    /// </summary>
    public static string DescribeRu(AppSignatureState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.ProblemRu is not null)
        {
            return $"Подпись файла прочитать не удалось ({state.ProblemRu}). Это не значит, что файл подписан " +
                "или не подписан: проверить можно вручную — в PowerShell «Get-AuthenticodeSignature <файл>».";
        }

        if (!state.IsSigned)
        {
            return "Файл не подписан сертификатом. Поэтому Windows покажет «Windows protected your PC» " +
                "(«Подробнее» → «Выполнить в любом случае»), а в запросе прав издателем будет «Неизвестный издатель». " +
                "Сертификат EV этого не исправил бы: с 2024 года тип сертификата на предупреждение не влияет — " +
                "SmartScreen смотрит на накопленную репутацию загрузок. В Windows 11 при включённом Smart App Control " +
                "неподписанный файл не запускается вовсе, и обходить эту защиту мы не советуем: проверяйте SHA-256 " +
                "из SHA256SUMS.txt и берите файл только из раздела Releases.";
        }

        return $"Файл подписан: {state.PublisherName}. Подпись подтверждает издателя и то, что файл не изменён " +
            "после подписи, но она не отменяет предупреждение SmartScreen для нового файла: доверие набирается " +
            "загрузками, и тип сертификата (OV или EV) на это не влияет с 2024 года.";
    }
}
