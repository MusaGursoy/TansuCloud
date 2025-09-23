// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TansuCloud.Observability.Auditing;

public static class AuditHelpers
{
    /// <summary>
    /// Build a redacted JsonDocument from a source object using an allowlist of property names.
    /// Non-allowlisted keys are dropped. Values are serialized with System.Text.Json defaults.
    /// </summary>
    public static JsonDocument RedactToJson(object source, IEnumerable<string> allowlist)
    {
        var allow = new HashSet<string>(
            allowlist ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase
        );
        var el = JsonSerializer.SerializeToElement(source);
        if (el.ValueKind != JsonValueKind.Object)
        {
            var wrapped = new Dictionary<string, JsonElement> { ["value"] = el };
            var json = JsonSerializer.Serialize(wrapped);
            return JsonDocument.Parse(json);
        }
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            if (allow.Contains(prop.Name))
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }
        var str = JsonSerializer.Serialize(dict);
        return JsonDocument.Parse(str);
    } // End of Method RedactToJson

    /// <summary>
    /// Compute a stable idempotency key using the natural key: service, WhenUtc (second resolution), subject, action, correlationId, uniqueKey.
    /// </summary>
    public static string ComputeIdempotencyKey(
        string service,
        DateTimeOffset whenUtc,
        string subject,
        string action,
        string correlationId,
        string? uniqueKey
    )
    {
        var whenSec = new DateTimeOffset(
            whenUtc.Year,
            whenUtc.Month,
            whenUtc.Day,
            whenUtc.Hour,
            whenUtc.Minute,
            whenUtc.Second,
            TimeSpan.Zero
        );
        var input = string.Join(
            "|",
            service,
            whenSec.ToUnixTimeSeconds().ToString(),
            subject,
            action,
            correlationId,
            uniqueKey ?? string.Empty
        );
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    } // End of Method ComputeIdempotencyKey
} // End of Class AuditHelpers
