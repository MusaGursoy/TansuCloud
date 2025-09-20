// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Dashboard.Observability.Logging
{
    /// <summary>
    /// Options to control production error/warning log reporting and Dashboard log views.
    /// </summary>
    public sealed class LogReportingOptions
    {
        /// <summary>
        /// Enable periodic log reporting to the main server (default: true).
        /// </summary>
        public bool Enabled { get; set; } = true; // End of Property Enabled

        /// <summary>
        /// How often to send aggregated logs (minutes). Default: 60.
        /// </summary>
        [Range(1, 24 * 60)]
        public int ReportIntervalMinutes { get; set; } = 60; // End of Property ReportIntervalMinutes

        /// <summary>
        /// HTTP endpoint on the "main server" that receives log reports.
        /// If empty, reporter becomes a no-op.
        /// </summary>
        public string? MainServerUrl { get; set; } // End of Property MainServerUrl

        /// <summary>
        /// Optional API key or bearer token to authenticate with the main server.
        /// </summary>
        public string? ApiKey { get; set; } // End of Property ApiKey

        /// <summary>
        /// Minimum severity to include in reports (Information, Warning, Error, Critical).
        /// Default: Warning.
        /// </summary>
        public string SeverityThreshold { get; set; } = "Warning"; // End of Property SeverityThreshold

        /// <summary>
        /// Time window (minutes) to query logs for each report. Default: 60.
        /// </summary>
        [Range(1, 24 * 60)]
        public int QueryWindowMinutes { get; set; } = 60; // End of Property QueryWindowMinutes

        /// <summary>
        /// Max items (log records) to include per report. Default: 2000.
        /// </summary>
        [Range(50, 100_000)]
        public int MaxItems { get; set; } = 2000; // End of Property MaxItems

        /// <summary>
        /// Timeout for outbound HTTP calls when sending the report.
        /// </summary>
        [Range(1, 300)]
        public int HttpTimeoutSeconds { get; set; } = 30; // End of Property HttpTimeoutSeconds

        /// <summary>
        /// Percentage (0-100) of non-allowlisted warnings to include (sampling). Default: 10.
        /// </summary>
        [Range(0, 100)]
        public int WarningSamplingPercent { get; set; } = 10; // End of Property WarningSamplingPercent

        /// <summary>
        /// Allowlisted categories (prefix match) for product warnings. Others are sampled or dropped.
        /// e.g., ["OIDC-", "Tansu.Gateway", "Tansu.Storage", "Tansu.Database"].
        /// </summary>
        public string[] AllowedWarningCategories { get; set; } = new[] { "OIDC-", "Tansu.Gateway", "Tansu.Storage", "Tansu.Database", "Tansu.Identity" }; // End of Property AllowedWarningCategories

        /// <summary>
        /// Performance SLO thresholds for emitting aggregated perf_slo_breach.
        /// </summary>
        [Range(50, 10_000)]
        public int HttpLatencyP95Ms { get; set; } = 500; // End of Property HttpLatencyP95Ms

        [Range(50, 10_000)]
        public int DbDurationP95Ms { get; set; } = 300; // End of Property DbDurationP95Ms

        [Range(0, 100)]
        public int ErrorRatePercent { get; set; } = 1; // End of Property ErrorRatePercent
    } // End of Class LogReportingOptions
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
