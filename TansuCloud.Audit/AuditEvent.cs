// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TansuCloud.Audit;

/// <summary>
/// Represents an immutable audit event for compliance and security tracking.
/// </summary>
[Table("audit_events")]
public sealed class AuditEvent
{
    /// <summary>
    /// Unique identifier using UUID v7 for time-ordered IDs.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Event timestamp in UTC.
    /// </summary>
    [Required]
    [Column("when_utc")]
    public DateTimeOffset WhenUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Tenant identifier (null for system-level events).
    /// </summary>
    [Column("tenant_id")]
    [MaxLength(256)]
    public string? TenantId { get; init; }

    /// <summary>
    /// Event category (e.g., "authentication", "database.provisioning").
    /// </summary>
    [Required]
    [Column("category")]
    [MaxLength(128)]
    public required string Category { get; init; }

    /// <summary>
    /// Specific action (e.g., "login.success", "tenant.create").
    /// </summary>
    [Required]
    [Column("action")]
    [MaxLength(128)]
    public required string Action { get; init; }

    /// <summary>
    /// Source service (e.g., "identity", "database", "storage").
    /// </summary>
    [Required]
    [Column("service")]
    [MaxLength(64)]
    public required string Service { get; init; }

    /// <summary>
    /// User ID or system identifier performing the action.
    /// </summary>
    [Column("subject")]
    [MaxLength(256)]
    public string? Subject { get; init; }

    /// <summary>
    /// Request correlation ID (X-Correlation-ID) for tracing across services.
    /// </summary>
    [Column("correlation_id")]
    [MaxLength(128)]
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Additional context as JSON (no PII; use for non-sensitive metadata).
    /// </summary>
    [Column("metadata", TypeName = "jsonb")]
    public string? Metadata { get; init; }

    /// <summary>
    /// OpenTelemetry trace ID for distributed tracing correlation.
    /// </summary>
    [Column("trace_id")]
    [MaxLength(32)]
    public string? TraceId { get; init; }

    /// <summary>
    /// OpenTelemetry span ID for distributed tracing correlation.
    /// </summary>
    [Column("span_id")]
    [MaxLength(16)]
    public string? SpanId { get; init; }
} // End of Class AuditEvent
