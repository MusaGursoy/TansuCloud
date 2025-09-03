// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;

namespace TansuCloud.Database.Security;

internal static class ClaimsPrincipalExtensions
{
    public static bool HasScope(this ClaimsPrincipal principal, string scope) =>
        principal.HasScope(scope, StringComparer.Ordinal);

    public static bool HasScope(
        this ClaimsPrincipal principal,
        string scope,
        IEqualityComparer<string> comparer
    )
    {
        if (principal is null)
            return false;

        // Prefer robust manual extraction to tolerate different token formats/middleware:
        // - Multiple "scope" claims
        // - Single space-separated "scope" string
        // - Azure-style "scp" claim
        // - OpenIddict GetScopes() fallback
        var set = new HashSet<string>(comparer);

        foreach (var c in principal.FindAll("scope"))
        {
            var v = c.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (v.Contains(' '))
            {
                foreach (var piece in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    set.Add(piece);
            }
            else
            {
                set.Add(v);
            }
        }

        foreach (var c in principal.FindAll("scp"))
        {
            var v = c.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (v.Contains(' '))
            {
                foreach (var piece in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    set.Add(piece);
            }
            else
            {
                set.Add(v);
            }
        }

        if (set.Count == 0)
        {
            // Fallback to OpenIddict-provided helper if available
            try
            {
                foreach (var s in principal.GetScopes())
                    set.Add(s);
            }
            catch
            {
                // ignore and rely on set being empty if OpenIddict extensions are unavailable
            }
        }

        return set.Contains(scope);
    }

    public static bool HasAudience(this ClaimsPrincipal principal, string audience)
    {
        if (principal is null)
            return false;
        var audClaims = principal.FindAll("aud").Select(c => c.Value).ToList();
        if (audClaims.Count == 0)
            return false;
        // Direct match among repeated aud claims
        if (audClaims.Any(v => string.Equals(v, audience, StringComparison.Ordinal)))
            return true;
        // Some handlers serialize audiences as a JSON array in a single claim
        foreach (var v in audClaims)
        {
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (v.Length > 1 && v[0] == '[')
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(v);
                    if (arr?.Contains(audience) == true)
                        return true;
                }
                catch { }
            }
        }
        return false;
    }
} // End of Class ClaimsPrincipalExtensions
