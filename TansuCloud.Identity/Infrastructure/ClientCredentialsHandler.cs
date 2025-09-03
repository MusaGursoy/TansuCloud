// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using System.Collections.Generic;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace TansuCloud.Identity.Infrastructure;

/// <summary>
/// Minimal handler to issue access tokens for the client_credentials flow.
/// Creates a claims principal representing the calling application and assigns requested scopes.
/// OpenIddict's built-in validation already authenticated the client (id/secret) and validated scopes.
/// </summary>
public sealed class ClientCredentialsHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.HandleTokenRequestContext>
{
    public ValueTask HandleAsync(OpenIddictServerEvents.HandleTokenRequestContext context)
    {
        // Only handle client credentials grant. Let other grants fall through to default handlers.
        if (!context.Request.IsClientCredentialsGrantType())
        {
            return default;
        }

        // Build an identity for the calling client application.
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        // Subject is the client_id for client credentials.
        if (!string.IsNullOrEmpty(context.ClientId))
        {
            identity.AddClaim(
                OpenIddictConstants.Claims.Subject,
                context.ClientId,
                OpenIddictConstants.Destinations.AccessToken
            );
            identity.AddClaim(
                OpenIddictConstants.Claims.Name,
                context.ClientId,
                OpenIddictConstants.Destinations.AccessToken
            );
        }

        var principal = new ClaimsPrincipal(identity);

        // Propagate requested scopes (OpenIddict already ensures they are permitted for this client).
        principal.SetScopes(context.Request.GetScopes());

        // Ensure audiences are set based on requested scopes, so resource servers can enforce aud checks.
        var scopes = context.Request.GetScopes();
        var audiences = new List<string>(2);
        if (scopes.Contains("db.read") || scopes.Contains("db.write") || scopes.Contains("admin.full"))
        {
            audiences.Add("tansu.db");
        }
        if (scopes.Contains("storage.read") || scopes.Contains("storage.write") || scopes.Contains("admin.full"))
        {
            audiences.Add("tansu.storage");
        }
        if (audiences.Count > 0)
        {
            principal.SetAudiences(audiences);
        }

        // Resources/audiences are derived later by TokenClaimsHandler based on scopes.
        context.Principal = principal;
        return default;
    }
} // End of Class ClientCredentialsHandler
