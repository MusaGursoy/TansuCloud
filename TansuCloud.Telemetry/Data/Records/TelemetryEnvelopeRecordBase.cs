// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Data.Records;

/// <summary>
/// Base record that maps telemetry envelopes to concrete database tables.
/// </summary>
/// <typeparam name="TItem">The telemetry item record type stored with the envelope.</typeparam>
public abstract class TelemetryEnvelopeRecordBase<TItem>
    where TItem : TelemetryItemRecordBase
{
    [Key]
    public Guid Id { get; set; } // End of Property Id

    [MaxLength(200)]
    public string Host { get; set; } = string.Empty; // End of Property Host

    [MaxLength(64)]
    public string Environment { get; set; } = string.Empty; // End of Property Environment

    [MaxLength(100)]
    public string Service { get; set; } = string.Empty; // End of Property Service

    [MaxLength(32)]
    public string SeverityThreshold { get; set; } = string.Empty; // End of Property SeverityThreshold

    public DateTime ReceivedAtUtc { get; set; } // End of Property ReceivedAtUtc

    public int WindowMinutes { get; set; } // End of Property WindowMinutes

    public int MaxItems { get; set; } // End of Property MaxItems

    public int ItemCount { get; set; } // End of Property ItemCount

    public DateTime? AcknowledgedAtUtc { get; set; } // End of Property AcknowledgedAtUtc

    public DateTime? DeletedAtUtc { get; set; } // End of Property DeletedAtUtc

    public IList<TItem> Items { get; set; } = new List<TItem>(); // End of Property Items
} // End of Class TelemetryEnvelopeRecordBase

/// <summary>
/// Active envelopes live in the hot-path table.
/// </summary>
public sealed class TelemetryActiveEnvelopeRecord : TelemetryEnvelopeRecordBase<TelemetryActiveItemRecord>
{ } // End of Class TelemetryActiveEnvelopeRecord

/// <summary>
/// Archived envelopes are moved from the hot-path table for long-term retention.
/// </summary>
public sealed class TelemetryArchivedEnvelopeRecord : TelemetryEnvelopeRecordBase<TelemetryArchivedItemRecord>
{ } // End of Class TelemetryArchivedEnvelopeRecord