// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Logging;

namespace TansuCloud.Dashboard.Observability.Logging;

/// <summary>
/// Options controlling local log capture and periodic reporting to a central endpoint.
/// </summary>
public sealed class LoggingReportOptions
{
    /// <summary>
    /// Enable log capture and reporting. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true; // End of Property Enabled

    /// <summary>
    /// Minimum log level to capture into the in-memory buffer and to include in reports. Default: Warning.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning; // End of Property MinimumLevel

    /// <summary>
    /// Maximum number of records to retain in the in-memory buffer for UI/inspection. Oldest entries are dropped.
    /// Default: 5000.
    /// </summary>
    public int MaxBufferEntries { get; set; } = 5000; // End of Property MaxBufferEntries

    /// <summary>
    /// Maximum number of logs to send in a single report batch. Default: 1000.
    /// </summary>
    public int MaxBatchSize { get; set; } = 1000; // End of Property MaxBatchSize

    /// <summary>
    /// How often to send reports automatically. Default: 1 hour.
    /// </summary>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromHours(1); // End of Property ReportInterval

    /// <summary>
    /// Central endpoint to POST the report payload to. If empty or null, reporting is a no-op.
    /// </summary>
    public string? Endpoint { get; set; } // End of Property Endpoint

    /// <summary>
    /// Optional API key or secret to include as an Authorization header (Bearer or custom) for the report request.
    /// </summary>
    public string? ApiKey { get; set; } // End of Property ApiKey

    /// <summary>
    /// Include environment/service information in the payload. Default: true.
    /// </summary>
    public bool IncludeEnvironmentDetails { get; set; } = true; // End of Property IncludeEnvironmentDetails
} // End of Class LoggingReportOptions
