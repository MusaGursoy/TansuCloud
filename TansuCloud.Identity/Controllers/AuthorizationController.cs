// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace TansuCloud.Identity.Controllers;

[AllowAnonymous]
public sealed class AuthorizationController : Controller
{
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly ILogger<AuthorizationController> _logger;

    public AuthorizationController(
        SignInManager<IdentityUser> signInManager,
        UserManager<IdentityUser> userManager,
        ILogger<AuthorizationController> logger
    )
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
    } // End of Constructor AuthorizationController

    [HttpGet]
    [Route("/connect/authorize")]
    public async Task<IActionResult> AuthorizeAsync()
    {
        // Minimal parsing: read requested scopes from query for inclusion in the principal
        var scopeParam = Request.Query[OpenIddictConstants.Parameters.Scope].ToString();
        var requestedScopes = string.IsNullOrWhiteSpace(scopeParam)
            ? Array.Empty<string>()
            : scopeParam.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );

        if (!User.Identity?.IsAuthenticated ?? true)
        {
            // Challenge the ASP.NET Identity cookie scheme to force login, then return to this authorize URL
            var queryPairs = Request.Query.Select(kvp => new KeyValuePair<string, string?>(
                kvp.Key,
                kvp.Value.ToString()
            ));
            var returnUrl = Request.Path + QueryString.Create(queryPairs);
            try
            {
                var queryKeys = string.Join(",", Request.Query.Keys);
                var hasClientId = Request.Query.ContainsKey(OpenIddictConstants.Parameters.ClientId);
                _logger.LogInformation(
                    "Authorize challenge triggered for unauthenticated user. Path={Path} QueryKeys={QueryKeys} HasClientId={HasClientId} ReturnUrlLength={ReturnUrlLength}",
                    Request.Path,
                    queryKeys,
                    hasClientId,
                    returnUrl?.Length ?? 0
                );
                if (!hasClientId)
                {
                    _logger.LogWarning(
                        "Authorize challenge missing client_id parameter. RawQuery={Query}",
                        Request.QueryString.ToString()
                    );
                }
            }
            catch
            {
                // Logging failures should not block authorization flow
            }
            return Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl },
                IdentityConstants.ApplicationScheme
            );
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _signInManager.SignOutAsync();
            return Challenge(
                new AuthenticationProperties { RedirectUri = Request.Path + Request.QueryString },
                IdentityConstants.ApplicationScheme
            );
        }

        // Build claims identity for the current user
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role
        );

        identity.AddClaim(OpenIddictConstants.Claims.Subject, user.Id);
        identity.AddClaim(OpenIddictConstants.Claims.Name, user.UserName ?? user.Email ?? user.Id);

        var roles = await _userManager.GetRolesAsync(user);
        foreach (var role in roles)
        {
            identity.AddClaim(ClaimTypes.Role, role);
        }

        var principal = new ClaimsPrincipal(identity);
        if (requestedScopes.Length > 0)
        {
            principal.SetScopes(requestedScopes);
        }

        // Issue the authorization code by returning a SignIn result with the OpenIddict authentication scheme
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    } // End of Method AuthorizeAsync
} // End of Class AuthorizationController
