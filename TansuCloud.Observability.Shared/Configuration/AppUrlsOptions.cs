// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TansuCloud.Observability.Shared.Configuration;

/// <summary>
/// Centralized URL configuration for browser-visible and backchannel endpoints.
/// Follows the repository standard:
/// - One PublicBaseUrl for all browser-visible links and OIDC issuer (dev: http://127.0.0.1:8080/).
/// - Backchannel discovery/JWKS for in-cluster services should go via gateway host, not 127.0.0.1.
/// </summary>
public sealed class AppUrlsOptions
{
    /// <summary>
    /// Configuration section name. We bind from root for simple env usage as well, e.g., PUBLIC_BASE_URL.
    /// </summary>
    public const string SectionName = "Urls";

    /// <summary>
    /// Browser-visible base URL, e.g., http://127.0.0.1:8080
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// In-cluster gateway base URL, e.g., http://gateway:8080
    /// Used for backchannel discovery/JWKS in containers.
    /// </summary>
    public string? GatewayBaseUrl { get; init; }

    /// <summary>
    /// Opt-in loopback canonicalization (dev only) per repo guidance.
    /// </summary>
    public bool CanonicalizeLoopback { get; init; }

    /// <summary>
    /// Bind from IConfiguration considering both hierarchical and flattened env (PUBLIC_BASE_URL, GATEWAY_BASE_URL).
    /// </summary>
    public static AppUrlsOptions FromConfiguration(IConfiguration config)
    {
        // Prefer explicit env keys; fall back to section binding.
        var publicBase =
            config["PublicBaseUrl"] ?? config["Urls:PublicBaseUrl"] ?? config["PUBLIC_BASE_URL"];

        var gatewayBase =
            config["GatewayBaseUrl"] ?? config["Urls:GatewayBaseUrl"] ?? config["GATEWAY_BASE_URL"];

        var canonicalize = false;
        var canonEnv = config["DASHBOARD_CANONICALIZE_LOOPBACK"];
        if (
            !string.IsNullOrWhiteSpace(canonEnv)
            && (canonEnv == "1" || canonEnv.Equals("true", StringComparison.OrdinalIgnoreCase))
        )
        {
            canonicalize = true;
        }

        var options = new AppUrlsOptions
        {
            PublicBaseUrl = NormalizeBase(publicBase, canonicalize),
            GatewayBaseUrl = TrimEndSlash(gatewayBase),
            CanonicalizeLoopback = canonicalize,
        };

        options.EnsureValid();
        return options;
    }

    /// <summary>
    /// Issuer (with trailing slash) under the identity base path.
    /// Example (dev): http://127.0.0.1:8080/identity/
    /// </summary>
    public string GetIssuer(string identityPath = "identity")
    {
        var pub = Require(PublicBaseUrl, nameof(PublicBaseUrl));
        var baseWithSlash = EnsureTrailingSlash(pub);
        var path = identityPath.Trim('/');
        return $"{baseWithSlash}{path}/";
    }

    /// <summary>
    /// Authority (without trailing slash) for browser/OIDC client flows.
    /// Example (dev): http://127.0.0.1:8080/identity
    /// </summary>
    public string GetAuthority(string identityPath = "identity")
    {
        var issuer = GetIssuer(identityPath);
        return issuer.TrimEnd('/');
    }

    /// <summary>
    /// Backchannel discovery URL for services validating JWTs.
    /// Resolution order per repo contract:
    /// 1) If GatewayBaseUrl set → {gateway}/identity/.well-known/openid-configuration
    /// 2) Else if inContainer → http://gateway:8080/identity/.well-known/openid-configuration
    /// 3) Else derive from PublicBaseUrl/Issuer
    /// </summary>
    public string GetBackchannelMetadataAddress(bool inContainer, string identityPath = "identity")
    {
        var path = identityPath.Trim('/');
        if (!string.IsNullOrWhiteSpace(GatewayBaseUrl))
        {
            var g = EnsureTrailingSlash(GatewayBaseUrl!);
            return $"{g}{path}/.well-known/openid-configuration";
        }

        if (inContainer)
        {
            // Default gateway host for compose network
            return $"http://gateway:8080/{path}/.well-known/openid-configuration";
        }

        // Fallback to issuer-derived
        var issuer = GetIssuer(identityPath);
        return $"{issuer}.well-known/openid-configuration";
    }

    // Note: Extension method is defined in the static class below.

    private void EnsureValid()
    {
        EnsureValidHttpUrl(PublicBaseUrl, nameof(PublicBaseUrl));
        EnsureValidHttpUrl(GatewayBaseUrl, nameof(GatewayBaseUrl));
    }

    private static string? NormalizeBase(string? value, bool canonicalizeLoopback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        trimmed = TrimEndSlash(trimmed);

        if (canonicalizeLoopback)
        {
            trimmed = (trimmed ?? string.Empty)
                .Replace("localhost:", "127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                .Replace("://localhost", "://127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed;
    }

    [return: NotNull]
    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required configuration: {name}");
        return value;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    private static string? TrimEndSlash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : value.TrimEnd('/');

    private static void EnsureValidHttpUrl(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Missing required configuration value for {name}. Populate PUBLIC_BASE_URL and GATEWAY_BASE_URL in your .env or configuration store."
            );
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Configuration value for {name} must be an absolute URI. Current value: '{value}'."
            );
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Configuration value for {name} must use http or https. Current value: '{value}'."
            );
        }

        if (!string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration value for {name} must not include a path component. Provided value: '{value}'."
            );
        }
    }
} // End of Class AppUrlsOptions

/// <summary>
/// ServiceCollection extensions for binding <see cref="AppUrlsOptions"/> from configuration.
/// </summary>
public static class AppUrlsOptionsServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="AppUrlsOptions"/> as a singleton resolved from the provided configuration.
    /// Supports flattened env keys (PUBLIC_BASE_URL, GATEWAY_BASE_URL) and Urls section.
    /// </summary>
    public static IServiceCollection AddAppUrlsOptions(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        var opts = AppUrlsOptions.FromConfiguration(config);
        return services.AddSingleton(opts);
    }
} // End of Class AppUrlsOptionsServiceCollectionExtensions
