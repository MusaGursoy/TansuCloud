// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace TansuCloud.Observability;

/// <summary>
/// Health check that verifies two readiness conditions:
/// 1) Activity uses W3C id format.
/// 2) The configured OTLP endpoint is reachable at TCP level (host:port).
/// 
/// In Development, OTLP connectivity failure downgrades to Degraded (HTTP 200) to reduce flakiness
/// during local bring-up; in non-Development, it returns Unhealthy.
/// </summary>
public sealed class OtlpConnectivityHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public OtlpConnectivityHealthCheck(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    } // End of Constructor OtlpConnectivityHealthCheck

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var w3cOk = Activity.DefaultIdFormat == ActivityIdFormat.W3C;

        // Resolve OTLP endpoint similar to exporter defaults
        var (endpoint, reason) = ResolveOtlpEndpoint(_configuration);

        // Default to Degraded if endpoint cannot be resolved; include reason for operators
        if (endpoint is null)
        {
            var status = _environment.IsDevelopment() ? HealthStatus.Degraded : HealthStatus.Unhealthy;
            var desc = $"OTLP endpoint not configured and no default could be resolved: {reason}";
            return new HealthCheckResult(status, description: CombineDesc(w3cOk, desc));
        }

        // Try TCP connect with a short timeout (configurable)
        var timeoutMs = _configuration.GetValue<int?>("OpenTelemetry:Otlp:Health:ConnectTimeoutMs")
            ?? (_environment.IsDevelopment() ? 1500 : 3000);

        var reachable = await TcpConnectAsync(endpoint.Host, endpoint.Port, timeoutMs, cancellationToken).ConfigureAwait(false);

        // Compose description and data
        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["activity.defaultIdFormat"] = Activity.DefaultIdFormat.ToString(),
            ["activity.forceDefaultIdFormat"] = Activity.ForceDefaultIdFormat,
            ["otlp.endpoint"] = endpoint.ToString(),
            ["otlp.tcpReachable"] = reachable,
        };

        if (!w3cOk)
        {
            details["warning"] = "Activity.DefaultIdFormat != W3C";
        }

        // Normalize data dictionary to non-nullable object values for HealthCheckResult
        var data = details
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value!, StringComparer.OrdinalIgnoreCase);

        if (reachable)
        {
            return HealthCheckResult.Healthy(CombineDesc(w3cOk, "OTLP reachable"), data);
        }
        else
        {
            var status = _environment.IsDevelopment() ? HealthStatus.Degraded : HealthStatus.Unhealthy;
            var desc = CombineDesc(w3cOk, $"OTLP not reachable at {endpoint.Host}:{endpoint.Port}");
            return new HealthCheckResult(status, description: desc, exception: null, data: data);
        }
    } // End of Method CheckHealthAsync

    private static async Task<bool> TcpConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(250, timeoutMs)));
            var completed = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
            if (completed == connectTask)
            {
                // Await to propagate exceptions
                await connectTask.ConfigureAwait(false);
                return client.Connected;
            }
            return false; // timeout
        }
        catch
        {
            return false;
        }
    } // End of Method TcpConnectAsync

    private static (Uri? endpoint, string reason) ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry:Otlp");
        var endpointRaw = section["Endpoint"];
        if (string.IsNullOrWhiteSpace(endpointRaw))
        {
            var inContainer = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase
            );
            endpointRaw = inContainer ? "http://signoz-otel-collector:4317" : "http://127.0.0.1:4317";
        }

        if (Uri.TryCreate(endpointRaw, UriKind.Absolute, out var uri))
        {
            // If port is missing, infer from scheme
            var port = uri.Port;
            if (port <= 0)
            {
                var inferred = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
                uri = new UriBuilder(uri) { Port = inferred }.Uri;
            }
            return (uri, string.Empty);
        }

        return (null, $"invalid URI '{endpointRaw}'");
    } // End of Method ResolveOtlpEndpoint

    private static string CombineDesc(bool w3cOk, string core)
        => w3cOk ? core : $"{core}; ActivityIdFormat != W3C";
} // End of Class OtlpConnectivityHealthCheck
