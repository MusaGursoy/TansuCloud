// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;

namespace TansuCloud.Observability.Auditing;

/// <summary>
/// Immutable audit event shape for central trail (Task 31 Phase 1).
/// PII-safe: Details must be pre-redacted/allowlisted by the SDK before enqueueing.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset WhenUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Service { get; init; } = string.Empty; // e.g., tansu.gateway
    public string Environment { get; init; } = string.Empty; // e.g., Development
    public string Version { get; init; } = string.Empty; // semver/build
    public string TenantId { get; init; } = string.Empty; // normalized tenant id
    public string Subject { get; init; } = "system"; // user id/sub or "system"
    public string Action { get; init; } = string.Empty; // e.g., ProvisionTenant
    public string Category { get; init; } = string.Empty; // e.g., Admin, Security, Storage
    public string RouteTemplate { get; init; } = string.Empty; // normalized template or operation
    public string CorrelationId { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
    public string SpanId { get; init; } = string.Empty;

    // Optional fields
    public string? ClientIpHash { get; init; }
    public string? UserAgent { get; init; } // truncated
    public string? Outcome { get; init; } // e.g., Success, Failure
    public string? ReasonCode { get; init; } // e.g., Unauthorized, ValidationError
    public JsonDocument? Details { get; init; } // allowlisted/redacted content
    public string? ImpersonatedBy { get; init; }
    public string? SourceHost { get; init; }

    // Natural idempotency/de-dupe support
    public string? IdempotencyKey { get; init; } // hash(Service, WhenUtc second, Subject, Action, CorrelationId, UniqueKey)
    public string? UniqueKey { get; init; } // optional caller-provided discriminator
} // End of Class AuditEvent
