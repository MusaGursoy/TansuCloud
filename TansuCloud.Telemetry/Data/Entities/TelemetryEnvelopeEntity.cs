// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Data.Entities;

/// <summary>
/// Represents a telemetry report envelope persisted in the database.
/// </summary>
public sealed class TelemetryEnvelopeEntity
{
    /// <summary>
    /// Gets or sets the unique identifier for the envelope.
    /// </summary>
    [Key]
    public Guid Id { get; set; } // End of Property Id

    /// <summary>
    /// Gets or sets the UTC timestamp when the envelope was received.
    /// </summary>
    public DateTime ReceivedAtUtc { get; set; } // End of Property ReceivedAtUtc

    /// <summary>
    /// Gets or sets the source host that submitted the payload.
    /// </summary>
    [MaxLength(200)]
    public string Host { get; set; } = string.Empty; // End of Property Host

    /// <summary>
    /// Gets or sets the environment label supplied by the client.
    /// </summary>
    [MaxLength(64)]
    public string Environment { get; set; } = string.Empty; // End of Property Environment

    /// <summary>
    /// Gets or sets the service name.
    /// </summary>
    [MaxLength(100)]
    public string Service { get; set; } = string.Empty; // End of Property Service

    /// <summary>
    /// Gets or sets the severity threshold applied by the client reporter.
    /// </summary>
    [MaxLength(32)]
    public string SeverityThreshold { get; set; } = string.Empty; // End of Property SeverityThreshold

    /// <summary>
    /// Gets or sets the rolling window duration in minutes covered by this report.
    /// </summary>
    public int WindowMinutes { get; set; } // End of Property WindowMinutes

    /// <summary>
    /// Gets or sets the configured maximum number of items.
    /// </summary>
    public int MaxItems { get; set; } // End of Property MaxItems

    /// <summary>
    /// Gets or sets the number of items contained within the report.
    /// </summary>
    public int ItemCount { get; set; } // End of Property ItemCount

    /// <summary>
    /// Gets or sets the collection of items belonging to the envelope.
    /// </summary>
    public IList<TelemetryItemEntity> Items { get; set; } = new List<TelemetryItemEntity>(); // End of Property Items
} // End of Class TelemetryEnvelopeEntity
