// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;

namespace TansuCloud.Gateway.Observability;

internal static class GatewayRouteMetadata
{
    private static readonly IReadOnlyDictionary<
        string,
        (string RouteTemplate, string Upstream)
    > Map = new Dictionary<string, (string RouteTemplate, string Upstream)>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["dashboard"] = ("/dashboard/{**catch-all}", "dashboard"),
        ["identity"] = ("/identity/{**catch-all}", "identity"),
        ["db"] = ("/db/{**catch-all}", "database"),
        ["storage"] = ("/storage/{**catch-all}", "storage"),
        ["admin"] = ("/admin/{**catch-all}", "gateway-admin"),
        ["health"] = ("/health/ready", "gateway"),
    };

    internal static (string? RouteTemplate, string? Upstream) Resolve(string? routeBase)
    {
        if (string.IsNullOrWhiteSpace(routeBase))
        {
            return ("/", "gateway");
        }

        if (Map.TryGetValue(routeBase, out var value))
        {
            return value;
        }

        return ($"/{routeBase}", null);
    }
}
