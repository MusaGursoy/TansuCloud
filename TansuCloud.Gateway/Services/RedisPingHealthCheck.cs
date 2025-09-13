// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TansuCloud.Gateway.Services;

internal sealed class RedisPingHealthCheck(string connectionString)
    : IHealthCheck
{
    private readonly string _connectionString = connectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            var mux = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            var db = mux.GetDatabase();
            _ = await db.PingAsync();
            return HealthCheckResult.Healthy("Redis ping succeeded");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Redis ping failed", ex);
        }
    } // End of Method CheckHealthAsync
} // End of Class RedisPingHealthCheck
