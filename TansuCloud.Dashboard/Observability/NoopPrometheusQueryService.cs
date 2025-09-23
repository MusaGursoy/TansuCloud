// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TansuCloud.Dashboard.Observability;

/// <summary>
/// No-op implementation used when the legacy Prometheus proxy is disabled. Returns nulls so callers can render empty states.
/// </summary>
public sealed class NoopPrometheusQueryService : IPrometheusQueryService
{
    public Task<PromRangeResult?> QueryRangeAsync(
        string chartId,
        string? tenant,
        string? service,
        TimeSpan? range = null,
        TimeSpan? step = null,
        CancellationToken ct = default
    ) => Task.FromResult<PromRangeResult?>(null);

    public Task<PromInstantResult?> QueryInstantAsync(
        string chartId,
        string? tenant,
        string? service,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    ) => Task.FromResult<PromInstantResult?>(null);
} // End of Class NoopPrometheusQueryService
