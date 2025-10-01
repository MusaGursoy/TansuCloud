// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;

namespace TansuCloud.Observability;

#pragma warning disable CS1591

public static class TelemetryConstants
{
    public const string Tenant = "tansu.tenant";
    public const string RouteBase = "tansu.route_base";
    public const string RouteTemplate = "tansu.route_template";
    public const string CorrelationId = "tansu.correlation_id";
    public const string UpstreamService = "tansu.gateway.upstream_service";
}

public static class TansuActivitySources
{
    public static readonly ActivitySource Background = new("TansuCloud.Background");
    public static readonly ActivitySource StorageTransforms = new("TansuCloud.Storage.Transforms");
}

#pragma warning restore CS1591
