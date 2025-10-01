// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TansuCloud.Observability;

namespace TansuCloud.Gateway.Observability;

internal sealed class YarpActivityEnricher(ILogger<YarpActivityEnricher> logger)
    : IHostedService,
        IDisposable
{
    private ActivityListener? _listener;
    private readonly ILogger<YarpActivityEnricher> _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                string.Equals(source.Name, "Yarp.ReverseProxy", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => TryEnrich(activity)
        };

        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
    }

    private void TryEnrich(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            var tenant = ResolveBaggage(activity, TelemetryConstants.Tenant);
            var routeBase = ResolveBaggage(activity, TelemetryConstants.RouteBase);
            var routeTemplate = ResolveBaggage(activity, TelemetryConstants.RouteTemplate);

            if (!string.IsNullOrEmpty(tenant))
            {
                activity.SetTag(TelemetryConstants.Tenant, tenant);
            }

            var (inferredTemplate, inferredUpstream) = GatewayRouteMetadata.Resolve(routeBase);

            var normalizedRouteBase = routeBase;
            if (string.IsNullOrWhiteSpace(normalizedRouteBase))
            {
                normalizedRouteBase = inferredUpstream ?? "gateway";
            }

            if (!string.IsNullOrWhiteSpace(normalizedRouteBase))
            {
                activity.SetTag(TelemetryConstants.RouteBase, normalizedRouteBase);
            }

            if (string.IsNullOrEmpty(routeTemplate))
            {
                routeTemplate = inferredTemplate;
            }

            if (!string.IsNullOrEmpty(inferredUpstream))
            {
                activity.SetTag(TelemetryConstants.UpstreamService, inferredUpstream);
            }

            if (!string.IsNullOrEmpty(routeTemplate))
            {
                activity.SetTag(TelemetryConstants.RouteTemplate, routeTemplate);
                activity.SetTag("http.route", routeTemplate);
            }

            if (
                activity.GetTagItem("http.status_code") is null
                && activity.Parent?.GetTagItem("http.status_code") is string parentStatus
            )
            {
                activity.SetTag("http.status_code", parentStatus);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to enrich YARP activity {ActivityName}",
                activity.DisplayName
            );
        }
    }

    private static string? ResolveBaggage(Activity activity, string key)
    {
        return activity.GetBaggageItem(key) ?? activity.Parent?.GetBaggageItem(key);
    }
}
