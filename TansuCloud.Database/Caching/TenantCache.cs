// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;

namespace TansuCloud.Database.Caching;

public interface ITenantCacheVersion
{
    int Get(string tenant);
    int Increment(string tenant);
}

internal sealed class TenantCacheVersion : ITenantCacheVersion
{
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);

    public int Get(string tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant)) return 0;
        return _versions.TryGetValue(tenant, out var v) ? v : 0;
    } // End of Method Get

    public int Increment(string tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant)) return 0;
        return _versions.AddOrUpdate(tenant, 1, (_, old) => unchecked(old + 1));
    } // End of Method Increment
} // End of Class TenantCacheVersion
