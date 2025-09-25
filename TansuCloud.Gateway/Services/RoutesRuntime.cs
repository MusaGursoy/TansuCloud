// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Concurrent;
using Yarp.ReverseProxy.Configuration;

namespace TansuCloud.Gateway.Services;

public interface IRoutesRuntime
{
    (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters)? GetPrevious();
    void SetPrevious(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters);
    void ClearPrevious();
} // End of Interface IRoutesRuntime

/// <summary>
/// Simple in-memory holder for the previous YARP config snapshot so the admin can roll back once.
/// </summary>
public sealed class RoutesRuntime : IRoutesRuntime
{
    private IReadOnlyList<RouteConfig>? _prevRoutes;
    private IReadOnlyList<ClusterConfig>? _prevClusters;
    private readonly object _lock = new();

    public (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters)? GetPrevious()
    {
        lock (_lock)
        {
            if (_prevRoutes is null || _prevClusters is null)
                return null;
            return (_prevRoutes, _prevClusters);
        }
    } // End of Method GetPrevious

    public void SetPrevious(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters
    )
    {
        lock (_lock)
        {
            _prevRoutes = routes;
            _prevClusters = clusters;
        }
    } // End of Method SetPrevious

    public void ClearPrevious()
    {
        lock (_lock)
        {
            _prevRoutes = null;
            _prevClusters = null;
        }
    } // End of Method ClearPrevious
} // End of Class RoutesRuntime
