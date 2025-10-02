// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TansuCloud.Telemetry.Data.Entities;

/// <summary>
/// Represents a single telemetry item persisted alongside an envelope.
/// </summary>
public sealed class TelemetryItemEntity
{
    /// <summary>
    /// Gets or sets the primary key of the item.
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; } // End of Property Id

    /// <summary>
    /// Gets or sets the envelope identifier this item belongs to.
    /// </summary>
    public Guid EnvelopeId { get; set; } // End of Property EnvelopeId

    /// <summary>
    /// Gets or sets the owning envelope navigation property.
    /// </summary>
    public TelemetryEnvelopeEntity Envelope { get; set; } = null!; // End of Property Envelope

    /// <summary>
    /// Gets or sets the telemetry item kind.
    /// </summary>
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty; // End of Property Kind

    /// <summary>
    /// Gets or sets the UTC timestamp for the telemetry event.
    /// </summary>
    public DateTime TimestampUtc { get; set; } // End of Property TimestampUtc

    /// <summary>
    /// Gets or sets the severity level of the telemetry event.
    /// </summary>
    [MaxLength(32)]
    public string Level { get; set; } = string.Empty; // End of Property Level

    /// <summary>
    /// Gets or sets the message template or summary.
    /// </summary>
    [MaxLength(1024)]
    public string Message { get; set; } = string.Empty; // End of Property Message

    /// <summary>
    /// Gets or sets the template hash provided by the client reporter.
    /// </summary>
    [MaxLength(128)]
    public string TemplateHash { get; set; } = string.Empty; // End of Property TemplateHash

    /// <summary>
    /// Gets or sets the exception summary if present.
    /// </summary>
    [MaxLength(2048)]
    public string? Exception { get; set; } // End of Property Exception

    /// <summary>
    /// Gets or sets the originating service name.
    /// </summary>
    [MaxLength(100)]
    public string? Service { get; set; } // End of Property Service

    /// <summary>
    /// Gets or sets the originating environment.
    /// </summary>
    [MaxLength(64)]
    public string? Environment { get; set; } // End of Property Environment

    /// <summary>
    /// Gets or sets the tenant hash when provided.
    /// </summary>
    [MaxLength(128)]
    public string? TenantHash { get; set; } // End of Property TenantHash

    /// <summary>
    /// Gets or sets the correlation identifier.
    /// </summary>
    [MaxLength(64)]
    public string? CorrelationId { get; set; } // End of Property CorrelationId

    /// <summary>
    /// Gets or sets the trace identifier.
    /// </summary>
    [MaxLength(64)]
    public string? TraceId { get; set; } // End of Property TraceId

    /// <summary>
    /// Gets or sets the span identifier.
    /// </summary>
    [MaxLength(32)]
    public string? SpanId { get; set; } // End of Property SpanId

    /// <summary>
    /// Gets or sets the category name.
    /// </summary>
    [MaxLength(128)]
    public string? Category { get; set; } // End of Property Category

    /// <summary>
    /// Gets or sets the EventId value provided by the client.
    /// </summary>
    public int? EventId { get; set; } // End of Property EventId

    /// <summary>
    /// Gets or sets the aggregated count represented by the item.
    /// </summary>
    public int Count { get; set; } // End of Property Count

    /// <summary>
    /// Gets or sets the serialized properties payload.
    /// </summary>
    public string? PropertiesJson { get; set; } // End of Property PropertiesJson
} // End of Class TelemetryItemEntity
