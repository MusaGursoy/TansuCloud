// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Gateway.Services;
using TansuCloud.Observability;
using TansuCloud.Observability.Auditing;
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

// Minimal custom metrics to observe gateway proxy traffic
var proxyMeter = new Meter("TansuCloud.Gateway.Proxy", serviceVersion);
var proxyRequests = proxyMeter.CreateCounter<long>(
    name: "tansu_gateway_proxy_requests_total",
    unit: "requests",
    description: "Total proxied requests observed at the gateway"
);
var proxyDurationMs = proxyMeter.CreateHistogram<double>(
    name: "tansu_gateway_proxy_request_duration_ms",
    unit: "ms",
    description: "Gateway proxied request duration in milliseconds"
);

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
        // Export custom gateway proxy meter so Prometheus sees our series
        metrics.AddMeter("TansuCloud.Gateway.Proxy");
        metrics.AddMeter("TansuCloud.Audit");
        metrics.AddOtlpExporter(otlp =>
        {
            var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                otlp.Endpoint = new Uri(endpoint);
            }
        });
    });

// Shared observability core (Task 38): dynamic log level overrides, etc.
builder.Services.AddTansuObservabilityCore();
builder.Services.AddTansuAudit(builder.Configuration);
// Required by HttpAuditLogger to enrich events from the current request
builder.Services.AddHttpContextAccessor();

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
builder
    .Services.AddHealthChecks()
    // Self liveness check (no external dependencies)
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self" });

// Phase 0: publish health transitions (Info) for ops visibility
builder.Services.AddSingleton<IHealthCheckPublisher, HealthTransitionPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(2);
    o.Period = TimeSpan.FromSeconds(15);
});

// Observability shared primitives (Task 38): dynamic levels, enrichment middleware
builder.Services.AddTansuObservabilityCore();

// OIDC JWT bearer authentication for admin API (replace temporary header guard)
builder
    .Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(
        JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            // Derive Authority/Metadata per repo guidance
            var issuer = builder.Configuration["Oidc:Issuer"] ?? "http://127.0.0.1:8080/identity/";
            var issuerTrim = issuer.TrimEnd('/');
            options.Authority = issuerTrim;

            var metadataAddress = builder.Configuration["Oidc:MetadataAddress"];
            if (!string.IsNullOrWhiteSpace(metadataAddress))
            {
                options.MetadataAddress = metadataAddress;
            }
            else if (
                string.Equals(
                    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                    "true",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                options.MetadataAddress =
                    "http://gateway:8080/identity/.well-known/openid-configuration";
            }
            else
            {
                options.MetadataAddress = issuerTrim + "/.well-known/openid-configuration";
            }

            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.MapInboundClaims = false; // keep raw JWT claim types
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                // Accept common dev variations (with/without trailing slash; localhost vs 127.0.0.1)
                ValidIssuers = new[]
                {
                    issuerTrim,
                    issuerTrim + "/",
                    issuerTrim.Replace(
                        "127.0.0.1",
                        "localhost",
                        StringComparison.OrdinalIgnoreCase
                    ),
                    issuerTrim.Replace("127.0.0.1", "localhost", StringComparison.OrdinalIgnoreCase)
                        + "/"
                },
                // In Development, it's acceptable to relax audience validation at the gateway admin layer
                ValidateAudience = !builder.Environment.IsDevelopment(),
                ValidTypes = new[] { "at+jwt", "JWT", "jwt" }
            };
        }
    );

// Admin authorization policy: Admin role or admin.full scope
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "AdminOnly",
        policy =>
        {
            policy.RequireAssertion(ctx =>
            {
                try
                {
                    var user = ctx.User;
                    if (user == null)
                        return false;
                    if (user.IsInRole("Admin"))
                        return true;
                    // scope/scp may be space-delimited or repeated
                    var scopeValues = user.FindAll("scope")
                        .Select(c => c.Value)
                        .Concat(user.FindAll("scp").Select(c => c.Value));
                    foreach (var v in scopeValues)
                    {
                        var parts = v.Split(
                            ' ',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                        );
                        if (parts.Contains("admin.full", StringComparer.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            });
        }
    );
});

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
        .AddCheck(
            "redis",
            new TansuCloud.Gateway.Services.RedisPingHealthCheck(redisConn),
            tags: new[] { "ready", "redis" }
        );
}

// Gateway knobs: rate limit window for Retry-After, and static assets cache TTL
var rateLimitWindowSeconds = builder.Configuration.GetValue("Gateway:RateLimits:WindowSeconds", 10);
var staticAssetsTtlSeconds = builder.Configuration.GetValue(
    "Gateway:OutputCache:StaticTtlSeconds",
    300
);

// Runtime rate limit configuration (Iteration 1 Task 17)
var initialDefaults = new RateLimitDefaults
{
    PermitLimit = builder.Configuration.GetValue("Gateway:RateLimits:Defaults:PermitLimit", 100),
    QueueLimit = builder.Configuration.GetValue(
        "Gateway:RateLimits:Defaults:QueueLimit",
        builder.Configuration.GetValue("Gateway:RateLimits:Defaults:PermitLimit", 100)
    )
};
var initialRoutes = new Dictionary<string, RateLimitRouteOverride>(StringComparer.OrdinalIgnoreCase)
{
    {
        "db",
        new RateLimitRouteOverride
        {
            PermitLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:db:PermitLimit"
            ),
            QueueLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:db:QueueLimit"
            )
        }
    },
    {
        "storage",
        new RateLimitRouteOverride
        {
            PermitLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:storage:PermitLimit"
            ),
            QueueLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:storage:QueueLimit"
            )
        }
    },
    {
        "identity",
        new RateLimitRouteOverride
        {
            PermitLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:identity:PermitLimit"
            ),
            QueueLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:identity:QueueLimit"
            )
        }
    },
    {
        "dashboard",
        new RateLimitRouteOverride
        {
            PermitLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:dashboard:PermitLimit"
            ),
            QueueLimit = builder.Configuration.GetValue<int?>(
                "Gateway:RateLimits:Routes:dashboard:QueueLimit"
            )
        }
    }
};
var rlRuntime = new RateLimitRuntime(rateLimitWindowSeconds, initialDefaults, initialRoutes);
builder.Services.AddSingleton<IRateLimitRuntime>(rlRuntime);
builder.Services.AddSingleton<RateLimitRejectionAggregator>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<RateLimitRejectionAggregator>>();
    var overrides = sp.GetRequiredService<IDynamicLogLevelOverride>();
    return new RateLimitRejectionAggregator(logger, overrides, rateLimitWindowSeconds);
});

// Safety controls: Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, token) =>
    {
        try
        {
            // Align with FixedWindow window length to provide a clear client backoff hint
            var retryAfter = rlRuntime.WindowSeconds.ToString();
            context.HttpContext.Response.Headers["Retry-After"] = retryAfter;
            context.HttpContext.Response.Headers["X-Retry-After"] = retryAfter;
            // Aggregate this rejection for summary logging
            var path = context.HttpContext.Request.Path;
            var prefix = path.HasValue ? path.Value!.TrimStart('/') : string.Empty;
            var first = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.Split('/', 2)[0];
            var tenant = context.HttpContext.Request.Headers["X-Tansu-Tenant"].ToString();
            var hasAuth = context.HttpContext.Request.Headers.ContainsKey("Authorization");
            string idPart;
            if (!hasAuth)
            {
                var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                idPart = $"ip:{ip}";
            }
            else
            {
                var authHeader = context.HttpContext.Request.Headers["Authorization"].ToString();
                idPart = $"auth:{HashKey(authHeader)}";
            }
            var key = $"{first}|{tenant}|{idPart}|v{rlRuntime.Version}";
            var agg =
                context.HttpContext.RequestServices.GetRequiredService<RateLimitRejectionAggregator>();
            agg.Report(first, tenant, key);
        }
        catch { }
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Partition key: route prefix + tenant id for fairness
        var path = context.Request.Path;
        var prefix = path.HasValue ? path.Value!.TrimStart('/') : string.Empty;
        var first = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.Split('/', 2)[0];
        var tenant = context.Request.Headers["X-Tansu-Tenant"].ToString();
        // Public (no Authorization): partition by client IP to reduce noisy neighbor impact
        // Authenticated: partition by tenant + hashed token (no PII/token leak)
        var hasAuth = context.Request.Headers.ContainsKey("Authorization");
        string idPart;
        if (!hasAuth)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            idPart = $"ip:{ip}";
        }
        else
        {
            var token = context.Request.Headers["Authorization"].ToString();
            // Hash token to avoid storing sensitive values and reduce cardinality
            idPart = $"auth:{HashKey(token)}";
        }
        // Include a runtime version component so that when the admin updates limits, we create
        // new limiter partitions and don't keep using cached FixedWindow instances with stale values.
        var key = $"{first}|{tenant}|{idPart}|v{rlRuntime.Version}";

        // Different limits per route family (configurable with sane defaults)
        var (permitLimit, queueLimit, windowSeconds) = rlRuntime.Resolve(first);

        var limiter = RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = queueLimit
            }
        );
        // Emit a structured debug for partition resolution
        var plogger = context
            .RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RateLimiter");
        plogger.LogRateLimitPartition(first, tenant, key, permitLimit, queueLimit, windowSeconds);
        return limiter;
    });
});

// Output caching: tenant-aware variation and safe defaults
builder.Services.AddOutputCache(options =>
{
    var defaultTtlSeconds = builder.Configuration.GetValue(
        "Gateway:OutputCache:DefaultTtlSeconds",
        15
    );
    options.AddBasePolicy(policy =>
        policy
            .Cache() // default: GET/HEAD, 200s
            .Expire(TimeSpan.FromSeconds(Math.Max(0, defaultTtlSeconds)))
            .SetVaryByHeader("X-Tansu-Tenant", "Accept", "Accept-Encoding")
            .SetVaryByHost(true)
    );
    // Named policy for public static assets (longer TTL, minimal vary-by)
    options.AddPolicy(
        "PublicStaticLong",
        policy =>
            policy
                .Cache()
                .Expire(TimeSpan.FromSeconds(Math.Max(0, staticAssetsTtlSeconds)))
                .SetVaryByHeader("Accept-Encoding")
                .SetVaryByHost(true)
    );
});

// Resolve downstream service base URLs from configuration/environment for Aspire wiring
// Use 127.0.0.1 instead of localhost to avoid IPv6/loopback divergence in dev
var dashboardBase = builder.Configuration["Services:DashboardBaseUrl"] ?? "http://127.0.0.1:5136";
var identityBase = builder.Configuration["Services:IdentityBaseUrl"] ?? "http://127.0.0.1:5095";
var dbBase = builder.Configuration["Services:DatabaseBaseUrl"] ?? "http://127.0.0.1:5278";
var storageBase = builder.Configuration["Services:StorageBaseUrl"] ?? "http://127.0.0.1:5257";

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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
        // Note: Root-level "/.well-known/*" and "/connect/*" routes are kept as low-priority
        // aliases for compatibility, but the canonical public base is "/identity/*".
        // Prefer the prefixed routes and reserve the root paths for legacy clients only.
        new RouteConfig
        {
            RouteId = "dashboard-content",
            ClusterId = "dashboard",
            Match = new RouteMatch { Path = "/_content/{**catch-all}" },
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
            OutputCachePolicy = "PublicStaticLong",
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
        // Forward Dashboard admin/API calls under /dashboard/api/* to the Dashboard app preserving the /api/* path
        // so Minimal API endpoints like /api/metrics/* are reachable via the gateway canonical prefix.
        new RouteConfig
        {
            RouteId = "dashboard-api-under-dashboard",
            ClusterId = "dashboard",
            Order = -100,
            Match = new RouteMatch { Path = "/dashboard/api/{**catch-all}" },
            Transforms = new[]
            {
                // Remove the /dashboard prefix but preserve the remaining /api/* path
                new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashboard" },
                // Preserve original Host header for downstream
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
                // Surface HTTPS scheme to downstream so OIDC and cookie policies remain consistent
                new Dictionary<string, string>
                {
                    ["RequestHeader"] = "X-Forwarded-Proto",
                    ["Set"] = "https"
                },
                // Inform downstream it is hosted under /dashboard (useful for building absolute links)
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
            RouteId = "dashboard-route",
            ClusterId = "dashboard",
            Order = 100,
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

// Enrichment middleware (Task 38) - correlation, tenant, route base in logging scope
app.UseMiddleware<RequestEnrichmentMiddleware>();

// Safety net: ensure any 429 response carries a Retry-After header
// Place BEFORE the rate limiter so it applies even when requests are rejected early.
app.Use(
    (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode == StatusCodes.Status429TooManyRequests)
            {
                if (!context.Response.Headers.ContainsKey("Retry-After"))
                {
                    context.Response.Headers["Retry-After"] = rlRuntime.WindowSeconds.ToString(); // fixed-window length hint
                }
                if (!context.Response.Headers.ContainsKey("X-Retry-After"))
                {
                    context.Response.Headers["X-Retry-After"] = rlRuntime.WindowSeconds.ToString();
                }
            }
            return Task.CompletedTask;
        });
        return next();
    }
); // End of Middleware Retry-After Safety

// Global Rate Limiter
app.UseRateLimiter();

// CORS before proxy to ensure preflight and headers are handled at the edge
app.UseCors("Default");

// Startup diagnostic: log OIDC metadata source choice (Task 38)
try
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OIDC-Config");
    var issuer = builder.Configuration["Oidc:Issuer"] ?? "http://127.0.0.1:8080/identity/";
    var issuerTrim = issuer.TrimEnd('/');
    var configuredMd = builder.Configuration["Oidc:MetadataAddress"];
    string effectiveMd;
    string source;
    if (!string.IsNullOrWhiteSpace(configuredMd))
    {
        effectiveMd = configuredMd!;
        source = "explicit-config";
    }
    else if (
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase
        )
    )
    {
        effectiveMd = "http://gateway:8080/identity/.well-known/openid-configuration";
        source = "container-gateway";
    }
    else
    {
        effectiveMd = issuerTrim + "/.well-known/openid-configuration";
        source = "issuer-derived";
    }
    log.LogOidcMetadataChoice(source, issuerTrim, effectiveMd);
}
catch
{
    // best-effort only
}

// Attach correlation/tenant enrichment as early as possible (Task 38)
app.UseMiddleware<RequestEnrichmentMiddleware>();

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
    // Do not cache authenticated requests at the gateway to avoid leakage and keep semantics simple
    app.UseWhen(
        ctx => !ctx.Request.Headers.ContainsKey("Authorization"),
        branch => branch.UseOutputCache()
    );
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

        // Dev-only: allow anonymous access to Storage diagnostic throw endpoint to validate SigNoz E2E
        var isStorageDevThrow =
            app.Environment.IsDevelopment()
            && path.StartsWithSegments("/storage/dev/throw", StringComparison.OrdinalIgnoreCase);

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

        if (
            requiresAuth
            && !isHealth
            && !isProvisioningBypass
            && !isPresignedStorage
            && !isStorageDevThrow
        )
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
app.MapHealthChecks(
        "/health/live",
        new HealthCheckOptions { Predicate = r => r.Tags.Contains("self") }
    )
    .AllowAnonymous();
app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") }
    )
    .AllowAnonymous();

// Enable authentication/authorization (required for admin API JWT validation)
app.UseAuthentication();
app.UseAuthorization();

// Admin API endpoints
// In Development, keep them anonymously accessible for ease of testing.
// In non-Development, require AdminOnly policy via JWT bearer.
var adminGroup = app.MapGroup("/admin/api");
if (app.Environment.IsDevelopment())
{
    adminGroup.AllowAnonymous();
}
else
{
    adminGroup.RequireAuthorization("AdminOnly");
}

app.MapGet("/ratelimit/ping", () => Results.Text("OK", "text/plain")).AllowAnonymous();

adminGroup
    .MapGet("/rate-limits", () => Results.Json(rlRuntime.GetSnapshot()))
    .WithDisplayName("Admin: Get rate limits");

adminGroup
    .MapPost(
        "/rate-limits",
        (
            HttpContext http,
            RateLimitConfigDto body,
            ILoggerFactory loggerFactory,
            IConfiguration cfg,
            IAuditLogger audit
        ) =>
        {
            // Basic validation with ProblemDetails-style response
            if (body is null)
            {
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "RateLimitUpdate",
                        Outcome = "Failure",
                        ReasonCode = "InvalidBody",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(ev, new { Path = http.Request?.Path.Value }, new[] { "Path" });
                }
                catch { }
                return Results.Problem(
                    title: "Invalid body",
                    detail: "Request body is required.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            var errors = new List<string>();
            if (body.WindowSeconds < 1)
            {
                errors.Add("WindowSeconds must be >= 1.");
            }
            if (body.Defaults is not null)
            {
                if (body.Defaults.PermitLimit < 0)
                    errors.Add("Defaults.PermitLimit must be >= 0.");
                if (body.Defaults.QueueLimit < 0)
                    errors.Add("Defaults.QueueLimit must be >= 0.");
            }
            if (body.Routes is not null)
            {
                foreach (var kv in body.Routes)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        errors.Add("Route keys must be non-empty.");
                    var r = kv.Value;
                    if (r is null)
                        continue;
                    if (r.PermitLimit is not null && r.PermitLimit < 0)
                        errors.Add($"Routes['{kv.Key}'].PermitLimit must be >= 0.");
                    if (r.QueueLimit is not null && r.QueueLimit < 0)
                        errors.Add($"Routes['{kv.Key}'].QueueLimit must be >= 0.");
                }
            }
            if (errors.Count > 0)
            {
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "RateLimitUpdate",
                        Outcome = "Failure",
                        ReasonCode = "ValidationError",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(ev, new { Errors = errors }, new[] { "Errors" });
                }
                catch { }
                return Results.ValidationProblem(
                    errors.GroupBy(e => "rateLimits").ToDictionary(g => g.Key, g => g.ToArray()),
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation failed"
                );
            }

            // Optional CSRF check (dev-friendly): if Gateway:Admin:Csrf is configured, require matching header X-Tansu-Csrf
            var expectedCsrf = cfg["Gateway:Admin:Csrf"];
            if (!string.IsNullOrWhiteSpace(expectedCsrf))
            {
                if (
                    !http.Request.Headers.TryGetValue("X-Tansu-Csrf", out var csrf)
                    || csrf != expectedCsrf
                )
                {
                    try
                    {
                        var ev = new TansuCloud.Observability.Auditing.AuditEvent
                        {
                            Category = "Admin",
                            Action = "RateLimitUpdate",
                            Outcome = "Failure",
                            ReasonCode = "Unauthorized",
                            Subject = http.User?.Identity?.Name ?? "anonymous",
                            CorrelationId = http.TraceIdentifier
                        };
                        audit.TryEnqueueRedacted(ev, new { Path = http.Request?.Path.Value }, new[] { "Path" });
                    }
                    catch { }
                    return Results.Problem(
                        title: "Unauthorized",
                        detail: "Missing or invalid X-Tansu-Csrf.",
                        statusCode: StatusCodes.Status401Unauthorized
                    );
                }
            }

            // Audit: capture a minimal diff and log an informational event
            var adminLogger = loggerFactory.CreateLogger("Gateway.Admin");
            var before = rlRuntime.GetSnapshot();

            rlRuntime.Apply(body);
            var after = rlRuntime.GetSnapshot();

            try
            {
                var remote = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var changedRoutes = new List<string>();
                var beforeRoutes =
                    before.Routes?.Keys?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var afterRoutes =
                    after.Routes?.Keys?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in beforeRoutes.Union(afterRoutes))
                {
                    TansuCloud.Gateway.Services.RateLimitRouteOverride? b = null;
                    TansuCloud.Gateway.Services.RateLimitRouteOverride? a = null;
                    var bHas = before.Routes != null && before.Routes.TryGetValue(k, out b);
                    var aHas = after.Routes != null && after.Routes.TryGetValue(k, out a);
                    if (!bHas && aHas)
                    {
                        changedRoutes.Add($"+{k}");
                        continue;
                    }
                    if (bHas && !aHas)
                    {
                        changedRoutes.Add($"-{k}");
                        continue;
                    }
                    if (
                        bHas
                        && aHas
                        && (
                            (b?.PermitLimit) != (a?.PermitLimit)
                            || (b?.QueueLimit) != (a?.QueueLimit)
                        )
                    )
                        changedRoutes.Add($"~{k}");
                }

                adminLogger.LogInformation(
                    "RateLimits changed by {Remote} WindowSeconds {BeforeWindow}->{AfterWindow}; Defaults P:{BeforeP} Q:{BeforeQ} -> P:{AfterP} Q:{AfterQ}; Routes changed: {ChangedRoutes}",
                    remote,
                    before.WindowSeconds,
                    after.WindowSeconds,
                    before.Defaults?.PermitLimit,
                    before.Defaults?.QueueLimit,
                    after.Defaults?.PermitLimit,
                    after.Defaults?.QueueLimit,
                    string.Join(",", changedRoutes)
                );

                // Audit success event (allowlisted details)
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "RateLimitUpdate",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            BeforeWindow = before.WindowSeconds,
                            AfterWindow = after.WindowSeconds,
                            ChangedRoutes = changedRoutes
                        },
                        new[] { "BeforeWindow", "AfterWindow", "ChangedRoutes" }
                    );
                }
                catch { }
            }
            catch { }

            return Results.Json(after);
        }
    )
    .WithDisplayName("Admin: Set rate limits");

// Phase 0: dynamic log level override shim (Development only)
if (app.Environment.IsDevelopment())
{
    adminGroup
        .MapPost(
            "/logging/overrides",
            (HttpContext http, IDynamicLogLevelOverride ovr, [AsParameters] LogOverrideRequest req, IAuditLogger audit) =>
            {
                if (string.IsNullOrWhiteSpace(req.Category))
                {
                    try
                    {
                        var ev = new TansuCloud.Observability.Auditing.AuditEvent
                        {
                            Category = "Admin",
                            Action = "LogLevelOverride",
                            Outcome = "Failure",
                            ReasonCode = "ValidationError",
                            Subject = http.User?.Identity?.Name ?? "anonymous",
                            CorrelationId = http.TraceIdentifier
                        };
                        audit.TryEnqueueRedacted(ev, new { Error = "CategoryRequired" }, new[] { "Error" });
                    }
                    catch { }
                    return Results.Problem("Category is required", statusCode: 400);
                }
                if (req.TtlSeconds <= 0)
                {
                    try
                    {
                        var ev = new TansuCloud.Observability.Auditing.AuditEvent
                        {
                            Category = "Admin",
                            Action = "LogLevelOverride",
                            Outcome = "Failure",
                            ReasonCode = "ValidationError",
                            Subject = http.User?.Identity?.Name ?? "anonymous",
                            CorrelationId = http.TraceIdentifier
                        };
                        audit.TryEnqueueRedacted(ev, new { Error = "TtlSecondsInvalid" }, new[] { "Error" });
                    }
                    catch { }
                    return Results.Problem("TtlSeconds must be > 0", statusCode: 400);
                }
                ovr.Set(req.Category!, req.Level, TimeSpan.FromSeconds(req.TtlSeconds));

                // Audit success
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "LogLevelOverride",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new { req.Category, Level = req.Level.ToString(), req.TtlSeconds },
                        new[] { "Category", "Level", "TtlSeconds" }
                    );
                }
                catch { }
                return Results.Ok(new { ok = true });
            }
        )
        .WithDisplayName("Admin: Set log level override (dev)");

    adminGroup
        .MapGet(
            "/logging/overrides",
            (IDynamicLogLevelOverride ovr) =>
                Results.Json(
                    ovr.Snapshot()
                        .ToDictionary(k => k.Key, v => new { v.Value.Level, v.Value.Expires })
                )
        )
        .WithDisplayName("Admin: Get log level overrides (dev)");

    // Rate limit summary snapshot for dashboard read-only card
    adminGroup
        .MapGet(
            "/rate-limits/summary",
            (RateLimitRejectionAggregator agg) =>
            {
                var snap = agg.GetLastSnapshot();
                return snap is null ? Results.NoContent() : Results.Json(snap);
            }
        )
        .WithDisplayName("Admin: Get rate limit summary (dev)");
}

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
        var reqPath = context.Request.Path;
        // Exclude admin API endpoints from redirect so programmatic calls (e.g., Dashboard/E2E) work at /admin/api/*
        // Use PathString.StartsWithSegments for robust matching.
        var isAdminApi = reqPath.StartsWithSegments("/admin/api", out _);
        if (!isAdminApi && reqPath.StartsWithSegments("/admin", out var rest))
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
                var queryStr = context.Request.QueryString.HasValue
                    ? context.Request.QueryString.Value
                    : string.Empty;
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
            var qs = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value
                : string.Empty;
            context.Response.Redirect(target + qs, permanent: false);
            return;
        }
        await next();
    }
);

// Record proxy metrics around proxied requests to capture status code and duration.
// Place this BEFORE MapReverseProxy so it wraps the proxy execution for proxied routes.
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path;
        // Only observe requests that are likely destined for proxied routes
        // based on our configured top-level prefixes to avoid double-counting local endpoints.
        var firstSegment = path.HasValue
            ? path.Value!.TrimStart('/').Split('/', 2)[0].ToLowerInvariant()
            : string.Empty;
        var shouldObserve =
            firstSegment
                is "dashboard"
                    or "identity"
                    or "db"
                    or "storage"
                    or "_framework"
                    or "_content"
                    or "_blazor"
                    or "signin-oidc"
                    or "signout-callback-oidc"
                    or "lib"
                    or "css"
                    or "js";
        if (!shouldObserve)
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
            var statusClass = (context.Response.StatusCode / 100) + "xx";
            var route = string.IsNullOrEmpty(firstSegment) ? "root" : firstSegment;
            proxyRequests.Add(
                1,
                new KeyValuePair<string, object?>("route", route),
                new KeyValuePair<string, object?>("status", statusClass)
            );
            proxyDurationMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("route", route),
                new KeyValuePair<string, object?>("status", statusClass)
            );
        }
    }
);

app.MapReverseProxy();

app.Run();

// Helper: stable short hash for rate limiting partitions (no PII/token leakage)
static string HashKey(string value)
{
    try
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        // Return first 8 bytes as hex for compactness
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8 && i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
    catch
    {
        return "hasherr";
    }
} // End of Method HashKey

public sealed record LogOverrideRequest(string Category, LogLevel Level, int TtlSeconds);
