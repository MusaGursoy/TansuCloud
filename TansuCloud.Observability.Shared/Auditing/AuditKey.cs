// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text;

namespace TansuCloud.Observability.Auditing;

/// <summary>
/// Internal helper to compute stable idempotency keys for audit events.
/// </summary>
internal static class AuditKey
{
    /// <summary>
    /// Compute SHA-256 hex of the natural key: Service | WhenUtc(second) | Subject | Action | CorrelationId | UniqueKey.
    /// </summary>
    public static string Compute(AuditEvent e)
    {
        // Use second resolution to avoid duplicate writes from clock jitter within the same second window as specified.
        var sec = e.WhenUtc.ToUnixTimeSeconds();
        var payload = string.Join('|',
            e.Service ?? string.Empty,
            sec.ToString(),
            e.Subject ?? string.Empty,
            e.Action ?? string.Empty,
            e.CorrelationId ?? string.Empty,
            e.UniqueKey ?? string.Empty);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    } // End of Method Compute
} // End of Class AuditKey
