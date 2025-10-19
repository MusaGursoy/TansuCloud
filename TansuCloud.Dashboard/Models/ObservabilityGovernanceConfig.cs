// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Models;

/// <summary>
/// Represents the observability governance configuration from governance.defaults.json
/// </summary>
public record ObservabilityGovernanceConfig
{
    public RetentionDaysConfig RetentionDays { get; init; } = new();
    public SamplingConfig Sampling { get; init; } = new();
    public List<AlertSloTemplate> AlertSLOs { get; init; } = new();
} // End of Record ObservabilityGovernanceConfig

public record RetentionDaysConfig
{
    public int Traces { get; init; } = 7;
    public int Logs { get; init; } = 7;
    public int Metrics { get; init; } = 14;
} // End of Record RetentionDaysConfig

public record SamplingConfig
{
    public double TraceRatio { get; init; } = 1.0;
} // End of Record SamplingConfig

public record AlertSloTemplate
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Service { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public int WindowMinutes { get; init; }
    public double Threshold { get; init; }
    public string Comparison { get; init; } = string.Empty;
} // End of Record AlertSloTemplate
