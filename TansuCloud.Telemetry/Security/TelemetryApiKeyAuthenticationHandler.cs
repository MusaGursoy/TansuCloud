// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Configuration;

namespace TansuCloud.Telemetry.Security;

/// <summary>
/// Authentication handler that validates bearer API keys for telemetry requests.
/// </summary>
/// <typeparam name="TOptions">The option type that supplies an API key.</typeparam>
public sealed class TelemetryApiKeyAuthenticationHandler<TOptions>
    : AuthenticationHandler<AuthenticationSchemeOptions>
    where TOptions : class, ITelemetryApiKeyOptions
{
    private readonly IOptionsMonitor<TOptions> _telemetryOptions;

    public TelemetryApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<TOptions> telemetryOptions
    )
        : base(options, logger, encoder)
    {
        _telemetryOptions = telemetryOptions;
    } // End of Constructor TelemetryApiKeyAuthenticationHandler

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = _telemetryOptions.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Task.FromResult(
                AuthenticateResult.Fail("Telemetry ingestion API key is not configured.")
            );
        }

        var allowCookieFallback = typeof(TOptions) == typeof(TelemetryAdminOptions);

        if (
            !TryResolveApiKey(
                allowCookieFallback,
                out var providedKey,
                out var source,
                out var failureReason
            )
        )
        {
            return Task.FromResult(AuthenticateResult.Fail(failureReason));
        }

        if (!SecureEquals(providedKey, configuredKey))
        {
            if (allowCookieFallback && source == ApiKeySource.Cookie)
            {
                Response.Cookies.Delete(
                    TelemetryAdminAuthenticationDefaults.ApiKeyCookieName,
                    new CookieOptions
                    {
                        Path = "/",
                        Secure = Request.IsHttps,
                        SameSite = SameSiteMode.Strict
                    }
                );
            }

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
        if (typeof(TOptions) == typeof(TelemetryAdminOptions))
        {
            if (!Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                var currentUrl = $"{Request.PathBase}{Request.Path}{Request.QueryString}";
                var redirectUrl = TelemetryAdminAuthenticationDefaults.LoginPath;

                if (!string.IsNullOrWhiteSpace(currentUrl) && currentUrl != "/")
                {
                    redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "returnUrl", currentUrl);
                }

                redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "missingKey", "1");

                Response.Redirect(redirectUrl);
                return Task.CompletedTask;
            }
        }

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

    private enum ApiKeySource
    {
        AuthorizationHeader,
        Cookie
    } // End of Enum ApiKeySource

    private bool TryResolveApiKey(
        bool allowCookieFallback,
        out string providedKey,
        out ApiKeySource source,
        out string failureReason
    )
    {
        const string bearerPrefix = "Bearer ";

        if (Request.Headers.TryGetValue("Authorization", out var authorizationHeader))
        {
            var headerValue = authorizationHeader.ToString();
            if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                providedKey = string.Empty;
                source = ApiKeySource.AuthorizationHeader;
                failureReason = "Authorization header must use the Bearer scheme.";
                return false;
            }

            var candidate = headerValue[bearerPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                providedKey = string.Empty;
                source = ApiKeySource.AuthorizationHeader;
                failureReason = "API key value is empty.";
                return false;
            }

            providedKey = candidate;
            source = ApiKeySource.AuthorizationHeader;
            failureReason = string.Empty;
            return true;
        }

        if (
            allowCookieFallback
            && Request.Cookies.TryGetValue(
                TelemetryAdminAuthenticationDefaults.ApiKeyCookieName,
                out var cookieValue
            )
            && !string.IsNullOrWhiteSpace(cookieValue)
        )
        {
            providedKey = cookieValue;
            source = ApiKeySource.Cookie;
            failureReason = string.Empty;
            return true;
        }

        providedKey = string.Empty;
        source = ApiKeySource.AuthorizationHeader;
        failureReason = allowCookieFallback
            ? "Authorization header or admin session cookie missing."
            : "Authorization header missing.";
        return false;
    } // End of Method TryResolveApiKey
} // End of Class TelemetryApiKeyAuthenticationHandler
