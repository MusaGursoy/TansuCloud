// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Xunit;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Fact that auto-skips when REDIS_URL is not provided in environment variables.
/// Provides a consistent skip reason in test output.
/// </summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL");
        if (string.IsNullOrWhiteSpace(redisUrl))
        {
            Skip = "REDIS_URL not set; Redis-dependent test skipped"; // xUnit will report as Skipped.
        }
    } // End of Constructor RedisFactAttribute
} // End of Class RedisFactAttribute
