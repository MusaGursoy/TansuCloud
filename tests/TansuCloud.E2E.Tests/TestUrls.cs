// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System;
using System.Linq;

namespace TansuCloud.E2E.Tests;

internal static class TestUrls
{
    private const string DefaultLoopback = "http://127.0.0.1:8080";
    private const string DefaultGatewayInternal = "http://gateway:8080";
    private const string DefaultTelemetryBase = "http://127.0.0.1:5279";

    private static string? _publicBase;
    private static string? _gatewayBase;
    private static string? _telemetryBase;

    internal static string PublicBaseUrl =>
        _publicBase ??= Resolve(
            "PUBLIC_BASE_URL",
            DefaultLoopback,
            preferLoopback: !IsRunningInContainer()
        );

    internal static string GatewayBaseUrl =>
        _gatewayBase ??= Resolve(
            "GATEWAY_BASE_URL",
            fallback: IsRunningInContainer() ? DefaultGatewayInternal : PublicBaseUrl,
            preferLoopback: !IsRunningInContainer()
        );

    internal static string TelemetryBaseUrl =>
        _telemetryBase ??= Resolve(
            "TELEMETRY_BASE_URL",
            fallback: DefaultTelemetryBase,
            preferLoopback: !IsRunningInContainer()
        );

    internal static Uri PublicBaseUri => new(PublicBaseUrl);

    internal static Uri GatewayBaseUri => new(GatewayBaseUrl);

    internal static Uri TelemetryBaseUri => new(TelemetryBaseUrl);

    internal static string JoinGateway(params string[] segments) => Join(GatewayBaseUrl, segments);

    internal static string JoinPublic(params string[] segments) => Join(PublicBaseUrl, segments);

    internal static string JoinTelemetry(params string[] segments) =>
        Join(TelemetryBaseUrl, segments);

    private static string Resolve(string key, string fallback, bool preferLoopback)
    {
        TestEnvironment.EnsureInitialized();
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return NormalizeBase(fallback, preferLoopback);
        }

        return NormalizeBase(value, preferLoopback);
    }

    private static string NormalizeBase(string value, bool preferLoopback)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        trimmed = trimmed.TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };

            if (preferLoopback && IsLoopbackAlias(builder.Host))
            {
                builder.Host = "127.0.0.1";
            }
            else if (
                !preferLoopback && string.Equals(builder.Host, "::1", StringComparison.Ordinal)
            )
            {
                builder.Host = "localhost";
            }

            return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return trimmed;
    }

    private static string Join(string baseUrl, params string[] segments)
    {
        if (segments is null || segments.Length == 0)
        {
            return baseUrl.TrimEnd('/');
        }

        var builder = new UriBuilder(baseUrl)
        {
            Path = string.Join('/', segments.Select(s => s.Trim('/')))
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool IsLoopbackAlias(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "gateway", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || host == "::1";
    }

    private static bool IsRunningInContainer()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );
    }
} // End of Class TestUrls
