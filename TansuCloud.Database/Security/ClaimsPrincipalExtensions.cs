// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using OpenIddict.Abstractions;

namespace TansuCloud.Database.Security;

internal static class ClaimsPrincipalExtensions
{
    public static bool HasScope(this ClaimsPrincipal principal, string scope)
        => principal.HasScope(scope, StringComparer.Ordinal);

    public static bool HasScope(this ClaimsPrincipal principal, string scope, IEqualityComparer<string> comparer)
    {
        if (principal is null) return false;
        var scopes = principal.GetScopes();
        return scopes.Contains(scope, comparer);
    }
} // End of Class ClaimsPrincipalExtensions
