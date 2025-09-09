// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace TansuCloud.Dashboard.Hosting;

/// <summary>
/// Proactively fetches OIDC discovery metadata and JWKS at startup so the first interactive
/// login during E2E tests doesn't race Identity's key material publication.
/// </summary>
internal sealed class OidcWarmupHostedService : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<OidcWarmupHostedService> _logger;

    public OidcWarmupHostedService(IServiceProvider sp, ILogger<OidcWarmupHostedService> logger)
    {
        _sp = sp;
        _logger = logger;
    } // End of Constructor OidcWarmupHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();
            var handlerProvider =
                scope.ServiceProvider.GetRequiredService<IAuthenticationHandlerProvider>();
            // Create a lightweight HttpContext instance – we don't need a factory here and
            // HttpContext is NOT disposable; previous using pattern caused a build error.
            var fakeContext = new DefaultHttpContext();

            // Resolve the OIDC handler
            var oidcScheme = await schemes.GetSchemeAsync("oidc");
            if (oidcScheme == null)
            {
                _logger.LogWarning("OIDC scheme not found during warm-up");
                return; // nothing to do
            }

            var handler =
                await handlerProvider.GetHandlerAsync(fakeContext, "oidc") as OpenIdConnectHandler;
            if (handler == null)
            {
                _logger.LogWarning("OpenIdConnectHandler not resolved during warm-up");
                return;
            }

            // Force configuration fetch with bounded retries
            var attempt = 0;
            OpenIdConnectConfiguration? config = null;
            const int maxAttempts = 5;
            while (attempt < maxAttempts && config == null)
            {
                attempt++;
                try
                {
                    config = await handler.Options.ConfigurationManager!.GetConfigurationAsync(
                        cancellationToken
                    );
                    if (config.SigningKeys.Count == 0)
                    {
                        _logger.LogWarning(
                            "OIDC warm-up: configuration fetched but no signing keys (attempt {Attempt}/{Max})",
                            attempt,
                            maxAttempts
                        );
                        // Attempt direct JWKS fetch as fallback
                        if (!string.IsNullOrWhiteSpace(config.JwksUri))
                        {
                            try
                            {
                                using var http = new HttpClient();
                                http.Timeout = TimeSpan.FromSeconds(5);
                                var jwksJson = await http.GetStringAsync(config.JwksUri, cancellationToken);
                                var jwks = new Microsoft.IdentityModel.Tokens.JsonWebKeySet(jwksJson);
                                foreach (var k in jwks.GetSigningKeys())
                                {
                                    config.SigningKeys.Add(k);
                                }
                                if (config.SigningKeys.Count > 0)
                                {
                                    _logger.LogInformation("OIDC warm-up: fallback JWKS fetch succeeded. Keys={Count}", config.SigningKeys.Count);
                                }
                            }
                            catch (Exception jwksEx)
                            {
                                _logger.LogWarning(jwksEx, "OIDC warm-up: fallback JWKS fetch failed");
                            }
                        }
                        if (config.SigningKeys.Count == 0)
                        {
                        config = null; // treat as transient, retry
                        }
                    }
                }
                catch (Exception ex)
                    when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        ex,
                        "OIDC warm-up attempt {Attempt}/{Max} failed",
                        attempt,
                        maxAttempts
                    );
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                }
            }

            if (config == null)
            {
                _logger.LogError(
                    "OIDC warm-up failed after {Attempts} attempts; first sign-in may fail due to missing keys",
                    attempt
                );
                return;
            }

            var keyIds = config
                .SigningKeys.Select(k => k.KeyId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            _logger.LogInformation(
                "OIDC warm-up complete. Issuer: {Issuer}. Keys: {KeyCount}. Kids: {Kids}",
                config.Issuer,
                config.SigningKeys.Count,
                string.Join(',', keyIds)
            );

            // Assign the configuration (with signing keys) directly to handler options for immediate availability
            if (handler.Options.Configuration is null || handler.Options.Configuration.SigningKeys.Count == 0)
            {
                handler.Options.Configuration = config;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OIDC warm-up");
        }
    } // End of Method StartAsync

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask; // End of Method StopAsync
} // End of Class OidcWarmupHostedService
