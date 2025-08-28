// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Generic;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.OutputCaching;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Gateway.Services;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

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
    options.AddPolicy("Default", policy =>
    {
        // Accept comma or semicolon separated list
        var list = builder.Configuration["Gateway:Cors:AllowedOrigins"] ?? string.Empty;
        var origins = list
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (origins.Length > 0)
        {
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            // No origins configured -> deny all cross-site requests by default
            policy.DisallowCredentials();
        }
    });
});

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

// Resolve downstream service base URLs from configuration/environment for Aspire wiring
var dashboardBase = builder.Configuration["Services:DashboardBaseUrl"] ?? "http://localhost:5136";
var identityBase = builder.Configuration["Services:IdentityBaseUrl"] ?? "http://localhost:5095";
var dbBase = builder.Configuration["Services:DatabaseBaseUrl"] ?? "http://localhost:5278";
var storageBase = builder.Configuration["Services:StorageBaseUrl"] ?? "http://localhost:5257";

// Add YARP Reverse Proxy from in-memory config so we don't depend on JSON comment support
builder
    .Services.AddReverseProxy()
    .LoadFromMemory(
        // Routes
        new[]
        {
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
                    new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
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
                    new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
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
                // Enable sticky sessions for Blazor Server (SignalR) stability via the gateway
                SessionAffinity = new()
                {
                    Enabled = true,
                    Policy = "Cookie", // use affinity cookie
                    FailurePolicy = "Redistribute",
                    AffinityKeyName = ".YARP.AFFINITY"
                },
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["d1"] = new() { Address = dashboardBase }
                }
            },
            new ClusterConfig
            {
                ClusterId = "identity",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["i1"] = new() { Address = identityBase }
                }
            },
            new ClusterConfig
            {
                ClusterId = "db",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["db1"] = new() { Address = dbBase }
                }
            },
            new ClusterConfig
            {
                ClusterId = "storage",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["s1"] = new() { Address = storageBase }
                }
            }
        }
    );

var app = builder.Build();

// Support WebSockets through the gateway
app.UseWebSockets();

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

        if (requiresAuth && !isHealth)
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

// Health endpoints
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// Map the reverse proxy endpoints
app.MapReverseProxy();

app.Run();
