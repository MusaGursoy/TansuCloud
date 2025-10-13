// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TansuCloud.Telemetry.Data.Records;

/// <summary>
/// Base record for telemetry items stored alongside envelopes in database tables.
/// </summary>
public abstract class TelemetryItemRecordBase
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; } // End of Property Id

    public Guid EnvelopeId { get; set; } // End of Property EnvelopeId

    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty; // End of Property Kind

    public DateTime TimestampUtc { get; set; } // End of Property TimestampUtc

    [MaxLength(32)]
    public string Level { get; set; } = string.Empty; // End of Property Level

    [MaxLength(1024)]
    public string Message { get; set; } = string.Empty; // End of Property Message

    [MaxLength(128)]
    public string TemplateHash { get; set; } = string.Empty; // End of Property TemplateHash

    [MaxLength(2048)]
    public string? Exception { get; set; } // End of Property Exception

    [MaxLength(100)]
    public string? Service { get; set; } // End of Property Service

    [MaxLength(64)]
    public string? Environment { get; set; } // End of Property Environment

    [MaxLength(128)]
    public string? TenantHash { get; set; } // End of Property TenantHash

    [MaxLength(64)]
    public string? CorrelationId { get; set; } // End of Property CorrelationId

    [MaxLength(64)]
    public string? TraceId { get; set; } // End of Property TraceId

    [MaxLength(32)]
    public string? SpanId { get; set; } // End of Property SpanId

    [MaxLength(128)]
    public string? Category { get; set; } // End of Property Category

    public int? EventId { get; set; } // End of Property EventId

    public int Count { get; set; } // End of Property Count

    public string? PropertiesJson { get; set; } // End of Property PropertiesJson
} // End of Class TelemetryItemRecordBase

/// <summary>
/// Telemetry item that belongs to an active envelope.
/// </summary>
public sealed class TelemetryActiveItemRecord : TelemetryItemRecordBase
{
    public TelemetryActiveEnvelopeRecord Envelope { get; set; } = null!; // End of Property Envelope
} // End of Class TelemetryActiveItemRecord

/// <summary>
/// Telemetry item that belongs to an archived envelope.
/// </summary>
public sealed class TelemetryArchivedItemRecord : TelemetryItemRecordBase
{
    public TelemetryArchivedEnvelopeRecord Envelope { get; set; } = null!; // End of Property Envelope
} // End of Class TelemetryArchivedItemRecord