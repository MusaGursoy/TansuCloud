// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TansuCloud.Storage.UnitTests;

public sealed class StorageWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestEnvironment.EnsureInitialized();

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration(
            (context, configBuilder) =>
            {
                var publicBase = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
                var gatewayBase = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");

                // Honor environment/.env only. Do not introduce hardcoded URL fallbacks here.
                // Add keys only when present to avoid masking other configuration sources.
                var overrides = new Dictionary<string, string?>();
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

                if (overrides.Count > 0)
                {
                    configBuilder.AddInMemoryCollection(overrides);
                }
            }
        );
    }
} // End of Class StorageWebApplicationFactory
