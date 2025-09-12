// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Hosting;

internal sealed class RequestMetricsMiddleware(
    RequestDelegate next,
    ILogger<RequestMetricsMiddleware> logger
)
{
    public async Task Invoke(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            var status = context.Response?.StatusCode ?? 0;
            var method = context.Request?.Method ?? "";
            var tenant = context.Request?.Headers["X-Tansu-Tenant"].ToString() ?? "";
            var statusClass =
                status >= 200 && status < 300
                    ? "2xx"
                    : status >= 400 && status < 500
                        ? "4xx"
                        : status >= 500 && status < 600
                            ? "5xx"
                            : "other";

            // Metrics
            StorageMetrics.Responses.Add(
                1,
                new("tenant", tenant),
                new("status", statusClass),
                new("op", method)
            );
            StorageMetrics.RequestDurationMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new("tenant", tenant),
                new("op", method),
                new("status", statusClass)
            );

            // Structured log (compact)
            logger.LogInformation(
                "{Method} {Path} -> {Status} in {ElapsedMs} ms (tenant={Tenant})",
                method,
                context.Request?.Path.Value,
                status,
                sw.Elapsed.TotalMilliseconds,
                tenant
            );
        }
    }
} // End of Class RequestMetricsMiddleware
