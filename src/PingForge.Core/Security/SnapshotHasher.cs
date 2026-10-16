using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PingForge.Core.Models;

namespace PingForge.Core.Security;

/// <summary>
/// Канонический SHA-256 по содержимому снапшота.
/// Правила (зафиксированы, менять нельзя без миграции):
///   • элементы сортируются по TweakCode (Ordinal);
///   • порядок полей фиксирован;
///   • кодировка UTF-8 без BOM, переводы строк нормализуются в LF.
/// </summary>
public static class SnapshotHasher
{
    public static string ComputeCanonicalHash(string snapshotId, IEnumerable<SnapshotItemEntry> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(items);

        string json = SerializeCanonicalJson(snapshotId, items);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static string SerializeCanonicalJson(string snapshotId, IEnumerable<SnapshotItemEntry> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            writer.WriteString("snapshot_id", snapshotId);
            writer.WritePropertyName("items");
            writer.WriteStartArray();

            foreach (SnapshotItemEntry item in items.OrderBy(i => i.TweakCode, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("tweak_code", item.TweakCode);
                writer.WriteString("mechanism_kind", item.Mechanism.ToString());
                writer.WriteString("resource_locator", item.ResourceLocator);
                writer.WriteString("adapter_guid", item.AdapterInterfaceGuid ?? string.Empty);
                writer.WriteString("adapter_display_name", item.AdapterDisplayName ?? string.Empty);
                writer.WriteBoolean("existed_before", item.ExistedBefore);

                if (item.CapturedRawValue is null)
                {
                    writer.WriteNull("captured_raw_value");
                }
                else
                {
                    writer.WriteString("captured_raw_value", NormalizeLf(item.CapturedRawValue));
                }

                writer.WriteString("value_data_type", item.ValueDataType.ToString());
                writer.WriteString("planned_value", NormalizeLf(item.PlannedNewValue));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return NormalizeLf(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Проверяет целостность снапшота: возвращает false при любом расхождении.</summary>
    public static bool VerifyIntegrity(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string actual = ComputeCanonicalHash(snapshot.SnapshotId, snapshot.Entries);
        return string.Equals(actual, snapshot.PayloadSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLf(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");
}
