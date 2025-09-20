// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json.Serialization;

namespace TansuCloud.Dashboard.Logging
{
    /// <summary>
    /// A minimal structured log record captured in-memory for admin viewing and periodic reporting.
    /// </summary>
    public sealed record LogRecord
    {
        public DateTimeOffset TimestampUtc { get; init; }
        public string Level { get; init; } = ""; // e.g., Information, Warning, Error, Critical
        public string Category { get; init; } = "";
        public int EventId { get; init; }
        public string Message { get; init; } = "";
        public string? Exception { get; init; }
        public string? Scope { get; init; }
    } // End of Class LogRecord
} // End of Namespace TansuCloud.Dashboard.Logging
