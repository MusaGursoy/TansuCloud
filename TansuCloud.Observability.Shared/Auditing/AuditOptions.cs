// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Observability.Auditing;

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    // Postgres connection used by the background writer; may be pgcat in dev/prod
    [Required]
    public string? ConnectionString { get; set; }

    // Table name for audit events
    public string Table { get; set; } = "audit_events";

    // Bounded channel capacity. Enqueue drops increment a metric and return false to caller when FullDropEnabled.
    [Range(100, 1_000_000)]
    public int ChannelCapacity { get; set; } = 10_000;

    // When the channel is full, should Enqueue fail fast (true) or block briefly (false)? Default: fail fast.
    public bool FullDropEnabled { get; set; } = true;

    // Maximum details payload size (bytes) after redaction/truncation; larger payloads will be truncated safely.
    [Range(0, 256_000)]
    public int MaxDetailsBytes { get; set; } = 16_384;

    // Hash salt for client IP pseudonymization (HMAC). If null/empty, client IP hash is omitted.
    public string? ClientIpHashSalt { get; set; }
} // End of Class AuditOptions

public interface IAuditLogger
{
    // Enqueue an event (already redacted) for background writing. Returns false if dropped.
    bool TryEnqueue(AuditEvent evt);
} // End of Interface IAuditLogger
