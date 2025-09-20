// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TansuCloud.Dashboard.UnitTests;

public class CorrelationEchoTests : IClassFixture<DashboardWebAppFactory>
{
	private readonly DashboardWebAppFactory _factory;
	public CorrelationEchoTests(DashboardWebAppFactory factory)
	{
		_factory = factory;
	}

	[Fact(DisplayName = "Dashboard echoes X-Correlation-ID on health")]
	public async Task Echoes_Correlation_Id()
	{
		var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false
		});

		using var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
		req.Headers.Add("X-Correlation-ID", "dash-corr");

		var resp = await client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		resp.Headers.TryGetValues("X-Correlation-ID", out var values).Should().BeTrue();
		values!.Single().Should().Be("dash-corr");
	}
}
