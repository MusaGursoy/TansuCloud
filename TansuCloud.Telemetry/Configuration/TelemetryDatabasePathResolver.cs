// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Hosting;

namespace TansuCloud.Telemetry.Configuration;

/// <summary>
/// Resolves and validates the absolute SQLite database file path.
/// </summary>
public static class TelemetryDatabasePathResolver
{
    /// <summary>
    /// Resolves the absolute database file path for the current environment.
    /// </summary>
    public static string Resolve(IHostEnvironment environment, TelemetryDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.FilePath))
        {
            throw new InvalidOperationException("Telemetry database file path is not configured.");
        }

        var candidate = options.FilePath;
        if (!Path.IsPathRooted(candidate))
        {
            candidate = Path.Combine(environment.ContentRootPath, candidate);
        }

        return Path.GetFullPath(candidate);
    } // End of Method Resolve
} // End of Class TelemetryDatabasePathResolver