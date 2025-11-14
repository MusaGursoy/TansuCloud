// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Service for generating Grafana dashboard embed URLs with proper kiosk mode and filters.
/// Used to seamlessly integrate Grafana visualizations into the Admin Dashboard.
/// </summary>
public interface IGrafanaEmbedService
{
    /// <summary>
    /// Generate an iframe-ready URL for a Grafana dashboard.
    /// </summary>
    /// <param name="dashboardUid">Grafana dashboard UID (e.g., "tansucloud-overview")</param>
    /// <param name="theme">Dashboard theme: "light" or "dark"</param>
    /// <param name="timeRange">Time range (e.g., "now-1h", "now-6h", "now-24h")</param>
    /// <param name="variables">Optional dashboard variables (e.g., {"service": "gateway"})</param>
    /// <param name="panelId">Optional single panel ID for focused view</param>
    /// <returns>Full iframe URL with kiosk mode and filters applied</returns>
    string GetDashboardEmbedUrl(
        string dashboardUid, 
        string theme = "light", 
        string timeRange = "now-1h",
        Dictionary<string, string>? variables = null,
        int? panelId = null);

    /// <summary>
    /// Check if Grafana embedding is enabled in this environment.
    /// </summary>
    bool IsGrafanaAvailable { get; }
}

public class GrafanaEmbedService : IGrafanaEmbedService
{
    private readonly GrafanaOptions _options;
    private readonly ILogger<GrafanaEmbedService> _logger;

    public GrafanaEmbedService(IOptions<GrafanaOptions> options, ILogger<GrafanaEmbedService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsGrafanaAvailable => _options.Enabled && !string.IsNullOrWhiteSpace(_options.BaseUrl);

    public string GetDashboardEmbedUrl(
        string dashboardUid,
        string theme = "light",
        string timeRange = "now-1h",
        Dictionary<string, string>? variables = null,
        int? panelId = null)
    {
        if (!IsGrafanaAvailable)
        {
            _logger.LogWarning("Grafana embedding requested but not available");
            return string.Empty;
        }

        var baseUrl = _options.BaseUrl!.TrimEnd('/');
        
        // Build dashboard URL: /d/{uid}/{slug}
        var url = $"{baseUrl}/d/{dashboardUid}";

        // Query parameters for kiosk mode and settings
        var queryParams = new List<string>
        {
            "kiosk",                    // Hide Grafana chrome (nav, header)
            $"theme={theme}",           // Light or dark theme
            $"from={timeRange}",        // Time range start
            "to=now",                   // Time range end
            "refresh=30s"               // Auto-refresh every 30 seconds
        };

        // Add dashboard variables (e.g., var-service=gateway)
        if (variables != null)
        {
            foreach (var (key, value) in variables)
            {
                queryParams.Add($"var-{key}={Uri.EscapeDataString(value)}");
            }
        }

        // Add panel ID for single-panel view
        if (panelId.HasValue)
        {
            queryParams.Add($"panelId={panelId.Value}");
            queryParams.Add("viewPanel=" + panelId.Value);
        }

        var fullUrl = $"{url}?{string.Join("&", queryParams)}";
        
        _logger.LogDebug("Generated Grafana embed URL: {Url}", fullUrl);
        
        return fullUrl;
    }
}

/// <summary>
/// Configuration options for Grafana integration.
/// Bind from "Grafana" section in appsettings.json or environment variables.
/// </summary>
public class GrafanaOptions
{
    /// <summary>
    /// Enable Grafana dashboard embedding. Default: false in production.
    /// Set GRAFANA_ENABLED=true to enable.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL for Grafana (e.g., "http://grafana:3000" for container-to-container,
    /// or "http://127.0.0.1:3000" for direct access).
    /// Set GRAFANA_BASE_URL in environment.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional: Grafana API key for server-side queries (not needed for iframe embedding with anonymous auth).
    /// </summary>
    public string? ApiKey { get; set; }
}

// End of Class GrafanaEmbedService
