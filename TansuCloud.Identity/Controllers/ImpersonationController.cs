// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TansuCloud.Identity.Infrastructure.Security;

namespace TansuCloud.Identity.Controllers;

[ApiController]
[Route("admin/impersonation")]
[Authorize(
    Roles = "Admin",
    AuthenticationSchemes = AuthenticationSchemeConstants.AdminCookieAndBearer
)]
public sealed class ImpersonationController(
    UserManager<IdentityUser> userManager,
    IOpenIddictScopeManager scopeManager,
    ISecurityAuditLogger audit
) : ControllerBase
{
    public sealed record StartImpersonationRequest(
        string UserId,
        int Minutes = 15,
        string[]? Scopes = null
    );

    [HttpPost("start")]
    public async Task<ActionResult> Start(
        [FromBody] StartImpersonationRequest req,
        CancellationToken ct
    )
    {
        var user = await userManager.FindByIdAsync(req.UserId);
        if (user is null)
            return NotFound();

        // Determine scopes: safe default is read-only scopes for services
        var requested =
            (req.Scopes is { Length: > 0 }) ? req.Scopes : new[] { "db.read", "storage.read" };
        // Validate requested scopes exist
        foreach (var s in requested)
        {
            if (await scopeManager.FindByNameAsync(s, ct) is null)
                return BadRequest(new { error = $"Unknown scope '{s}'" });
        }

        // Build new identity/principal for the impersonated user
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role
        );

        identity.SetClaim(OpenIddictConstants.Claims.Subject, user.Id);
        identity.SetClaim(OpenIddictConstants.Claims.Name, user.UserName ?? user.Email ?? user.Id);
        identity.SetClaim("impersonated", "true");
        identity.SetScopes(requested);

        // Short TTL override: 15 minutes default
        var ttl = TimeSpan.FromMinutes(Math.Clamp(req.Minutes, 1, 120));
        identity.SetAccessTokenLifetime(ttl);

        var principal = new ClaimsPrincipal(identity);

        await audit.LogAsync(
            "ImpersonationStarted",
            userId: req.UserId,
            actorId: User?.Identity?.Name
        );

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    } // End of Method Start

    [HttpPost("end")]
    public async Task<ActionResult> End([FromBody] string userId)
    {
        await audit.LogAsync("ImpersonationEnded", userId: userId, actorId: User?.Identity?.Name);
        return Ok(new { message = "Impersonation ended (no-op)" });
    } // End of Method End
}
