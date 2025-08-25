// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Generic;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.OutputCaching;
using TansuCloud.Gateway.Services;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Safety controls: Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Partition key: route prefix + tenant id for fairness
        var path = context.Request.Path;
        var prefix = path.HasValue ? path.Value!.TrimStart('/') : string.Empty;
        var first = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.Split('/', 2)[0];
        var tenant = context.Request.Headers["X-Tansu-Tenant"].ToString();
        var key = $"{first}|{tenant}";

        // Different limits per route family
        var permitLimit = first switch
        {
            "db" => 200, // DB APIs
            "storage" => 150, // Storage APIs
            "identity" => 300, // Auth endpoints
            "dashboard" => 300, // UI
            _ => 100
        };

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = permitLimit
            }
        );
    });
});

// Output caching: tenant-aware variation and safe defaults
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(policy =>
        policy
            .Cache() // applies default: only GET/HEAD, 200s, not authenticated
            .SetVaryByHeader("X-Tansu-Tenant", "Accept", "Accept-Encoding")
            .SetVaryByHost(true)
    );
});

// Add YARP Reverse Proxy from in-memory config so we don't depend on JSON comment support
builder
    .Services.AddReverseProxy()
    .LoadFromMemory(
        // Routes
        new[]
        {
            new RouteConfig
            {
                RouteId = "dashboard-route",
                ClusterId = "dashboard",
                Match = new RouteMatch { Path = "/dashboard/{**catch-all}" },
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                    // Preserve original Host header for downstream apps
                    new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                    // Propagate a base path hint to downstream (useful for UI routing)
                    new Dictionary<string, string>
                    {
                        ["RequestHeader"] = "X-Forwarded-Prefix",
                        ["Set"] = "/dashboard"
                    },
                    // Copy request/response headers (ensures trace headers like traceparent are forwarded)
                    new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                    new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
                }
            },
            new RouteConfig
            {
                RouteId = "identity-route",
                ClusterId = "identity",
                Match = new RouteMatch { Path = "/identity/{**catch-all}" },
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/identity" },
                    new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                    new Dictionary<string, string>
                    {
                        ["RequestHeader"] = "X-Forwarded-Prefix",
                        ["Set"] = "/identity"
                    },
                    new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                    new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
                }
            },
            new RouteConfig
            {
                RouteId = "db-route",
                ClusterId = "db",
                Match = new RouteMatch { Path = "/db/{**catch-all}" },
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/db" },
                    new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                    new Dictionary<string, string>
                    {
                        ["RequestHeader"] = "X-Forwarded-Prefix",
                        ["Set"] = "/db"
                    },
                    new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                    new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
                }
            },
            new RouteConfig
            {
                RouteId = "storage-route",
                ClusterId = "storage",
                Match = new RouteMatch { Path = "/storage/{**catch-all}" },
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/storage" },
                    new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                    new Dictionary<string, string>
                    {
                        ["RequestHeader"] = "X-Forwarded-Prefix",
                        ["Set"] = "/storage"
                    },
                    new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                    new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
                }
            }
        },
        // Clusters -> use dev ports from respective projects
        new[]
        {
            new ClusterConfig
            {
                ClusterId = "dashboard",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["d1"] = new() { Address = "http://localhost:5136" }
                }
            },
            new ClusterConfig
            {
                ClusterId = "identity",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["i1"] = new() { Address = "http://localhost:5095" }
                }
            },
            new ClusterConfig
            {
                ClusterId = "db",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["db1"] = new() { Address = "http://localhost:5278" }
                }
            },
            new ClusterConfig
            {
                ClusterId = "storage",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["s1"] = new() { Address = "http://localhost:5257" }
                }
            }
        }
    );

var app = builder.Build();

// Support WebSockets through the gateway
app.UseWebSockets();

// Global Rate Limiter
app.UseRateLimiter();

// Redirect HTTP -> HTTPS in dev to enforce TLS termination at the gateway
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Output cache middleware (before proxy)
app.UseOutputCache();

// Resolve tenant from host or path and stamp a header for downstream services
app.Use(
    async (context, next) =>
    {
        var result = TenantResolver.Resolve(context.Request.Host.Host, context.Request.Path);
        if (!string.IsNullOrWhiteSpace(result.TenantId))
        {
            context.Request.Headers["X-Tansu-Tenant"] = result.TenantId!;
        }
        await next();
    }
); // End of Middleware Tenant Header

// Enforce simple per-route auth guard at gateway until Identity is wired
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path;
        var requiresAuth = path.StartsWithSegments("/db") || path.StartsWithSegments("/storage");

        if (requiresAuth)
        {
            // Reject if no Authorization header present
            if (!context.Request.Headers.ContainsKey("Authorization"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                return;
            }
        }

        // If Authorization header present, instruct downstream to avoid caching the response
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            context.Response.Headers["Cache-Control"] = "no-store";
        }

        await next();
    }
); // End of Middleware Simple Auth Guard & Cache Bypass

// Request body size limits per route family
app.Use(
    async (context, next) =>
    {
        var feature =
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            long? limit = null;
            var p = context.Request.Path;
            if (p.StartsWithSegments("/storage"))
                limit = 100L * 1024 * 1024; // 100 MB
            else if (p.StartsWithSegments("/db"))
                limit = 10L * 1024 * 1024; // 10 MB
            else if (p.StartsWithSegments("/identity"))
                limit = 2L * 1024 * 1024; // 2 MB
            else if (p.StartsWithSegments("/dashboard"))
                limit = 10L * 1024 * 1024; // 10 MB

            if (limit.HasValue)
            {
                feature.MaxRequestBodySize = limit.Value;
            }
        }
        await next();
    }
); // End of Middleware Body Size Limits

// Simple root endpoint
app.MapGet("/", () => "TansuCloud Gateway is running");

// Map the reverse proxy endpoints
app.MapReverseProxy();

app.Run();
