// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Configuration;

/// <summary>
/// Represents configuration that provides an API key for authentication.
/// </summary>
public interface ITelemetryApiKeyOptions
{
    /// <summary>
    /// Gets the API key used for authenticating requests.
    /// </summary>
    [Required]
    [MinLength(16)]
    string ApiKey { get; }
} // End of Interface ITelemetryApiKeyOptions
