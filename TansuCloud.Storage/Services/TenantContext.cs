// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.RegularExpressions;

namespace TansuCloud.Storage.Services;

public interface ITenantContext
{
    string TenantId { get; }
}

internal sealed class HttpTenantContext : ITenantContext
{
    private static readonly Regex NonAlnum = new("[^a-zA-Z0-9]+", RegexOptions.Compiled);

    // Capture the tenant id once per request scope. This avoids relying on IHttpContextAccessor
    // during cached/background executions (e.g., HybridCache delegates) where HttpContext may be null.
    private readonly string _tenantId;

    public HttpTenantContext(IHttpContextAccessor accessor)
    {
        var id = accessor.HttpContext?.Request?.Headers["X-Tansu-Tenant"].ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            // Keep consistent prefixing even for default to ensure isolated storage roots
            // and stable cache keys across the app.
            _tenantId = "tansu_tenant__default";
        }
        else
        {
            // Normalize like DB service: non-alnum -> '_', prefix for isolation
            var norm = NonAlnum.Replace(id, "_");
            _tenantId = $"tansu_tenant_{norm}";
        }
    } // End of Constructor HttpTenantContext

    public string TenantId => _tenantId; // End of Property TenantId
} // End of Class HttpTenantContext
