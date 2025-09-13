// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Storage.Services;

/// <summary>
/// Centralized helper for normalizing object route keys.
/// Currently decodes percent-escapes (e.g., %2F) once using Uri.UnescapeDataString.
/// </summary>
internal static class RouteKeyNormalizer
{
    /// <summary>
    /// Normalize a route key captured from a path parameter.
    /// </summary>
    public static string Normalize(string key)
    {
        // Perform a single unescape to convert %2F back to '/'.
        // This mirrors behavior used by controllers and presign canonicalization.
        return Uri.UnescapeDataString(key);
    } // End of Method Normalize
} // End of Class RouteKeyNormalizer
