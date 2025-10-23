// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace TansuCloud.Audit;

/// <summary>
/// Immutable audit event shape for central trail (Task 31 Phase 1).
/// PII-safe: Details must be pre-redacted/allowlisted by the SDK before enqueueing.
/// This is the entity model for EF Core migrations.
/// </summary>
[Table("audit_events")]
public sealed class AuditEvent
{
    [Key]
    [Column("id")]
    public Guid Id { get; init; } = Guid.CreateVersion7();

    [Required]
    [Column("when_utc")]
    public DateTimeOffset WhenUtc { get; init; } = DateTimeOffset.UtcNow;

    [Required]
    [Column("service")]
    [MaxLength(64)]
    public required string Service { get; init; }

    [Required]
    [Column("environment")]
    [MaxLength(64)]
    public required string Environment { get; init; }

    [Required]
    [Column("version")]
    [MaxLength(64)]
    public required string Version { get; init; }

    [Required]
    [Column("tenant_id")]
    [MaxLength(256)]
    public required string TenantId { get; init; }

    [Required]
    [Column("subject")]
    [MaxLength(256)]
    public required string Subject { get; init; }

    [Required]
    [Column("action")]
    [MaxLength(128)]
    public required string Action { get; init; }

    [Required]
    [Column("category")]
    [MaxLength(128)]
    public required string Category { get; init; }

    [Required]
    [Column("route_template")]
    [MaxLength(512)]
    public required string RouteTemplate { get; init; }

    [Required]
    [Column("correlation_id")]
    [MaxLength(128)]
    public required string CorrelationId { get; init; }

    [Required]
    [Column("trace_id")]
    [MaxLength(32)]
    public required string TraceId { get; init; }

    [Required]
    [Column("span_id")]
    [MaxLength(16)]
    public required string SpanId { get; init; }

    // Optional fields
    [Column("client_ip_hash")]
    [MaxLength(64)]
    public string? ClientIpHash { get; init; }

    [Column("user_agent")]
    [MaxLength(512)]
    public string? UserAgent { get; init; }

    [Column("outcome")]
    [MaxLength(64)]
    public string? Outcome { get; init; }

    [Column("reason_code")]
    [MaxLength(128)]
    public string? ReasonCode { get; init; }

    [Column("details", TypeName = "jsonb")]
    public JsonDocument? Details { get; init; }

    [Column("impersonated_by")]
    [MaxLength(256)]
    public string? ImpersonatedBy { get; init; }

    [Column("source_host")]
    [MaxLength(256)]
    public string? SourceHost { get; init; }

    [Column("idempotency_key")]
    [MaxLength(128)]
    public string? IdempotencyKey { get; init; }

    [Column("unique_key")]
    [MaxLength(256)]
    public string? UniqueKey { get; init; }
} // End of Class AuditEvent
