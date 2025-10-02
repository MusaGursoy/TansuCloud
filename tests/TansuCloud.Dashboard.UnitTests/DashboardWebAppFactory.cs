// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", "http://127.0.0.1:8080");
        Environment.SetEnvironmentVariable("GATEWAY_BASE_URL", "http://127.0.0.1:8080");
        builder.ConfigureAppConfiguration((context, configBuilder) =>
        {
            var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PublicBaseUrl"] = "http://127.0.0.1:8080",
                ["GatewayBaseUrl"] = "http://127.0.0.1:8080",
                ["PUBLIC_BASE_URL"] = "http://127.0.0.1:8080",
                ["GATEWAY_BASE_URL"] = "http://127.0.0.1:8080"
            };
            configBuilder.AddInMemoryCollection(overrides);
        });
    }
} // End of Class DashboardWebAppFactory
