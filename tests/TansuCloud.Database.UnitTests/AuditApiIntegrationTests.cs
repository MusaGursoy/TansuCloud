// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.UnitTests;

// Custom factory that fakes authZ and replaces IAuditQueryService
public class DatabaseWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestEnvironment.EnsureInitialized();

        builder.ConfigureServices(services =>
        {
            // Replace the real query service with a fake one returning a deterministic item
            services.AddSingleton<IAuditQueryService, FakeAuditQueryService>();

            // Override auth to always satisfy the "db.read" policy
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                "Test",
                _ => { }
            );
            services.AddAuthorization(o =>
            {
                o.AddPolicy("db.read", p => p.RequireAssertion(_ => true));
            });
        });

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
} // End of Class DatabaseWebAppFactory

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
#pragma warning disable CS0618 // ISystemClock obsolete in tests; acceptable here
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock
    ) : base(options, logger, encoder, clock) { }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new System.Security.Claims.ClaimsIdentity(Scheme.Name);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
} // End of Class TestAuthHandler

public sealed class FakeAuditQueryService : IAuditQueryService
{
    public Task<QueryResult> QueryAsync(AuditQuery input, CancellationToken ct)
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var item = new AuditItem(
            id,
            DateTimeOffset.UtcNow,
            "tansu.database",
            "Development",
            "0.0.0",
            input.tenantId ?? "acme-dev",
            "tester",
            "TestAction",
            "Admin",
            "/test",
            "corr-123",
            "t",
            "s",
            null,
            null,
            null,
            null,
            null,
            null,
            null
        );
        return Task.FromResult(new QueryResult(new[] { item }, NextPageToken: null));
    }
} // End of Class FakeAuditQueryService

public class AuditApiIntegrationTests : IClassFixture<DatabaseWebAppFactory>
{
    private readonly DatabaseWebAppFactory _factory;

    public AuditApiIntegrationTests(DatabaseWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "GET /api/audit returns items for tenant")]
    public async Task Audit_Query_Returns_Items()
    {
        using var client = _factory.CreateClient(new() { BaseAddress = new Uri("http://localhost") });

        var start = DateTimeOffset.UtcNow.AddMinutes(-10).UtcDateTime.ToString("O");
        var end = DateTimeOffset.UtcNow.AddMinutes(10).UtcDateTime.ToString("O");
        var url = $"/api/audit?startUtc={Uri.EscapeDataString(start)}&endUtc={Uri.EscapeDataString(end)}&tenantId=acme-dev&pageSize=10";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Tansu-Tenant", "acme-dev");

        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        payload.TryGetProperty("items", out var items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);
        items.GetArrayLength().Should().BeGreaterThan(0);
    }
} // End of Class AuditApiIntegrationTests

