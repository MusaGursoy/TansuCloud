// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability;

/// <summary>
/// Immutable chart metadata describing allowed/known PromQL templates that the Dashboard will execute.
/// </summary>
public sealed record ChartDefinition(
    string Id,
    string Title,
    string Category,
    bool RequiresTenant,
    bool AcceptsService,
    string Unit
);

/// <summary>
/// Central catalog for allowlisted charts. Keep IDs in sync with PrometheusQueryService builders.
/// </summary>
public static class ChartCatalog
{
    public static IReadOnlyList<ChartDefinition> All { get; } =
        new List<ChartDefinition>
        {
            // Overview
            new(
                "overview.rps.byservice",
                "Requests/sec by service",
                "Overview",
                false,
                false,
                "rps"
            ),
            new(
                "overview.errors.byservice",
                "Error rate (4xx/5xx) by service",
                "Overview",
                false,
                false,
                "rps"
            ),
            new(
                "overview.latency.p95.byservice",
                "Latency p95 by service",
                "Overview",
                false,
                false,
                "ms"
            ),
            new(
                "overview.latency.p50.byservice",
                "Latency p50 by service",
                "Overview",
                false,
                false,
                "ms"
            ),
            // Storage
            new("storage.http.rps", "Storage HTTP RPS by op/status", "Storage", true, false, "rps"),
            new("storage.http.errors", "Storage 5xx by op", "Storage", true, false, "rps"),
            new(
                "storage.http.latency.p95",
                "Storage latency p95 by op",
                "Storage",
                true,
                false,
                "ms"
            ),
            new(
                "storage.http.latency.p50",
                "Storage latency p50 by op",
                "Storage",
                true,
                false,
                "ms"
            ),
            new(
                "storage.bytes.ingress.rate",
                "Ingress bytes/sec by tenant",
                "Storage",
                true,
                false,
                "B/s"
            ),
            new(
                "storage.bytes.egress.rate",
                "Egress bytes/sec by tenant",
                "Storage",
                true,
                false,
                "B/s"
            ),
            new(
                "storage.http.status",
                "Storage status distribution",
                "Storage",
                true,
                false,
                "rps"
            ),
            new("storage.cache.hitratio", "Cache hit ratio by op", "Storage", false, true, "ratio"),
            // Gateway
            new("gateway.http.rps.byroute", "Gateway RPS by route", "Gateway", false, false, "rps"),
            new(
                "gateway.http.status",
                "Gateway status distribution",
                "Gateway",
                false,
                false,
                "rps"
            ),
            new(
                "gateway.http.latency.p95.byroute",
                "Gateway latency p95 by route",
                "Gateway",
                false,
                false,
                "ms"
            ),
            // Database / Outbox
            new(
                "db.outbox.dispatched.rate",
                "Outbox dispatched/sec",
                "Database",
                false,
                false,
                "eps"
            ),
            new("db.outbox.retried.rate", "Outbox retried/sec", "Database", false, false, "eps"),
            new(
                "db.outbox.deadlettered.rate",
                "Outbox deadlettered/sec",
                "Database",
                false,
                false,
                "eps"
            ),
            // Database / HTTP (via ASP.NET Core instrumentation)
            new("db.http.rps", "Database HTTP requests/sec", "Database", false, false, "rps"),
            new(
                "db.http.errors.5xx",
                "Database HTTP 5xx errors/sec",
                "Database",
                false,
                false,
                "rps"
            ),
        };
} // End of Class ChartCatalog
