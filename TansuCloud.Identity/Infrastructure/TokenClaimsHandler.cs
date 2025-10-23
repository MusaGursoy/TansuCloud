// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TansuCloud.Identity.Data.Entities;
using TansuCloud.Identity.Infrastructure.Security;
using AspNetClaimTypes = System.Security.Claims.ClaimTypes;

namespace TansuCloud.Identity.Infrastructure;

public sealed class TokenClaimsHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IConfiguration _config;
    private readonly ISecurityAuditLogger _audit;
    private readonly IOptions<Options.IdentityPolicyOptions> _policy;
    private readonly ILogger<TokenClaimsHandler>? _logger;

    public TokenClaimsHandler(
        IHttpContextAccessor httpContextAccessor,
        UserManager<IdentityUser> userManager,
        IConfiguration config,
        ISecurityAuditLogger audit,
        IOptions<Options.IdentityPolicyOptions> policy,
        ILogger<TokenClaimsHandler>? logger = null
    )
    {
        _httpContextAccessor = httpContextAccessor;
        _userManager = userManager;
        _config = config;
        _audit = audit;
        _policy = policy;
        _logger = logger;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        var principal =
            context.Principal ?? throw new InvalidOperationException("Missing principal");

        _logger?.LogInformation(
            "ProcessSignIn for {Subject} initial scopes={Scopes}",
            principal.GetClaim(OpenIddictConstants.Claims.Subject) ?? "unknown",
            string.Join(' ', principal.GetScopes())
        );

        // Tenant id from gateway header if present
        var tenant = _httpContextAccessor.HttpContext?.Request.Headers["X-Tansu-Tenant"].ToString();
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            principal.SetClaim(ClaimTypes.TenantId, tenant);
        }

        // Optional plan/quotas
        var plan = _httpContextAccessor.HttpContext?.Request.Headers["X-Tansu-Plan"].ToString();
        plan ??= _config["Plan:Default"] ?? "dev";
        principal.SetClaim(ClaimTypes.Plan, plan);

        // Resources/audiences based on scopes so resource servers can enforce aud checks
        var audiences = new List<string>(2);
        var resources = new List<string>(2);
        if (principal.HasScope("db.read") || principal.HasScope("db.write"))
        {
            audiences.Add("tansu.db");
            resources.Add("tansu.db");
        }
        if (principal.HasScope("storage.read") || principal.HasScope("storage.write"))
        {
            audiences.Add("tansu.storage");
            resources.Add("tansu.storage");
        }

        if (principal.HasScope("admin.full"))
        {
            audiences.Add("tansu.identity");
            resources.Add("tansu.identity");
        }
        if (audiences.Count > 0)
        {
            principal.SetAudiences(audiences);
            principal.SetResources(resources);
        }

        // Ensure roles are present when scope "roles" is granted
        if (principal.HasScope(OpenIddictConstants.Scopes.Roles))
        {
            var userId = principal.GetClaim(OpenIddictConstants.Claims.Subject);
            if (!string.IsNullOrEmpty(userId))
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (user is not null)
                {
                    var roles = await _userManager.GetRolesAsync(user);
                    if (roles.Count > 0 && principal.Identity is ClaimsIdentity identity)
                    {
                        var openIddictRoleClaims = new HashSet<string>(
                            identity.FindAll(OpenIddictConstants.Claims.Role).Select(c => c.Value),
                            StringComparer.OrdinalIgnoreCase
                        );
                        var aspNetRoleClaims = new HashSet<string>(
                            identity.FindAll(AspNetClaimTypes.Role).Select(c => c.Value),
                            StringComparer.OrdinalIgnoreCase
                        );

                        foreach (var role in roles)
                        {
                            if (openIddictRoleClaims.Add(role))
                            {
                                identity.AddClaim(new Claim(OpenIddictConstants.Claims.Role, role));
                            }

                            if (aspNetRoleClaims.Add(role))
                            {
                                identity.AddClaim(new Claim(AspNetClaimTypes.Role, role));
                            }
                        }
                    }
                }
            }
        }

        // Optional: require MFA based on policy (skip for impersonation tokens)
        if (_policy.Value.RequireMfa)
        {
            var isImpersonated = string.Equals(
                principal.GetClaim("impersonated"),
                "true",
                StringComparison.OrdinalIgnoreCase
            );
            var amr = principal.GetClaim("amr");
            var hasMfa =
                string.Equals(amr, "mfa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(amr, "otp", StringComparison.OrdinalIgnoreCase);
            if (!isImpersonated && !hasMfa)
            {
                await _audit.LogAsync(
                    "TokenRejectedMfaRequired",
                    userId: principal.GetClaim(OpenIddictConstants.Claims.Subject)
                );
                context.Reject(
                    error: OpenIddictConstants.Errors.ConsentRequired,
                    description: "Multi-factor authentication required."
                );
                return;
            }
        }

        // Destinations: include selected claims in access and/or identity tokens
        principal.SetDestinations(claim =>
            claim.Type switch
            {
                OpenIddictConstants.Claims.Subject
                    => new[]
                    {
                        OpenIddictConstants.Destinations.AccessToken,
                        OpenIddictConstants.Destinations.IdentityToken
                    },
                OpenIddictConstants.Claims.Name
                    => new[]
                    {
                        OpenIddictConstants.Destinations.AccessToken,
                        OpenIddictConstants.Destinations.IdentityToken
                    },
                OpenIddictConstants.Claims.Email
                    => principal.HasScope(OpenIddictConstants.Scopes.Email)
                        ? new[]
                        {
                            OpenIddictConstants.Destinations.AccessToken,
                            OpenIddictConstants.Destinations.IdentityToken
                        }
                        : Array.Empty<string>(),
                OpenIddictConstants.Claims.Role
                    => principal.HasScope(OpenIddictConstants.Scopes.Roles)
                        ? new[]
                        {
                            OpenIddictConstants.Destinations.AccessToken,
                            OpenIddictConstants.Destinations.IdentityToken
                        }
                        : Array.Empty<string>(),
                AspNetClaimTypes.Role
                    => principal.HasScope(OpenIddictConstants.Scopes.Roles)
                        ? new[]
                        {
                            OpenIddictConstants.Destinations.AccessToken,
                            OpenIddictConstants.Destinations.IdentityToken
                        }
                        : Array.Empty<string>(),
                ClaimTypes.TenantId
                    => new[]
                    {
                        OpenIddictConstants.Destinations.AccessToken,
                        OpenIddictConstants.Destinations.IdentityToken
                    },
                ClaimTypes.Plan
                    => new[]
                    {
                        OpenIddictConstants.Destinations.AccessToken,
                        OpenIddictConstants.Destinations.IdentityToken
                    },
                "impersonated"
                    => new[]
                    {
                        OpenIddictConstants.Destinations.AccessToken,
                        OpenIddictConstants.Destinations.IdentityToken
                    },
                _ => new[] { OpenIddictConstants.Destinations.AccessToken }
            }
        );

        // Audit token issuance
        var sub = principal.GetClaim(OpenIddictConstants.Claims.Subject);
        _logger?.LogInformation(
            "Token prepared for {Subject} scopes={Scopes} audiences={Audiences}",
            sub ?? "unknown",
            string.Join(' ', principal.GetScopes()),
            string.Join(',', principal.GetAudiences())
        );
        await _audit.LogAsync("TokenIssued", userId: sub, details: null);
    } // End of Method HandleAsync
} // End of Class TokenClaimsHandler
