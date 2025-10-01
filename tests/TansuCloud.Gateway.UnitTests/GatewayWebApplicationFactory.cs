// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TansuCloud.Gateway.UnitTests;

public class GatewayWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestEnvironment.EnsureInitialized();

        builder.ConfigureAppConfiguration(
            (_, configBuilder) =>
            {
                var publicBase = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
                var gatewayBase = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");

                if (string.IsNullOrWhiteSpace(publicBase) && string.IsNullOrWhiteSpace(gatewayBase))
                {
                    return;
                }

                var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(publicBase))
                {
                    overrides["PublicBaseUrl"] = publicBase;
                    overrides["PUBLIC_BASE_URL"] = publicBase;
                }

                if (!string.IsNullOrWhiteSpace(gatewayBase))
                {
                    overrides["GatewayBaseUrl"] = gatewayBase;
                    overrides["GATEWAY_BASE_URL"] = gatewayBase;
                }

                configBuilder.AddInMemoryCollection(overrides);
            }
        );

        builder.UseEnvironment("Development");
    } // End of Method ConfigureWebHost
} // End of Class GatewayWebApplicationFactory
