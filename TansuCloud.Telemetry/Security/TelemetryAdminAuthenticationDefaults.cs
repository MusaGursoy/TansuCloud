// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Telemetry.Security;

/// <summary>
/// Default values and constants used by the telemetry admin authentication flow.
/// </summary>
public static class TelemetryAdminAuthenticationDefaults
{
    /// <summary>
    /// The name of the cookie that stores the admin API key for browser sessions.
    /// </summary>
    public const string ApiKeyCookieName = "tansu.telemetry.admin.apikey"; // End of Field ApiKeyCookieName

    /// <summary>
    /// The query-string parameter name accepted when bootstrapping a browser session.
    /// </summary>
    public const string ApiKeyQueryParameter = "apiKey"; // End of Field ApiKeyQueryParameter

    /// <summary>
    /// The relative login path for the admin UI.
    /// </summary>
    public const string LoginPath = "/admin/login"; // End of Field LoginPath

    /// <summary>
    /// The relative logout path for the admin UI.
    /// </summary>
    public const string LogoutPath = "/admin/logout"; // End of Field LogoutPath
} // End of Class TelemetryAdminAuthenticationDefaults
