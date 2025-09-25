// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// A simple dynamic provider for YARP that holds routes/clusters in-memory and exposes a change token when updated.
/// </summary>
public sealed class DynamicProxyConfigProvider : IProxyConfigProvider
{
    private volatile InMemoryConfig _config;

    public DynamicProxyConfigProvider(
        IEnumerable<RouteConfig> routes,
        IEnumerable<ClusterConfig> clusters
    )
    {
        _config = new InMemoryConfig(routes.ToList(), clusters.ToList());
    } // End of Constructor DynamicProxyConfigProvider

    public IProxyConfig GetConfig() => _config;

    public (
        IReadOnlyList<RouteConfig> Routes,
        IReadOnlyList<ClusterConfig> Clusters
    ) GetSnapshot() => (_config.Routes, _config.Clusters);

    public void Update(IEnumerable<RouteConfig> routes, IEnumerable<ClusterConfig> clusters)
    {
        var newConfig = new InMemoryConfig(routes.ToList(), clusters.ToList());
        _config = newConfig;
        newConfig.SignalChange();
    } // End of Method Update

    private sealed class InMemoryConfig : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();

        public InMemoryConfig(
            IReadOnlyList<RouteConfig> routes,
            IReadOnlyList<ClusterConfig> clusters
        )
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        } // End of Constructor InMemoryConfig

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }
        public IChangeToken ChangeToken { get; private set; }

        public void SignalChange()
        {
            try
            {
                _cts.Cancel();
            }
            catch { }
        } // End of Method SignalChange
    } // End of Class InMemoryConfig
} // End of Class DynamicProxyConfigProvider
