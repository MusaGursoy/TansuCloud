// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TansuCloud.Storage.UnitTests;

public class CorrelationEchoTests : IClassFixture<StorageWebApplicationFactory>
{
    private readonly StorageWebApplicationFactory _factory;

    public CorrelationEchoTests(StorageWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "Storage echoes X-Correlation-ID on health")]
    public async Task Echoes_Correlation_Id()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        using var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Add("X-Correlation-ID", "storage-corr");

        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
        values!.Single().Should().Be("storage-corr");
    }
}
