// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Represents the query-string parameters accepted by the telemetry envelopes admin API.
/// </summary>
public sealed class TelemetryEnvelopeListRequest
{
    [Range(1, int.MaxValue)]
    public int? Page { get; set; } = 1; // End of Property Page

    [Range(1, 500)]
    public int? PageSize { get; set; } = 50; // End of Property PageSize

    [MaxLength(200)]
    public string? Host { get; set; } // End of Property Host

    [MaxLength(100)]
    public string? Service { get; set; } // End of Property Service

    [MaxLength(64)]
    public string? Environment { get; set; } // End of Property Environment

    [MaxLength(32)]
    public string? SeverityThreshold { get; set; } // End of Property SeverityThreshold

    public DateTimeOffset? FromUtc { get; set; } // End of Property FromUtc

    public DateTimeOffset? ToUtc { get; set; } // End of Property ToUtc

    public bool? Acknowledged { get; set; } // End of Property Acknowledged

    public bool? Deleted { get; set; } // End of Property Deleted

    public bool IncludeAcknowledged { get; set; } // End of Property IncludeAcknowledged

    public bool IncludeDeleted { get; set; } // End of Property IncludeDeleted

    [MaxLength(256)]
    public string? Search { get; set; } // End of Property Search
} // End of Class TelemetryEnvelopeListRequest
