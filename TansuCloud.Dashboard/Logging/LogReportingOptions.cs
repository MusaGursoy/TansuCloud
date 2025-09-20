// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Dashboard.Logging
{
    /// <summary>
    /// Options for periodic log reporting from the Dashboard to a central server.
    /// Enabled by default; admins can toggle at runtime via Logs admin page or API.
    /// </summary>
    public sealed class LogReportingOptions
    {
        /// <summary>
        /// Master switch; default true to collect and report logs.
        /// Admins can override at runtime; see ILogReportingState.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Destination endpoint to POST log batches (JSON).
        /// If null/empty, reporting is a no-op while buffering still works.
        /// </summary>
        [Url]
        public string? Endpoint { get; init; }

        /// <summary>
        /// Optional API key header value used as Authorization: Bearer {ApiKey} when posting.
        /// </summary>
        public string? ApiKey { get; init; }

        /// <summary>
        /// Reporting interval in minutes. Default: 60 (hourly).
        /// </summary>
        [Range(1, 24 * 60)]
        public int IntervalMinutes { get; init; } = 60;

        /// <summary>
        /// Maximum entries per batch POST. Default: 1000. Remaining entries will be sent in subsequent cycles.
        /// </summary>
        [Range(100, 100_000)]
        public int BatchSize { get; init; } = 1000;

        /// <summary>
        /// Minimum log level to capture into the buffer. Default: Warning.
        /// </summary>
        public string MinLevel { get; init; } = "Warning";

        /// <summary>
        /// In-memory buffer capacity (ring buffer). Default: 5000 entries.
        /// </summary>
        [Range(100, 1_000_000)]
        public int BufferCapacity { get; init; } = 5000;
    } // End of Class LogReportingOptions
} // End of Namespace TansuCloud.Dashboard.Logging
