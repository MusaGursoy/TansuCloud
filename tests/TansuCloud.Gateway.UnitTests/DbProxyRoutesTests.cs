// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TansuCloud.Gateway.UnitTests;

public sealed class DbProxyRoutesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DbProxyRoutesTests(WebApplicationFactory<Program> factory)
    {
        // Boot the gateway with defaults; no downstream Database needed to assert auth guard
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task Db_Audit_Export_Json_Is_Guarded_By_Auth()
    {
        // In Development our gateway still enforces Authorization for /db except explicit health/provisioning exceptions
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        var resp = await client.GetAsync("/db/api/audit/export/json");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
    } // End of Method Db_Audit_Export_Json_Is_Guarded_By_Auth
} // End of Class DbProxyRoutesTests
