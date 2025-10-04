// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Configuration;

/// <summary>
/// Database configuration settings.
/// </summary>
public sealed class TelemetryDatabaseOptions
{
    /// <summary>
    /// Gets or sets the SQLite database file path used for persistence.
    /// </summary>
    [Required]
    [MinLength(3)]
    public string FilePath { get; set; } = "App_Data/telemetry/telemetry.db"; // End of Property FilePath

    /// <summary>
    /// Gets or sets a value indicating whether to enforce foreign keys on the SQLite connection.
    /// </summary>
    public bool EnforceForeignKeys { get; set; } = true; // End of Property EnforceForeignKeys
} // End of Class TelemetryDatabaseOptions
