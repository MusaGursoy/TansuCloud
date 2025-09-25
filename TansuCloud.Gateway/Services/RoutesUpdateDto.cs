// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Yarp.ReverseProxy.Configuration;

namespace TansuCloud.Gateway.Services;

public sealed class RoutesUpdateDto
{
    public List<RouteConfig> Routes { get; set; } = new();
    public List<ClusterConfig> Clusters { get; set; } = new();
} // End of Class RoutesUpdateDto
