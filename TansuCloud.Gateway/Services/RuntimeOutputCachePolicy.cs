// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Custom OutputCache policy that reads TTL from runtime configuration.
/// </summary>
internal sealed class RuntimeOutputCachePolicy : IOutputCachePolicy
{
    private readonly IOutputCacheRuntime _runtime;
    private readonly bool _isStatic;

    public RuntimeOutputCachePolicy(IOutputCacheRuntime runtime, bool isStatic = false)
    {
        _runtime = runtime;
        _isStatic = isStatic;
    } // End of Constructor RuntimeOutputCachePolicy

    ValueTask IOutputCachePolicy.CacheRequestAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken
    )
    {
        var attemptOutputCaching = AttemptOutputCaching(context);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = attemptOutputCaching;
        context.AllowCacheStorage = attemptOutputCaching;
        context.AllowLocking = true;

        // Read TTL from runtime config
        var config = _runtime.GetCurrent();
        var ttl = _isStatic ? config.StaticTtlSeconds : config.DefaultTtlSeconds;
        context.ResponseExpirationTimeSpan = TimeSpan.FromSeconds(Math.Max(0, ttl));

        // Vary-by headers (set as StringValues, not Add)
        var headers = new List<string> { "Accept-Encoding" };
        if (!_isStatic)
        {
            headers.Add("X-Tansu-Tenant");
            headers.Add("Accept");
        }
        context.CacheVaryByRules.HeaderNames = new StringValues(headers.ToArray());

        // Vary by host
        context.CacheVaryByRules.VaryByHost = true;

        return ValueTask.CompletedTask;
    } // End of Method CacheRequestAsync

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken
    )
    {
        return ValueTask.CompletedTask;
    } // End of Method ServeFromCacheAsync

    ValueTask IOutputCachePolicy.ServeResponseAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken
    )
    {
        var response = context.HttpContext.Response;
        if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            context.AllowCacheStorage = false;
            return ValueTask.CompletedTask;
        }

        if (response.StatusCode != StatusCodes.Status200OK)
        {
            context.AllowCacheStorage = false;
            return ValueTask.CompletedTask;
        }

        return ValueTask.CompletedTask;
    } // End of Method ServeResponseAsync

    private static bool AttemptOutputCaching(OutputCacheContext context)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        return true;
    } // End of Method AttemptOutputCaching
} // End of Class RuntimeOutputCachePolicy
