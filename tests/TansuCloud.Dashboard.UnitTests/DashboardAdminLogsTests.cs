// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Dashboard.Observability.Logging;

namespace TansuCloud.Dashboard.UnitTests;

public sealed class DashboardAdminLogsTests : IClassFixture<DashboardAdminWebAppFactory>
{
    private readonly DashboardAdminWebAppFactory _factory;

    public DashboardAdminLogsTests(DashboardAdminWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "Recent logs endpoint supports pagination and filters")]
    public async Task Recent_Logs_Paginates_And_Filters()
    {
        using var scope = _factory.Services.CreateScope();
        var buffer = scope.ServiceProvider.GetRequiredService<ILogBuffer>();
        buffer.Clear();

        var runtime = scope.ServiceProvider.GetRequiredService<ILogReportingRuntimeSwitch>();
        runtime.Enabled = false;

        var now = DateTimeOffset.UtcNow;
        buffer.Add(new LogRecord { Timestamp = now.AddMinutes(-1), Level = "Error", Category = "Tansu.Storage", Message = "err-1" });
        buffer.Add(new LogRecord { Timestamp = now, Level = "Error", Category = "Tansu.Storage", Message = "err-2" });
        buffer.Add(new LogRecord { Timestamp = now, Level = "Warning", Category = "Tansu.Gateway", Message = "warn-1" });

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

    var url = "/api/admin/logs/recent?take=1&skip=1&level=Error&categoryContains=Storage";
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<List<LogRecord>>();
        payload.Should().NotBeNull();
        payload!.Should().ContainSingle();
        payload[0].Message.Should().Be("err-2");
    }
}

public sealed class DashboardAdminWebAppFactory : DashboardWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication("TestAdmin")
                .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>("TestAdmin", _ => { });

            services.AddAuthorization(options =>
            {
                options.AddPolicy("AdminOnly", policy =>
                {
                    policy.AddAuthenticationSchemes("TestAdmin");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Admin");
                });
            });
        });
    }
}

public sealed class TestAdminAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
#pragma warning disable CS0618
    public TestAdminAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ISystemClock clock
    ) : base(options, logger, encoder, clock)
    {
    }
#pragma warning restore CS0618

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin@test"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
