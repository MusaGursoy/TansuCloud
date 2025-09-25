// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TansuCloud.Dashboard.UnitTests;

/// <summary>
/// Minimal WebApplicationFactory wiring for dashboard integration tests.
/// Keeps the application in Development mode so diagnostics stay verbose
/// while avoiding Prometheus-specific overrides that were removed.
/// </summary>
public class DashboardWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
} // End of Class DashboardWebAppFactory
