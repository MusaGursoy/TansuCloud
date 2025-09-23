// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;

namespace TansuCloud.Observability.Auditing;

/// <summary>
/// Convenience extensions to enforce allowlist-based redaction before enqueueing audit events.
/// </summary>
public static class AuditLoggerExtensions
{
    /// <summary>
    /// Redacts the provided source object using the allowlist and enqueues a copy of the seed audit event with Details set to the redacted JSON.
    /// </summary>
    /// <param name="logger">Audit logger instance.</param>
    /// <param name="seed">Base event with required fields (service/tenant/action etc.).</param>
    /// <param name="source">Arbitrary object to be redacted.</param>
    /// <param name="allowlist">Allowlisted property names to include in Details.</param>
    /// <returns>true if enqueued; false if dropped under backpressure.</returns>
    public static bool TryEnqueueRedacted(
        this IAuditLogger logger,
        AuditEvent seed,
        object source,
        IEnumerable<string> allowlist
    )
    {
        JsonDocument doc = AuditHelpers.RedactToJson(source, allowlist);
        var evt = new AuditEvent
        {
            Id = seed.Id,
            WhenUtc = seed.WhenUtc,
            Service = seed.Service,
            Environment = seed.Environment,
            Version = seed.Version,
            TenantId = seed.TenantId,
            Subject = seed.Subject,
            Action = seed.Action,
            Category = seed.Category,
            RouteTemplate = seed.RouteTemplate,
            CorrelationId = seed.CorrelationId,
            TraceId = seed.TraceId,
            SpanId = seed.SpanId,
            ClientIpHash = seed.ClientIpHash,
            UserAgent = seed.UserAgent,
            Outcome = seed.Outcome,
            ReasonCode = seed.ReasonCode,
            Details = doc,
            ImpersonatedBy = seed.ImpersonatedBy,
            SourceHost = seed.SourceHost,
            IdempotencyKey = seed.IdempotencyKey,
            UniqueKey = seed.UniqueKey
        };
        return logger.TryEnqueue(evt);
    } // End of Method TryEnqueueRedacted
} // End of Class AuditLoggerExtensions
