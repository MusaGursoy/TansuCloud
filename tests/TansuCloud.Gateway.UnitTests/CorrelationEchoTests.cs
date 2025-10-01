// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TansuCloud.Gateway.UnitTests;

public class CorrelationEchoTests : IClassFixture<GatewayWebApplicationFactory>
{
    private readonly GatewayWebApplicationFactory _factory;

    public CorrelationEchoTests(GatewayWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "Gateway echoes X-Correlation-ID on ping")]
    public async Task Echoes_Correlation_Id()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        // Use the public root endpoint which is mapped in Program
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Correlation-ID", "unit-test-corr");

        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        values!.Single().Should().Be("unit-test-corr");
    }
}
