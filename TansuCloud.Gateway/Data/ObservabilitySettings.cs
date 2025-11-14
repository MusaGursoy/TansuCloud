// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Gateway.Data;

/// <summary>
/// Observability settings for retention and sampling per component (Prometheus, Tempo, Loki).
/// Task 47 Phase 5: Retention and Sampling Management UI.
/// </summary>
public class ObservabilitySettings
{
    /// <summary>
    /// Primary key (auto-generated).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Component name: "prometheus", "tempo", or "loki" (lowercase, unique).
    /// </summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>
    /// Number of days to retain data (1-365).
    /// Default: 7 days.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Percentage of data to sample (0-100).
    /// Default: 100% (no sampling).
    /// </summary>
    public int SamplingPercent { get; set; } = 100;

    /// <summary>
    /// Whether data collection is enabled for this component.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When this setting was last updated (UTC).
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Admin user who last updated this setting (email or sub claim).
    /// </summary>
    public string? UpdatedBy { get; set; }
} // End of Class ObservabilitySettings
