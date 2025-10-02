// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Configuration;

namespace TansuCloud.Telemetry.Security;

/// <summary>
/// Authentication handler that validates bearer API keys for ingestion requests.
/// </summary>
public sealed class TelemetryApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<TelemetryIngestionOptions> _telemetryOptions;

    public TelemetryApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock,
        IOptionsMonitor<TelemetryIngestionOptions> telemetryOptions
    ) : base(options, logger, encoder, clock)
    {
        _telemetryOptions = telemetryOptions;
    } // End of Constructor TelemetryApiKeyAuthenticationHandler

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = _telemetryOptions.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Telemetry ingestion API key is not configured."));
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authorizationHeader))
        {
            return Task.FromResult(AuthenticateResult.Fail("Authorization header missing."));
        }

        var headerValue = authorizationHeader.ToString();
        if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Authorization header must use the Bearer scheme."));
        }

        var providedKey = headerValue["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(providedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("API key value is empty."));
        }

        if (!SecureEquals(providedKey, configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "TelemetryReporter"),
            new Claim(ClaimTypes.Name, "TelemetryReporter"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    } // End of Method HandleAuthenticateAsync

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"tansu.telemetry\"";
        return Task.CompletedTask;
    } // End of Method HandleChallengeAsync

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    } // End of Method HandleForbiddenAsync

    private static bool SecureEquals(string providedValue, string expectedValue)
    {
        var providedBytes = Encoding.UTF8.GetBytes(providedValue);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedValue);

        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    } // End of Method SecureEquals
} // End of Class TelemetryApiKeyAuthenticationHandler