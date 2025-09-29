// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using TansuCloud.Observability.Shared.Configuration;

namespace TansuCloud.Identity.Infrastructure;

public static class DevSeeder
{
    public static async Task SeedAsync(
        IServiceProvider services,
        ILogger logger,
        IConfiguration config
    )
    {
        var roleMgr = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userMgr = services.GetRequiredService<UserManager<IdentityUser>>();
        var appMgr = services.GetRequiredService<IOpenIddictApplicationManager>();
        var appUrls = services.GetRequiredService<AppUrlsOptions>();

        static string CombineUrl(string baseUrl, string relative)
        {
            var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
            return new Uri(new Uri(normalizedBase), relative.TrimStart('/')).ToString();
        } // End of Method CombineUrl

        var publicBase = appUrls.PublicBaseUrl
            ?? throw new InvalidOperationException("PUBLIC_BASE_URL must be configured.");

        foreach (var role in new[] { "Admin", "User" })
        {
            if (await roleMgr.FindByNameAsync(role) is null)
                await roleMgr.CreateAsync(new IdentityRole(role));
        }

        var email = config["Seed:AdminEmail"] ?? "admin@tansu.local";
        var password = config["Seed:AdminPassword"] ?? "Passw0rd!";
        var user = await userMgr.FindByEmailAsync(email);
        if (user is null)
        {
            user = new IdentityUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true
            };
            var res = await userMgr.CreateAsync(user, password);
            if (res.Succeeded)
                await userMgr.AddToRoleAsync(user, "Admin");
            else
                logger.LogError(
                    "Admin creation failed: {Errors}",
                    string.Join(',', res.Errors.Select(e => e.Description))
                );
        }

        var dashboardClientId = config["Oidc:Dashboard:ClientId"] ?? "tansu-dashboard";
        var dashboardClientSecret = config["Oidc:Dashboard:ClientSecret"] ?? "dev-secret";
        var dashboardRedirectUri =
            config["Oidc:Dashboard:RedirectUri"]
            ?? CombineUrl(publicBase, "dashboard/signin-oidc");
        var dashboardRedirectUriRoot =
            config["Oidc:Dashboard:RedirectUriRoot"] ?? CombineUrl(publicBase, "signin-oidc");
        var dashboardPostLogoutUri =
            config["Oidc:Dashboard:PostLogoutRedirectUri"]
            ?? CombineUrl(publicBase, "dashboard/signout-callback-oidc");
        var dashboardPostLogoutUriRoot =
            config["Oidc:Dashboard:PostLogoutRedirectUriRoot"]
            ?? CombineUrl(publicBase, "signout-callback-oidc");

        var existing = await appMgr.FindByClientIdAsync(dashboardClientId);
        var enableClientCreds = services.GetRequiredService<IHostEnvironment>().IsDevelopment();
        if (existing is null)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = dashboardClientId,
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ClientSecret = dashboardClientSecret,
                ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
                DisplayName = "Tansu Dashboard",
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    OpenIddictConstants.Permissions.Prefixes.Scope + "openid",
                    OpenIddictConstants.Permissions.Scopes.Email,
                    OpenIddictConstants.Permissions.Scopes.Profile,
                    OpenIddictConstants.Permissions.Scopes.Roles,
                    OpenIddictConstants.Permissions.Prefixes.Scope + "offline_access",
                    // Custom API scopes
                    OpenIddictConstants.Permissions.Prefixes.Scope + "db.read",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "db.write",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "storage.read",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "storage.write",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "admin.full",
                    OpenIddictConstants.Permissions.Prefixes.Resource + "tansu.db",
                    OpenIddictConstants.Permissions.Prefixes.Resource + "tansu.storage",
                    OpenIddictConstants.Permissions.Prefixes.Resource + "tansu.identity"
                },
                Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
            };
            if (enableClientCreds)
            {
                descriptor.Permissions.Add(
                    OpenIddictConstants.Permissions.GrantTypes.ClientCredentials
                );
            }
            descriptor.RedirectUris.Add(new Uri(dashboardRedirectUri));
            descriptor.RedirectUris.Add(new Uri(dashboardRedirectUriRoot));
            descriptor.PostLogoutRedirectUris.Add(new Uri(dashboardPostLogoutUri));
            descriptor.PostLogoutRedirectUris.Add(new Uri(dashboardPostLogoutUriRoot));

            await appMgr.CreateAsync(descriptor);
        }
        else
        {
            // Ensure permissions/redirects are up to date (idempotent update)
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = dashboardClientId,
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                ClientSecret = dashboardClientSecret,
                ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
                DisplayName = "Tansu Dashboard",
                Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
            };

            descriptor.RedirectUris.Add(new Uri(dashboardRedirectUri));
            descriptor.RedirectUris.Add(new Uri(dashboardRedirectUriRoot));
            descriptor.PostLogoutRedirectUris.Add(new Uri(dashboardPostLogoutUri));
            descriptor.PostLogoutRedirectUris.Add(new Uri(dashboardPostLogoutUriRoot));

            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode
            );
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
            if (enableClientCreds)
            {
                descriptor.Permissions.Add(
                    OpenIddictConstants.Permissions.GrantTypes.ClientCredentials
                );
            }
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Email);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Profile);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Roles);
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "offline_access"
            );
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "db.read");
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "db.write");
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "storage.read"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "storage.write"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "admin.full"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Resource + "tansu.db"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Resource + "tansu.storage"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Resource + "tansu.identity"
            );

            await appMgr.UpdateAsync(existing, descriptor);
        }

        // Seed a public PKCE client for Postman/native tooling (dev only)
        var postmanClientId = config["Oidc:Postman:ClientId"] ?? "postman-pkce";
        var postmanRedirectUri =
            config["Oidc:Postman:RedirectUri"] ?? "https://oauth.pstmn.io/v1/callback";
        var postmanExisting = await appMgr.FindByClientIdAsync(postmanClientId);
        if (postmanExisting is null)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = postmanClientId,
                ClientType = OpenIddictConstants.ClientTypes.Public, // no client secret
                ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
                DisplayName = "Postman (PKCE)",
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    OpenIddictConstants.Permissions.Prefixes.Scope + "openid",
                    OpenIddictConstants.Permissions.Scopes.Email,
                    OpenIddictConstants.Permissions.Scopes.Profile,
                    OpenIddictConstants.Permissions.Scopes.Roles,
                    OpenIddictConstants.Permissions.Prefixes.Scope + "offline_access",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "db.read",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "db.write",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "storage.read",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "storage.write",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "admin.full"
                },
                Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
            };
            descriptor.RedirectUris.Add(new Uri(postmanRedirectUri));

            await appMgr.CreateAsync(descriptor);
        }
        else
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = postmanClientId,
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
                DisplayName = "Postman (PKCE)",
                Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
            };
            descriptor.RedirectUris.Add(new Uri(postmanRedirectUri));
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode
            );
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "openid");
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Email);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Profile);
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Scopes.Roles);
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "offline_access"
            );
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "db.read");
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + "db.write");
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "storage.read"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "storage.write"
            );
            descriptor.Permissions.Add(
                OpenIddictConstants.Permissions.Prefixes.Scope + "admin.full"
            );

            await appMgr.UpdateAsync(postmanExisting, descriptor);
        }
    }
} // End of Class DevSeeder
