// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Runtime configuration holder for OutputCache policies.
/// </summary>
public interface IOutputCacheRuntime
{
    OutputCacheConfig GetCurrent();
    void Update(OutputCacheConfig config);
} // End of Interface IOutputCacheRuntime

internal sealed class OutputCacheRuntime : IOutputCacheRuntime
{
    private readonly ConcurrentDictionary<string, OutputCacheConfig> _store = new();
    private const string Key = "current";

    public OutputCacheRuntime(int defaultTtlSeconds, int staticTtlSeconds)
    {
        _store[Key] = new OutputCacheConfig
        {
            DefaultTtlSeconds = defaultTtlSeconds,
            StaticTtlSeconds = staticTtlSeconds
        };
    } // End of Constructor OutputCacheRuntime

    public OutputCacheConfig GetCurrent()
    {
        return _store.TryGetValue(Key, out var cfg) ? cfg : new OutputCacheConfig();
    } // End of Method GetCurrent

    public void Update(OutputCacheConfig config)
    {
        _store[Key] = config with { };
    } // End of Method Update
} // End of Class OutputCacheRuntime

/// <summary>
/// OutputCache configuration model.
/// </summary>
public sealed record OutputCacheConfig
{
    /// <summary>
    /// Default TTL in seconds for anonymous responses (base policy).
    /// </summary>
    public int DefaultTtlSeconds { get; set; } = 15;

    /// <summary>
    /// TTL in seconds for static assets (PublicStaticLong policy).
    /// </summary>
    public int StaticTtlSeconds { get; set; } = 300;
} // End of Class OutputCacheConfig
