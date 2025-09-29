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
                var publicBase =
                    Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                    ?? "http://127.0.0.1:8080";
                var gatewayBase =
                    Environment.GetEnvironmentVariable("GATEWAY_BASE_URL") ?? "http://gateway:8080";

                var overrides = new Dictionary<string, string?>
                {
                    ["PublicBaseUrl"] = publicBase,
                    ["GatewayBaseUrl"] = gatewayBase,
                    ["PUBLIC_BASE_URL"] = publicBase,
                    ["GATEWAY_BASE_URL"] = gatewayBase
                };

                configBuilder.AddInMemoryCollection(overrides);
            }
        );
    }
} // End of Class StorageWebApplicationFactory
