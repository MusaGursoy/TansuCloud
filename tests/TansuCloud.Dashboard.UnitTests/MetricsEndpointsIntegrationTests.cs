// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Dashboard.Observability;

namespace TansuCloud.Dashboard.UnitTests;

// Lightweight WebApplicationFactory that replaces the Prometheus HTTP client and fakes auth.
public class DashboardWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Override Prometheus HttpClient with a stubbed handler that returns a tiny valid payload
            services
                .AddHttpClient("prometheus")
                .ConfigurePrimaryHttpMessageHandler(() =>
                {
                    return new StubHandler();
                });

            // Override authentication/authorization to always treat requests as Admin
            services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            services.AddAuthorization(o =>
            {
                o.AddPolicy("AdminOnly", policy => policy.RequireAssertion(_ => true));
            });
        });

        builder.UseEnvironment("Development");
    }

    public sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var content =
                "{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[]}}";
            // For instant query endpoint, return vector shape
            if (
                request.RequestUri!.AbsolutePath.EndsWith("/api/v1/query", StringComparison.Ordinal)
            )
            {
                content =
                    "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}";
            }
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }
}

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
#pragma warning disable CS0618 // ISystemClock obsolete in tests; acceptable here
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock
    )
        : base(options, logger, encoder, clock) { }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

// Factory that authenticates as a non-admin user and does NOT override the AdminOnly policy
public class DashboardWebAppFactoryNonAdmin : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Stub Prometheus client to avoid external calls
            services
                .AddHttpClient("prometheus")
                .ConfigurePrimaryHttpMessageHandler(() => new DashboardWebAppFactory.StubHandler());

            // Provide a test auth scheme that yields a non-admin identity
            services
                .AddAuthentication("TestNonAdmin")
                .AddScheme<AuthenticationSchemeOptions, NonAdminAuthHandler>(
                    "TestNonAdmin",
                    _ => { }
                );
        });

        builder.UseEnvironment("Development");
    }
}

public class NonAdminAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
#pragma warning disable CS0618 // ISystemClock obsolete in tests; acceptable here
    public NonAdminAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock
    )
        : base(options, logger, encoder, clock) { }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "test-nonadmin") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class MetricsEndpointsIntegrationTests : IClassFixture<DashboardWebAppFactory>
{
    private readonly DashboardWebAppFactory _factory;

    public MetricsEndpointsIntegrationTests(DashboardWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "GET /api/metrics/catalog returns 200 and list")]
    public async Task Catalog_Returns_List()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        var resp = await client.GetAsync("/api/metrics/catalog");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<List<object>>();
        json.Should().NotBeNull();
        json!.Count.Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "GET /api/metrics/range returns 200 for known chart id")]
    public async Task Range_Returns_Success()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        var url = "/api/metrics/range?chartId=storage.http.rps&rangeMinutes=1&stepSeconds=15";
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<PromRangeResult>();
        json.Should().NotBeNull();
        json!.resultType.Should().Be("matrix");
    }

    [Fact(DisplayName = "GET /api/metrics/instant returns 200 for known chart id")]
    public async Task Instant_Returns_Success()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        var url = "/api/metrics/instant?chartId=gateway.http.rps.byroute";
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<PromInstantResult>();
        json.Should().NotBeNull();
        json!.resultType.Should().Be("vector");
    }

    [Fact(DisplayName = "Unknown chart id returns 400 from endpoints")]
    public async Task Unknown_Chart_Returns_400()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        var resp = await client.GetAsync("/api/metrics/range?chartId=does.not.exist");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public class MetricsEndpointsRbacTests : IClassFixture<DashboardWebAppFactoryNonAdmin>
{
    private readonly DashboardWebAppFactoryNonAdmin _factory;

    public MetricsEndpointsRbacTests(DashboardWebAppFactoryNonAdmin factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "RBAC: non-admin gets 403 on catalog endpoint")]
    public async Task Catalog_Forbidden_For_NonAdmin()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/metrics/catalog");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
