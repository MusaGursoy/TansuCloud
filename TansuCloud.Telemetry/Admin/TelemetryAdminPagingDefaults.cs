// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using TansuCloud.Telemetry.Configuration;

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Provides helper methods for normalizing paging configuration for the telemetry admin surface.
/// </summary>
internal static class TelemetryAdminPagingDefaults
{
    internal const int DefaultPageSizeFallback = 50;
    internal const int MaxPageSizeFallback = 200;

    /// <summary>
    /// Calculates normalized paging defaults ensuring values stay within sane bounds even when configuration is missing or invalid.
    /// </summary>
    /// <param name="options">The admin options instance to normalize.</param>
    /// <returns>A tuple containing (defaultPageSize, maxPageSize).</returns>
    public static (int defaultPageSize, int maxPageSize) Calculate(TelemetryAdminOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var maxPageSize = options.MaxPageSize;
        if (maxPageSize <= 0)
        {
            maxPageSize = MaxPageSizeFallback;
        }

        var defaultPageSize = options.DefaultPageSize;
        if (defaultPageSize <= 0)
        {
            defaultPageSize = DefaultPageSizeFallback;
        }

        if (defaultPageSize > maxPageSize)
        {
            defaultPageSize = maxPageSize;
        }

        defaultPageSize = Math.Clamp(defaultPageSize, 1, MaxPageSizeFallback);
        maxPageSize = Math.Clamp(maxPageSize, defaultPageSize, MaxPageSizeFallback);

        return (defaultPageSize, maxPageSize);
    } // End of Method Calculate
} // End of Class TelemetryAdminPagingDefaults
