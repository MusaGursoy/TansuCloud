// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Minimal, non-invasive placeholders for ML-related gateway metrics.
/// These are not wired yet; callers can opt-in to publish during Task 34.
/// </summary>
public static class MlMetrics
{
    private static readonly Meter Meter = new("TansuCloud.Gateway", "1.0.0");

    // Counter: number of recommendation responses served.
    public static readonly Counter<long> RecommendationsServed = Meter.CreateCounter<long>(
        name: "ml_recommendations_served",
        unit: "items",
        description: "Count of recommendation items served to clients."
    );

    // Histogram: milliseconds for inference latency observed at gateway edge.
    public static readonly Histogram<double> InferenceLatencyMs = Meter.CreateHistogram<double>(
        name: "ml_inference_latency_ms",
        unit: "ms",
        description: "Observed end-to-end inference latency at gateway."
    );

    // Gauge-like via Observable: percentage coverage (items with recs / items requested), placeholder returning 0.
    private static double _coveragePct;
    private static readonly ObservableGauge<double> CoverageGauge = Meter.CreateObservableGauge(
        name: "ml_recommendation_coverage_pct",
        observeValue: () => new Measurement<double>(_coveragePct),
        unit: "%",
        description: "Recommendation coverage percentage (placeholder)."
    );

    /// <summary>
    /// Optional setter for coverage gauge. Safe no-op wrapper.
    /// </summary>
    public static void SetCoverage(double percentage)
    {
        _coveragePct = percentage;
    } // End of Method SetCoverage
}
