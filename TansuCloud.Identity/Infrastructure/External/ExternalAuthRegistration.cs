// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TansuCloud.Identity.Data;

namespace TansuCloud.Identity.Infrastructure.External;

/// <summary>
/// Registers OpenIdConnect external login schemes dynamically from the ExternalProviderSettings table.
/// For baseline, providers are loaded at startup; updating providers requires app restart.
/// </summary>
internal static class ExternalAuthRegistration
{
    public static void AddDynamicExternalOidcProviders(this IServiceCollection services)
    {
        // Identity already adds cookie schemes; ensure authentication builder exists
        var authBuilder = services.AddAuthentication(options =>
        {
            // Keep defaults as configured by Identity; do not override unless missing
            options.DefaultScheme ??= Microsoft
                .AspNetCore
                .Identity
                .IdentityConstants
                .ApplicationScheme;
            options.DefaultSignInScheme ??= Microsoft
                .AspNetCore
                .Identity
                .IdentityConstants
                .ExternalScheme;
        });

        // Build a temporary provider to query DB for provider settings at startup time.
        try
        {
            using var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<AppDbContext>();

            // Load enabled OIDC providers (table may not exist yet during first run)
            var providers = db
                .ExternalProviderSettings.AsNoTracking()
                .Where(p => p.Enabled && p.Provider == "oidc")
                .ToList();

            foreach (var p in providers)
            {
                var scheme = $"oidc-{p.TenantId}-{p.Id.ToString(CultureInfo.InvariantCulture)}";
                authBuilder.AddOpenIdConnect(
                    scheme,
                    options =>
                    {
                        options.SignInScheme = Microsoft
                            .AspNetCore
                            .Identity
                            .IdentityConstants
                            .ExternalScheme;
                        options.Authority = p.Authority;
                        options.ClientId = p.ClientId;
                        if (!string.IsNullOrWhiteSpace(p.ClientSecret))
                        {
                            options.ClientSecret = p.ClientSecret;
                        }
                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.SaveTokens = true;
                        options.GetClaimsFromUserInfoEndpoint = true;
                        options.MapInboundClaims = false; // use original claim types

                        // Scopes: start clean then add requested ones
                        options.Scope.Clear();
                        if (!string.IsNullOrWhiteSpace(p.Scopes))
                        {
                            foreach (
                                var s in p.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            )
                            {
                                options.Scope.Add(s);
                            }
                        }
                        else
                        {
                            options.Scope.Add("openid");
                            options.Scope.Add("profile");
                            options.Scope.Add("email");
                        }
                    }
                );
            }
        }
        catch
        {
            // Swallow and proceed: providers can be added later; baseline loads on next restart.
        }
    }
} // End of Class ExternalAuthRegistration
