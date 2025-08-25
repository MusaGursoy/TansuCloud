// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Text.RegularExpressions;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Resolves tenant information from host and path.
/// </summary>
public static class TenantResolver
{
    public enum Source
    {
        None,
        Subdomain,
        Path,
        Both
    }

    private static readonly Regex PathTenantRegex =
        new(
            pattern: "^/(?:[a-z0-9-_.]+/)?t/([a-z0-9-_.]+)(?:/|$)",
            options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
            matchTimeout: TimeSpan.FromMilliseconds(100)
        );

    private static readonly HashSet<string> ReservedHostnames =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1" };

    public sealed record Result(string? TenantId, Source From);

    public static Result Resolve(string host, string path)
    {
        var fromPath = TryFromPath(path);
        var fromSub = TryFromSubdomain(host);

        if (!string.IsNullOrEmpty(fromPath) && !string.IsNullOrEmpty(fromSub))
        {
            // Path wins when both present (explicit override)
            return new Result(fromPath, Source.Both);
        }

        if (!string.IsNullOrEmpty(fromPath))
            return new Result(fromPath, Source.Path);

        if (!string.IsNullOrEmpty(fromSub))
            return new Result(fromSub, Source.Subdomain);

        return new Result(null, Source.None);
    }

    public static string? TryFromPath(string path)
    {
        var m = PathTenantRegex.Match(path);
        if (!m.Success)
            return null;
        var tenant = m.Groups[1].Value;
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }

    public static string? TryFromSubdomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        // Strip port
        var h = host;
        var colon = h.IndexOf(':');
        if (colon >= 0)
            h = h[..colon];

        if (ReservedHostnames.Contains(h))
            return null;
        if (IPAddress.TryParse(h, out _))
            return null;

        // Extract first label as tenant; ignore 'www'
        var firstDot = h.IndexOf('.');
        if (firstDot <= 0)
            return null;
        var label = h[..firstDot];
        if (string.Equals(label, "www", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }
} // End of Class TenantResolver
