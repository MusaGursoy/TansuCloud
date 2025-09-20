// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;

namespace TansuCloud.Storage.Security;

internal static class ClaimsPrincipalExtensions
{
    // Robust scope check tolerant to different token shapes (space-separated, multiple scope claims, Azure 'scp', etc.)
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

        foreach (var s in principal.EnumerateScopes(comparer))
        {
            if (comparer.Equals(s, scope))
                return true;
        }
        return false;
    }

    // Enumerate scopes from common claim shapes without depending solely on OpenIddict helpers
    public static IEnumerable<string> EnumerateScopes(
        this ClaimsPrincipal principal,
        IEqualityComparer<string>? comparer = null
    )
    {
        comparer ??= StringComparer.Ordinal;
        if (principal is null)
            yield break;

        var seen = new HashSet<string>(comparer);

        // 1) Standard "scope" claims (may appear multiple times or as a single space-separated string)
        foreach (var c in principal.FindAll("scope"))
        {
            var v = c.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (v.Contains(' '))
            {
                foreach (var piece in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seen.Add(piece))
                        yield return piece;
                }
            }
            else
            {
                if (seen.Add(v))
                    yield return v;
            }
        }

        // 2) Azure AD style 'scp' claim
        foreach (var c in principal.FindAll("scp"))
        {
            var v = c.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (v.Contains(' '))
            {
                foreach (var piece in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seen.Add(piece))
                        yield return piece;
                }
            }
            else
            {
                if (seen.Add(v))
                    yield return v;
            }
        }

        // 3) Fallback to OpenIddict helper if nothing else found
        if (seen.Count == 0)
        {
            List<string>? buffer = null;
            try
            {
                foreach (var s in principal.GetScopes())
                {
                    if (seen.Add(s))
                    {
                        buffer ??= new List<string>();
                        buffer.Add(s);
                    }
                }
            }
            catch
            {
                // ignore
            }
            if (buffer is { Count: > 0 })
            {
                foreach (var s in buffer)
                    yield return s;
            }
        }
    }

    // Enumerate audiences robustly for diagnostics
    public static IEnumerable<string> EnumerateAudiences(this ClaimsPrincipal principal)
    {
        if (principal is null)
            yield break;

        // Multiple aud claims
        foreach (var c in principal.FindAll("aud"))
        {
            var v = c.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(v))
                continue;
            if (v.Length > 1 && v[0] == '[')
            {
                List<string>? buffer = null;
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(v);
                    if (arr is { Length: > 0 })
                    {
                        foreach (var item in arr)
                            if (!string.IsNullOrWhiteSpace(item))
                            {
                                buffer ??= new List<string>();
                                buffer.Add(item);
                            }
                    }
                }
                catch { }
                if (buffer is { Count: > 0 })
                {
                    foreach (var item in buffer)
                        yield return item;
                }
            }
            else
            {
                yield return v;
            }
        }
    }
} // End of Class ClaimsPrincipalExtensions
