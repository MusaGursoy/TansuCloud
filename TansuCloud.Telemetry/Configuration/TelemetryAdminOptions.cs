// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Telemetry.Configuration;

/// <summary>
/// Options controlling administrator access to the telemetry service.
/// </summary>
public sealed class TelemetryAdminOptions : ITelemetryApiKeyOptions
{
    /// <summary>
    /// Gets or sets the API key used for administrator authentication.
    /// </summary>
    [Required]
    [MinLength(16)]
    public string ApiKey { get; set; } = string.Empty; // End of Property ApiKey

    /// <summary>
    /// Gets or sets the maximum number of envelopes returned per page in the admin UI.
    /// </summary>
    [Range(1, 500)]
    public int DefaultPageSize { get; set; } = 50; // End of Property DefaultPageSize

    /// <summary>
    /// Gets or sets the page size upper bound enforceable by admin queries.
    /// </summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 200; // End of Property MaxPageSize

    /// <summary>
    /// Gets or sets the maximum number of envelopes permitted for export operations.
    /// </summary>
    [Range(1, 1000)]
    public int MaxExportItems { get; set; } = 500; // End of Property MaxExportItems
} // End of Class TelemetryAdminOptions
