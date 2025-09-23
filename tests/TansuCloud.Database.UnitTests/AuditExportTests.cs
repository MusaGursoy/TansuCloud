// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Database.Security;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.UnitTests;

internal sealed class AdminAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
#pragma warning disable CS0618
    public AdminAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock
    )
        : base(options, logger, encoder, clock) { }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = new System.Security.Claims.ClaimsIdentity(Scheme.Name);
        id.AddClaim(new("scope", "admin.full"));
        var principal = new System.Security.Claims.ClaimsPrincipal(id);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class ReaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
#pragma warning disable CS0618
    public ReaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock
    )
        : base(options, logger, encoder, clock) { }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = new System.Security.Claims.ClaimsIdentity(Scheme.Name);
        id.AddClaim(new("scope", "db.read"));
        var principal = new System.Security.Claims.ClaimsPrincipal(id);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class AuditExportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuditExportTests(WebApplicationFactory<Program> baseFactory)
    {
        _factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAuditQueryService, FakeAuditQueryService>();

                // Register both handlers; tests will select scheme name
                services
                    .AddAuthentication("Admin")
                    .AddScheme<AuthenticationSchemeOptions, AdminAuthHandler>("Admin", _ => { });
                services
                    .AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, ReaderAuthHandler>("Reader", _ => { });
            });
            builder.UseEnvironment("Development");
        });
    }

    [Fact(DisplayName = "Export JSON requires admin scope and honors limit cap")]
    public async Task ExportJson_AdminAndLimit()
    {
        using var client = _factory.CreateClient(
            new() { BaseAddress = new Uri("http://localhost") }
        );

        // Force Admin auth
        client.DefaultRequestHeaders.Add("Authorization", "Admin test");

        var start = DateTimeOffset.UtcNow.AddMinutes(-10).UtcDateTime.ToString("O");
        var end = DateTimeOffset.UtcNow.AddMinutes(10).UtcDateTime.ToString("O");
        var url =
            $"/api/audit/export/json?startUtc={Uri.EscapeDataString(start)}&endUtc={Uri.EscapeDataString(end)}&tenantId=acme-dev&limit=5";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Tansu-Tenant", "acme-dev");

        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        resp.Headers.TryGetValues("X-Export-Limit", out var limits).Should().BeTrue();
        limits!.Single().Should().Be("5");
        var payload = await resp.Content.ReadAsStringAsync();
        var arr = JsonSerializer.Deserialize<JsonElement>(payload);
        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "Export CSV requires admin; reader gets 403")]
    public async Task ExportCsv_ReaderForbidden()
    {
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                s.AddAuthentication("Reader");
            });
        });

        using var client = factory.CreateClient(
            new() { BaseAddress = new Uri("http://localhost") }
        );
        client.DefaultRequestHeaders.Add("Authorization", "Reader test");

        var start = DateTimeOffset.UtcNow.AddMinutes(-10).UtcDateTime.ToString("O");
        var end = DateTimeOffset.UtcNow.AddMinutes(10).UtcDateTime.ToString("O");
        var url =
            $"/api/audit/export/csv?startUtc={Uri.EscapeDataString(start)}&endUtc={Uri.EscapeDataString(end)}&tenantId=acme-dev&limit=3";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Tansu-Tenant", "acme-dev");

        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
