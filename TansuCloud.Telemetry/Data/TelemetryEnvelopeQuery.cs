// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Telemetry.Data;

/// <summary>
/// Represents filter parameters when querying telemetry envelopes.
/// </summary>
public sealed class TelemetryEnvelopeQuery
{
    public int Page { get; set; } = 1; // End of Property Page

    public int PageSize { get; set; } = 50; // End of Property PageSize

    public string? Service { get; set; } // End of Property Service

    public string? Host { get; set; } // End of Property Host

    public string? Environment { get; set; } // End of Property Environment

    public string? SeverityThreshold { get; set; } // End of Property SeverityThreshold

    public DateTime? FromUtc { get; set; } // End of Property FromUtc

    public DateTime? ToUtc { get; set; } // End of Property ToUtc

    public bool? Acknowledged { get; set; } // End of Property Acknowledged

    public bool? Deleted { get; set; } // End of Property Deleted

    public bool IncludeAcknowledged { get; set; } // End of Property IncludeAcknowledged

    public bool IncludeDeleted { get; set; } // End of Property IncludeDeleted

    public string? Search { get; set; } // End of Property Search

    public int Skip => (Math.Max(Page, 1) - 1) * Math.Max(PageSize, 1); // End of Property Skip
} // End of Class TelemetryEnvelopeQuery
