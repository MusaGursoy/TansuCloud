// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.RegularExpressions;

namespace TansuCloud.Storage.Services;

public interface ITenantContext
{
    string TenantId { get; }
}

internal sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private static readonly Regex NonAlnum = new("[^a-zA-Z0-9]+", RegexOptions.Compiled);

    public string TenantId
    {
        get
        {
            var id = accessor.HttpContext?.Request?.Headers["X-Tansu-Tenant"].ToString();
            if (string.IsNullOrWhiteSpace(id)) return "_default";
            // Normalize like DB service: non-alnum -> '_', prefix for isolation
            var norm = NonAlnum.Replace(id, "_");
            return $"tansu_tenant_{norm}";
        }
    }
} // End of Class HttpTenantContext
