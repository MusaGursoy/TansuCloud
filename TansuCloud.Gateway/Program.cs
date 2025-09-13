// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Generic;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Gateway.Services;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: enable HTTP/2 TCP keepalive pings for long-lived connections
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    kestrel.AddServerHeader = false;
});

// If provided, feed the HTTPS certificate password into Kestrel from env
var gatewayCertPwd = Environment.GetEnvironmentVariable("GATEWAY_CERT_PASSWORD")?.Trim();
if (!string.IsNullOrWhiteSpace(gatewayCertPwd))
{
    builder.Configuration["Kestrel:Endpoints:Https:Certificate:Password"] = gatewayCertPwd;
}

// OpenTelemetry: tracing, metrics, and logs
var serviceName = "tansu.gateway";
var serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var environmentName = builder.Environment.EnvironmentName;

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(rb =>
        rb.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName
            )
            .AddAttributes(
                new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", (object)environmentName)
                }
            )
    )
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            o.Filter = ctx => true;
        });
        tracing.AddHttpClientInstrumentation();
        tracing.AddSource("Yarp.ReverseProxy");
        tracing.AddOtlpExporter(otlp =>
        {
            var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                otlp.Endpoint = new Uri(endpoint);
            }
        });
    })
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation();
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddOtlpExporter(otlp =>
        {
            var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                otlp.Endpoint = new Uri(endpoint);
            }
        });
    });

// Wire OpenTelemetry logging exporter (structured logs as OTLP) in addition to console/aspnet defaults
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(otlp =>
    {
        var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            otlp.Endpoint = new Uri(endpoint);
        }
    });
});

// Health checks
builder.Services.AddHealthChecks();

// CORS: tighten cross-origin access at the gateway; configure allowed origins via Gateway:Cors:AllowedOrigins
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "Default",
        policy =>
        {
            // Accept comma or semicolon separated list
            var list = builder.Configuration["Gateway:Cors:AllowedOrigins"] ?? string.Empty;
            var origins = list.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
            else
            {
                // No origins configured -> deny all cross-site requests by default
                policy.DisallowCredentials();
            }
        }
    );
});

// HybridCache: configure Redis distributed backing if provided
var redisConn = builder.Configuration["Cache:Redis"];
var disableCache = builder.Configuration.GetValue("Cache:Disable", false);
if (!string.IsNullOrWhiteSpace(redisConn) && !disableCache)
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConn);
    builder.Services.AddHybridCache();
    // Health check for Redis
    builder
        .Services.AddHealthChecks()
        .AddCheck("redis", new TansuCloud.Gateway.Services.RedisPingHealthCheck(redisConn));
}

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
            "identity" => 600, // Auth endpoints (discovery/token can burst during tests)
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
                QueueLimit = first == "identity" ? 0 : permitLimit // avoid queueing for identity to reduce timeouts
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

// Resolve downstream service base URLs from configuration/environment for Aspire wiring
var dashboardBase = builder.Configuration["Services:DashboardBaseUrl"] ?? "http://localhost:5136";
var identityBase = builder.Configuration["Services:IdentityBaseUrl"] ?? "http://localhost:5095";
var dbBase = builder.Configuration["Services:DatabaseBaseUrl"] ?? "http://localhost:5278";
var storageBase = builder.Configuration["Services:StorageBaseUrl"] ?? "http://localhost:5257";

// Add YARP Reverse Proxy from in-memory config so we don't depend on JSON comment support
var reverseProxyBuilder = builder.Services.AddReverseProxy();
reverseProxyBuilder.LoadFromMemory(
    // Routes
    new[]
    {
        // Minimal debug route: forward under /dashdbg/* to dashboard without extra transforms
        new RouteConfig
        {
            RouteId = "dashboard-direct-debug",
            ClusterId = "dashboard",
            Order = -100,
            Match = new RouteMatch { Path = "/dashdbg/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashdbg" }
            }
        },
        // Alias: ensure /Identity/Account/Login at gateway root resolves to Identity UI
        new RouteConfig
        {
            RouteId = "identity-login-alias-root",
            ClusterId = "identity",
            Order = 0,
            Match = new RouteMatch { Path = "/Identity/Account/Login" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/identity"
                },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Identity UI when accessed at root paths (some frameworks generate /Identity/* redirects)
        new RouteConfig
        {
            RouteId = "identity-ui-root",
            ClusterId = "identity",
            Order = 5,
            Match = new RouteMatch { Path = "/Identity/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Identity static assets when referenced from root (e.g., /lib, /css, /js)
        new RouteConfig
        {
            RouteId = "identity-assets-lib-root",
            ClusterId = "identity",
            Order = 5,
            Match = new RouteMatch { Path = "/lib/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "identity-assets-css-root",
            ClusterId = "identity",
            Order = 5,
            Match = new RouteMatch { Path = "/css/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "identity-assets-js-root",
            ClusterId = "identity",
            Order = 5,
            Match = new RouteMatch { Path = "/js/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Dashboard static/framework endpoints when accessed via gateway root
        new RouteConfig
        {
            RouteId = "dashboard-framework",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/_framework/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Support framework resources when addressed under /dashboard as a relative path
        new RouteConfig
        {
            RouteId = "dashboard-framework-under-dashboard",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/dashboard/_framework/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Root-level OIDC callbacks forwarded to Dashboard (callbacks kept at gateway root)
        new RouteConfig
        {
            RouteId = "dashboard-signin-oidc-root",
            ClusterId = "dashboard",
            Order = 1,
            Match = new RouteMatch { Path = "/signin-oidc" },
            Transforms = new[]
            {
                // Ensure downstream Dashboard can reconstruct prefixed return URLs during OIDC callback
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Also support callbacks under /dashboard when X-Forwarded-Prefix is used by the client
        new RouteConfig
        {
            RouteId = "dashboard-signin-oidc-under-dashboard",
            ClusterId = "dashboard",
            Order = 1,
            Match = new RouteMatch { Path = "/dashboard/signin-oidc" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                // Keep forwarded scheme/prefix consistent for downstream during callback processing
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-signout-callback-oidc-root",
            ClusterId = "dashboard",
            Order = 1,
            Match = new RouteMatch { Path = "/signout-callback-oidc" },
            Transforms = new[]
            {
                // Ensure downstream sees canonical scheme/prefix when building post-logout redirects
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-signout-callback-oidc-under-dashboard",
            ClusterId = "dashboard",
            Order = 1,
            Match = new RouteMatch { Path = "/dashboard/signout-callback-oidc" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Low-priority root OIDC endpoints to preserve compatibility; canonical path is /identity/*
        new RouteConfig
        {
            RouteId = "identity-connect-root",
            ClusterId = "identity",
            Order = 10,
            Match = new RouteMatch { Path = "/connect/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
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
            RouteId = "identity-wellknown-root",
            ClusterId = "identity",
            Order = 10,
            Match = new RouteMatch { Path = "/.well-known/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/identity"
                },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Note: Root-level "/.well-known/*" and "/connect/*" routes removed to enforce canonical
        // "/identity" base. Only the prefixed routes below are supported.
        new RouteConfig
        {
            RouteId = "dashboard-content",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/_content/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-content-under-dashboard",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/dashboard/_content/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-blazor",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/_blazor/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" },
                // Ensure the downstream Dashboard app gets the canonical base path so the circuit
                // uses /dashboard consistently and avoids router NotFound flicker on refresh
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-blazor-under-dashboard",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/dashboard/_blazor/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" },
                // Also stamp the prefix when addressed under /dashboard
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-favicon",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/favicon.ico" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Forward app.css (non-hashed)
        new RouteConfig
        {
            RouteId = "dashboard-app-css-exact",
            ClusterId = "dashboard",
            Order = -5,
            Match = new RouteMatch { Path = "/app.css" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Forward hashed app CSS (e.g., /app.<hash>.css)
        new RouteConfig
        {
            RouteId = "dashboard-app-css-hash",
            ClusterId = "dashboard",
            Order = -5,
            Match = new RouteMatch { Path = "/app.{hash}.css" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Forward component-scoped styles (e.g., /TansuCloud.Dashboard.<hash>.styles.css)
        new RouteConfig
        {
            RouteId = "dashboard-styles-css-hash",
            ClusterId = "dashboard",
            Order = -10,
            Match = new RouteMatch { Path = "/TansuCloud.Dashboard.{hash}.styles.css" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Forward non-hashed component styles generated by Blazor Server
        new RouteConfig
        {
            RouteId = "dashboard-styles-css",
            ClusterId = "dashboard",
            Order = -10,
            Match = new RouteMatch { Path = "/TansuCloud.Dashboard.styles.css" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Also support the stylesheet when addressed under /dashboard
        new RouteConfig
        {
            RouteId = "dashboard-styles-css-under-dashboard",
            ClusterId = "dashboard",
            Order = -10,
            Match = new RouteMatch { Path = "/dashboard/TansuCloud.Dashboard.styles.css" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        new RouteConfig
        {
            RouteId = "dashboard-health-under-dashboard",
            ClusterId = "dashboard",
            Order = -50,
            Match = new RouteMatch { Path = "/dashboard/health/{**catch-all}" },
            Transforms = new[]
            {
                // Strip the /dashboard prefix but do NOT set X-Forwarded-Prefix to avoid interfering with health endpoints
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
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
                // Surface HTTPS scheme to downstream so OIDC redirect URIs use https when accessed via gateway
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
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
            RouteId = "dashboard-root",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/dashboard" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                // After removing the prefix, the path would be empty for the exact match.
                // Force it to "/" to avoid downstream issuing a 301 redirect to the root,
                // which would drop the "/dashboard" prefix at the client side.
                new Dictionary<string, string> { ["PathSet"] = "/" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                // Ensure downstream sees HTTPS and the /dashboard base path for correct OIDC redirect URIs
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/dashboard"
                },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // NOTE: The /admin/* alias is handled via an explicit redirect endpoint (below),
        // not via proxy transforms. Keeping it out of YARP ensures the browser address bar
        // reflects the canonical /dashboard/* path and avoids prefix-related flakiness.
        new RouteConfig
        {
            RouteId = "identity-route",
            ClusterId = "identity",
            Order = 1,
            Match = new RouteMatch { Path = "/identity/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/identity" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                // Surface HTTPS scheme to downstream so frameworks relying on IsHttps don't reject proxied requests
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Prefix",
                    ["Set"] = "/identity"
                },
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
            }
        },
        // Map OpenID Connect endpoints at root to Identity
        // Explicitly surface Identity OIDC endpoints under "/identity" to avoid root-path ambiguities
        new RouteConfig
        {
            RouteId = "identity-connect-prefixed",
            ClusterId = "identity",
            Order = 0,
            Match = new RouteMatch { Path = "/identity/connect/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/identity" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
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
            RouteId = "identity-wellknown-prefixed",
            ClusterId = "identity",
            Order = 0,
            Match = new RouteMatch { Path = "/identity/.well-known/{**catch-all}" },
            Transforms = new[]
            {
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/identity" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
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
                new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" },
                // Ensure downstream Vary: Accept-Encoding is present even when not compressed,
                // so caches/keying remain correct (aligns with Storage service semantics).
                new Dictionary<string, string>
                {
                    ["ResponseHeader"] = "Vary",
                    ["Set"] = "Accept-Encoding"
                }
            }
        }
    },
    // Clusters -> use dev ports from respective projects
    new[]
    {
        new ClusterConfig
        {
            ClusterId = "dashboard",
            // Enable sticky sessions for Blazor Server (SignalR) stability via the gateway
            SessionAffinity = new()
            {
                Enabled = true,
                Policy = "Cookie", // use affinity cookie
                FailurePolicy = "Redistribute",
                AffinityKeyName = ".YARP.AFFINITY"
            },
            // Allow very long-lived upgraded connections and avoid activity timeouts
            HttpRequest = new()
            {
                // Use a large finite timeout for safety; some environments can misinterpret 0
                // (intended as infinite) as immediate timeout. 10 minutes covers WS handshakes
                // and normal requests while avoiding spurious gateway 504s.
                ActivityTimeout = TimeSpan.FromMinutes(10),
                // Force HTTP/1.1 for plaintext backend to avoid h2c negotiation issues
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            },
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["d1"] = new() { Address = dashboardBase }
            }
        },
        new ClusterConfig
        {
            ClusterId = "identity",
            // Align request handling with dashboard cluster to avoid HTTP version negotiation edge cases
            HttpRequest = new()
            {
                ActivityTimeout = TimeSpan.FromMinutes(5),
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            },
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["i1"] = new() { Address = identityBase }
            }
        },
        new ClusterConfig
        {
            ClusterId = "db",
            HttpRequest = new()
            {
                ActivityTimeout = TimeSpan.FromMinutes(5),
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            },
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["db1"] = new() { Address = dbBase }
            }
        },
        new ClusterConfig
        {
            ClusterId = "storage",
            HttpRequest = new()
            {
                ActivityTimeout = TimeSpan.FromMinutes(5),
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            },
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["s1"] = new() { Address = storageBase }
            }
        }
    }
);

// Harden HttpClient used by YARP per cluster (disable proxies, force HTTP/1.1 semantics)
reverseProxyBuilder.ConfigureHttpClient(
    (context, handler) =>
    {
        if (
            string.Equals(context.ClusterId, "dashboard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.ClusterId, "identity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.ClusterId, "db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.ClusterId, "storage", StringComparison.OrdinalIgnoreCase)
        )
        {
            handler.UseProxy = false;
            handler.AllowAutoRedirect = false;
            handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
            handler.ConnectTimeout = TimeSpan.FromSeconds(5);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            handler.EnableMultipleHttp2Connections = false;
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
            };
        }
    }
);

var app = builder.Build();

// Support WebSockets through the gateway and send regular pings
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// Global Rate Limiter
app.UseRateLimiter();

// CORS before proxy to ensure preflight and headers are handled at the edge
app.UseCors("Default");

// Redirect HTTP -> HTTPS in dev when not disabled (Aspire fronting may handle TLS)
var disableHttpsRedirect = builder.Configuration.GetValue<bool>(
    "Gateway:DisableHttpsRedirect",
    false
);
if (app.Environment.IsDevelopment() && !disableHttpsRedirect)
{
    app.UseHttpsRedirection();
}

// Output cache middleware (before proxy)
// Note: Output caching can interfere with streaming/proxy scenarios; disable in Development
var disableOutputCache = builder.Configuration.GetValue(
    "Gateway:DisableOutputCache",
    app.Environment.IsDevelopment()
);
if (!disableOutputCache)
{
    app.UseOutputCache();
}

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

// Enforce simple per-route auth guard at gateway with a safe exception for presigned storage links
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path;
        var requiresAuth = false;
        if (path.StartsWithSegments("/db") || path.StartsWithSegments("/storage"))
        {
            // Allow anonymous health endpoints for downstream services
            if (
                !(
                    path.StartsWithSegments("/db/health")
                    || path.StartsWithSegments("/storage/health")
                )
            )
            {
                requiresAuth = true;
            }
        }

        // Allow health endpoints unauthenticated for monitoring
        var isHealth =
            path.StartsWithSegments("/db/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/storage/health", StringComparison.OrdinalIgnoreCase);

        // In Development, allow provisioning calls to pass without Authorization if a valid dev bypass key is present.
        var isProvisioningBypass = false;
        if (
            app.Environment.IsDevelopment()
            && path.StartsWithSegments(
                "/db/api/provisioning/tenants",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var key = app.Configuration["Dev:ProvisionBypassKey"];
            if (!string.IsNullOrWhiteSpace(key))
            {
                var hdr = context.Request.Headers["X-Provision-Key"].ToString();
                if (string.Equals(hdr, key, StringComparison.Ordinal))
                {
                    isProvisioningBypass = true;
                }
            }
        }

        // Development-only: permit anonymous access to presigned storage object URLs
        // These URLs carry HMAC query params validated by the Storage service itself (sig/exp[/max/ct]).
        var isPresignedStorage = false;
        if (requiresAuth && path.StartsWithSegments("/storage", StringComparison.OrdinalIgnoreCase))
        {
            var q = context.Request.Query;
            // Only allow for object routes, not arbitrary storage APIs
            if (
                (
                    path.StartsWithSegments(
                        "/storage/api/objects",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || path.StartsWithSegments(
                        "/storage/api/transform",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && q.ContainsKey("sig")
                && q.ContainsKey("exp")
            )
            {
                isPresignedStorage = true;
            }
        }

        if (requiresAuth && !isHealth && !isProvisioningBypass && !isPresignedStorage)
        {
            // Reject if no Authorization header present
            if (!context.Request.Headers.ContainsKey("Authorization"))
            {
                // Diagnostic: log why we are rejecting, include path/query and presign detection
                try
                {
                    var qdump = string.Join(
                        '&',
                        context.Request.Query.Select(kv => $"{kv.Key}={kv.Value}").ToArray()
                    );
                    app.Logger.LogWarning(
                        "AuthGuard: 401 unauthenticated request to {Path} (presigned={IsPresigned}, provisioningBypass={Bypass}). Query={Query}",
                        context.Request.Path,
                        isPresignedStorage,
                        isProvisioningBypass,
                        qdump
                    );
                }
                catch { }
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

// Health endpoints
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// Diagnostic middleware: log Identity token proxy 5xx to help deflake tests in Dev
if (app.Environment.IsDevelopment())
{
    var diagLogger = app
        .Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Gateway.IdentityProxy");
    app.Use(
        async (context, next) =>
        {
            var path = context.Request.Path;
            var isTokenPath =
                path.StartsWithSegments(
                    "/identity/connect/token",
                    StringComparison.OrdinalIgnoreCase
                ) || path.StartsWithSegments("/connect/token", StringComparison.OrdinalIgnoreCase);
            if (!isTokenPath)
            {
                await next();
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await next();
            }
            finally
            {
                sw.Stop();
                var status = context.Response.StatusCode;
                if (
                    status >= 500
                    || status == StatusCodes.Status502BadGateway
                    || status == StatusCodes.Status504GatewayTimeout
                )
                {
                    var host = context.Request.Headers.Host.ToString();
                    diagLogger.LogError(
                        "Identity token proxy returned {Status} for {Path} in {ElapsedMs} ms (Host={Host})",
                        status,
                        path.Value,
                        sw.ElapsedMilliseconds,
                        host
                    );
                }
            }
        }
    ); // End of Middleware Identity Token Diagnostics
}

// Development-only: debug passthrough to verify direct HttpClient connectivity to dashboard without YARP
if (app.Environment.IsDevelopment())
{
    app.MapGet(
            "/debug/dashboard-health",
            async () =>
            {
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.None
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                var resp = await client.GetAsync(
                    "http://dashboard:8080/health/ready",
                    HttpCompletionOption.ResponseHeadersRead
                );
                var text = await resp.Content.ReadAsStringAsync();
                return Results.Content(text, "text/plain");
            }
        )
        .AllowAnonymous();
}

// Map the reverse proxy endpoints (no /admin redirect; preserve original requested paths)
// Defensive canonicalization: if a request accidentally targets root-level /admin/*,
// issue a temporary redirect to the canonical /dashboard/admin/* path.
// This preserves the repository rule of no root-level admin alias while avoiding a 404 UX.
app.Use(
    async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/admin", out var rest))
        {
            // Capture diagnostics to find the source of incorrect root-level /admin links
            try
            {
                var referer = context.Request.Headers["Referer"].ToString();
                var origin = context.Request.Headers["Origin"].ToString();
                var ua = context.Request.Headers["User-Agent"].ToString();
                var traceparent = context.Request.Headers["traceparent"].ToString();
                var tracestate = context.Request.Headers["tracestate"].ToString();
                var tenant = context.Request.Headers["X-Tansu-Tenant"].ToString();
                var host = context.Request.Headers["Host"].ToString();
                var xff = context.Request.Headers["X-Forwarded-For"].ToString();
                var remote = context.Connection.RemoteIpAddress?.ToString();
                var queryStr = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
                app.Logger.LogWarning(
                    "AdminRootRedirect: Redirecting root '/admin' request to canonical '/dashboard' path. Method={Method} Path={Path} Query={Query} Host={Host} Referer={Referer} Origin={Origin} UA={UA} Traceparent={Traceparent} Tracestate={Tracestate} Tenant={Tenant} XFF={XFF} Remote={Remote}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    queryStr,
                    host,
                    referer,
                    origin,
                    ua,
                    traceparent,
                    tracestate,
                    tenant,
                    xff,
                    remote
                );
            }
            catch { }

            var target = "/dashboard" + context.Request.Path.Value;
            var qs = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
            context.Response.Redirect(target + qs, permanent: false);
            return;
        }
        await next();
    }
);
app.MapReverseProxy();

app.Run();
