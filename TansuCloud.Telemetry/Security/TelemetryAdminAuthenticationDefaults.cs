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

    /// <summary>
    /// The query-string parameter used to convey authentication status to the login page.
    /// </summary>
    public const string AuthMessageQueryParameter = "reason"; // End of Field AuthMessageQueryParameter

    /// <summary>
    /// The context item key that stores the latest authentication failure reason for the current request.
    /// </summary>
    public const string AuthFailureContextItemKey = "Telemetry.Admin.AuthFailureReason"; // End of Field AuthFailureContextItemKey

    /// <summary>
    /// Canonical string values that represent authentication failure reasons understood by the admin UI.
    /// </summary>
    public static class AuthFailureReasons
    {
        /// <summary>
        /// Indicates an admin session is missing (header/cookie absent) and the user should provide an API key.
        /// </summary>
        public const string MissingSession = "missing-session"; // End of Field MissingSession

        /// <summary>
        /// Indicates the stored admin session cookie was rejected (likely due to key rotation or expiry).
        /// </summary>
        public const string InvalidSession = "invalid-session"; // End of Field InvalidSession

        /// <summary>
        /// Indicates the caller attempted to use a malformed Authorization header.
        /// </summary>
        public const string InvalidAuthorizationHeader = "invalid-authorization"; // End of Field InvalidAuthorizationHeader
    } // End of Class AuthFailureReasons
} // End of Class TelemetryAdminAuthenticationDefaults
