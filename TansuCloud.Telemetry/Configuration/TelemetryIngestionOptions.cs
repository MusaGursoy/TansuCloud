// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Configuration;

/// <summary>
/// Options controlling incoming telemetry ingestion.
/// </summary>
public sealed class TelemetryIngestionOptions : ITelemetryApiKeyOptions
{
    /// <summary>
    /// Gets or sets the API key required for ingestion requests.
    /// </summary>
    [Required]
    [MinLength(16)]
    public string ApiKey { get; set; } = string.Empty; // End of Property ApiKey

    /// <summary>
    /// Gets or sets the maximum number of telemetry batches that may be buffered concurrently.
    /// </summary>
    [Range(1, 32768)]
    public int QueueCapacity { get; set; } = 4096; // End of Property QueueCapacity

    /// <summary>
    /// Gets or sets the maximum duration to wait for queue availability before rejecting the request.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:15:00")]
    public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromSeconds(5); // End of Property EnqueueTimeout
} // End of Class TelemetryIngestionOptions
