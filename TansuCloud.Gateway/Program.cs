// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TansuCloud.Gateway.Middleware;
using TansuCloud.Gateway.Observability;
using TansuCloud.Gateway.Services;
using TansuCloud.Observability;
using TansuCloud.Observability.Auditing;
using TansuCloud.Observability.Shared.Configuration;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

var appUrls = AppUrlsOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(appUrls);

// Explicitly bind Gateway listen URL early to avoid surprises from machine-wide ASPNETCORE_URLS
// Priority order: env GATEWAY_URLS > appsettings Kestrel:Endpoints:Http:Url > fallback
// Note: ASPNETCORE_URLS tends to override Kestrel endpoints; we counter by setting UseUrls explicitly.
var aspnetUrlsEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var gatewayUrlsEnv = Environment.GetEnvironmentVariable("GATEWAY_URLS");
var configuredKestrelUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"];
var fallbackListenUrl = !string.IsNullOrWhiteSpace(configuredKestrelUrl)
    ? configuredKestrelUrl!
    : appUrls.PublicBaseUrl!;
var desiredListenUrl = !string.IsNullOrWhiteSpace(gatewayUrlsEnv)
    ? gatewayUrlsEnv
    : fallbackListenUrl;

// Apply explicit binding; Kestrel will log that Urls override endpoint config, which is intended here.
builder.WebHost.UseUrls(desiredListenUrl);

if (
    !string.IsNullOrWhiteSpace(aspnetUrlsEnv)
    && !aspnetUrlsEnv.Equals(desiredListenUrl, StringComparison.OrdinalIgnoreCase)
)
{
    Console.WriteLine(
        $"[Gateway] ASPNETCORE_URLS='{aspnetUrlsEnv}' detected but overridden by '{desiredListenUrl}'. Set GATEWAY_URLS to change."
    );
}

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
        tracing.AddTansuAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        tracing.AddSource("Yarp.ReverseProxy");
        tracing.AddRedisInstrumentation();
        tracing.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
    })
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation();
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        // Export custom gateway proxy meter so SigNoz captures our series via OTLP
        metrics.AddMeter("TansuCloud.Gateway.Proxy");
        metrics.AddMeter("TansuCloud.Gateway.Policy");
        metrics.AddMeter("TansuCloud.Audit");
        // Export OTLP diagnostics/gauges
        metrics.AddMeter("tansu.otel.exporter");
        metrics.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
        // Export Prometheus metrics for direct scraping (Task 47 Phase 4)
        metrics.AddPrometheusExporter();
    });

// Shared observability core (Task 38): dynamic log level overrides, etc.
builder.Services.AddTansuObservabilityCore();
builder.Services.AddTansuAudit(builder.Configuration);
builder.Services.AddHostedService<YarpActivityEnricher>();

// Required by HttpAuditLogger to enrich events from the current request
builder.Services.AddHttpContextAccessor();

// Wire OpenTelemetry logging exporter (structured logs as OTLP) in addition to console/aspnet defaults
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.ParseStateValues = true;
    logging.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
});

// Health checks
builder
    .Services.AddHealthChecks()
    // Self liveness check (no external dependencies)
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self" });

// Readiness: verify OTLP exporter reachability and W3C Activity id format
builder
    .Services.AddHealthChecks()
    .AddCheck<OtlpConnectivityHealthCheck>("otlp", tags: new[] { "ready", "otlp" });

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
            var configuredIssuer = builder.Configuration["Oidc:Issuer"];
            var issuer = string.IsNullOrWhiteSpace(configuredIssuer)
                ? appUrls.GetIssuer("identity")
                : configuredIssuer!;
            var issuerTrim = issuer.TrimEnd('/');
            options.Authority = issuerTrim;

            var metadataAddress = builder.Configuration["Oidc:MetadataAddress"];
            if (string.IsNullOrWhiteSpace(metadataAddress))
            {
                var inContainer = string.Equals(
                    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                    "true",
                    StringComparison.OrdinalIgnoreCase
                );
                metadataAddress = appUrls.GetBackchannelMetadataAddress(inContainer, "identity");
            }

            options.MetadataAddress = metadataAddress;

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

// Persist Data Protection keys for affinity cookies and other encrypted payloads so they remain valid across restarts
var dataProtectionBuilder = builder
    .Services.AddDataProtection()
    .SetApplicationName("TansuCloud.Gateway");
if (!string.IsNullOrWhiteSpace(redisConn))
{
    try
    {
        var mux = ConnectionMultiplexer.Connect(redisConn);
        builder.Services.AddSingleton<IConnectionMultiplexer>(mux);
        dataProtectionBuilder.PersistKeysToStackExchangeRedis(mux, "DataProtection-Keys:Gateway");
        Console.WriteLine(
            "[DataProtection] Using Redis key ring at 'DataProtection-Keys:Gateway' (StackExchange.Redis)"
        );
    }
    catch
    {
        var fallback = builder.Configuration["DataProtection:KeysPath"] ?? "/keys";
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
            // ignore directory errors; rely on Data Protection to surface issues at runtime
        }

        dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(fallback));
        Console.WriteLine(
            $"[DataProtection] Redis connect failed; using filesystem key ring at '{fallback}'"
        );
    }
}
else
{
    var keysPath = builder.Configuration["DataProtection:KeysPath"] ?? "/keys";
    try
    {
        Directory.CreateDirectory(keysPath);
    }
    catch
    {
        // ignore directory errors; rely on Data Protection to surface issues at runtime
    }

    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    Console.WriteLine($"[DataProtection] Using filesystem key ring at '{keysPath}'");
}

// End of DataProtection configuration

// Gateway knobs: rate limit window for Retry-After, and static assets cache TTL
var rateLimitWindowSeconds = builder.Configuration.GetValue("Gateway:RateLimits:WindowSeconds", 10);
var defaultOutputCacheTtl = builder.Configuration.GetValue(
    "Gateway:OutputCache:DefaultTtlSeconds",
    15
);
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
var initialRateLimitRoutes = new Dictionary<string, RateLimitRouteOverride>(
    StringComparer.OrdinalIgnoreCase
)
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
var rlRuntime = new RateLimitRuntime(
    rateLimitWindowSeconds,
    initialDefaults,
    initialRateLimitRoutes
);
builder.Services.AddSingleton<IRateLimitRuntime>(rlRuntime);
builder.Services.AddSingleton<RateLimitRejectionAggregator>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<RateLimitRejectionAggregator>>();
    var overrides = sp.GetRequiredService<IDynamicLogLevelOverride>();
    return new RateLimitRejectionAggregator(logger, overrides, rateLimitWindowSeconds);
});

// Domains/TLS runtime registry (Iteration 2 Task 17 - scaffold)
var domainTlsRuntime = new DomainTlsRuntime();
builder.Services.AddSingleton<IDomainTlsRuntime>(domainTlsRuntime);

// OutputCache runtime configuration (Task 17 - OutputCache editor)
var outputCacheRuntime = new OutputCacheRuntime(defaultOutputCacheTtl, staticAssetsTtlSeconds);
builder.Services.AddSingleton<IOutputCacheRuntime>(outputCacheRuntime);

// Policy Center runtime with PostgreSQL persistence (Task 17 - Policy Center)
var gatewayDbConnectionString =
    builder.Configuration.GetConnectionString("GatewayDb")
    ?? throw new InvalidOperationException("GatewayDb connection string not configured");

builder.Services.AddDbContext<TansuCloud.Gateway.Data.PolicyDbContext>(options =>
{
    options.UseNpgsql(gatewayDbConnectionString);
    
    // In Development, suppress pending model changes warning (Task 47: manual table creation workaround)
    if (builder.Environment.IsDevelopment())
    {
        options.ConfigureWarnings(warnings =>
            warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }
});

builder.Services.AddScoped<
    TansuCloud.Gateway.Data.IPolicyStore,
    TansuCloud.Gateway.Data.PolicyStore
>();
builder.Services.AddSingleton<IPolicyRuntime, PolicyRuntime>();

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

// Output caching: tenant-aware variation and safe defaults with runtime TTL
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(new RuntimeOutputCachePolicy(outputCacheRuntime, isStatic: false));
    options.AddPolicy(
        "PublicStaticLong",
        new RuntimeOutputCachePolicy(outputCacheRuntime, isStatic: true)
    );

    // Add dynamic cache policy that reads from PolicyRuntime
    options.AddPolicy(
        "DynamicCachePolicy",
        builder => builder.AddPolicy<TansuCloud.Gateway.OutputCache.DynamicCachePolicy>()
    );
});

string ResolveServiceBaseUrl(string key, int defaultPort, IConfiguration? overrideConfig = null)
{
    var configSource = overrideConfig ?? builder.Configuration;
    var configured = configSource[key];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured!;
    }

    var baseUri = new Uri(appUrls.PublicBaseUrl!);
    var uriBuilder = new UriBuilder(baseUri)
    {
        Port = defaultPort,
        Path = string.Empty,
        Query = string.Empty,
        Fragment = string.Empty,
    };
    return uriBuilder.Uri.GetLeftPart(UriPartial.Authority);
}

// Resolve downstream service base URLs from configuration/environment for Aspire wiring
var dashboardBase = ResolveServiceBaseUrl("Services:DashboardBaseUrl", 5136);
var identityBase = ResolveServiceBaseUrl("Services:IdentityBaseUrl", 5095);
var dbBase = ResolveServiceBaseUrl("Services:DatabaseBaseUrl", 5278);
var storageBase = ResolveServiceBaseUrl("Services:StorageBaseUrl", 5257);

// Prepare initial YARP config in memory and register a dynamic provider
var reverseProxyBuilder = builder.Services.AddReverseProxy();
var initialProxyRoutes = new List<RouteConfig>
{
    // Minimal debug route: forward under /dashdbg/* to dashboard without extra transforms
    new RouteConfig
    {
        RouteId = "dashboard-direct-debug",
        ClusterId = "dashboard",
        Order = -100,
        Match = new RouteMatch { Path = "/dashdbg/{**catch-all}" },
        Transforms = new[] { new Dictionary<string, string> { ["PathRemovePrefix"] = "/dashdbg" } }
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
    },
    // Grafana route - Optional advanced visualization (Task 47 Phase 4)
    // Only accessible in Development or when GRAFANA_UI_ENABLED=true
    // Note: Grafana serves from sub-path (/grafana), so we don't remove the prefix
    new RouteConfig
    {
        RouteId = "grafana-route",
        ClusterId = "grafana",
        Order = 1,
        Match = new RouteMatch { Path = "/grafana/{**catch-all}" },
        Transforms = new[]
        {
            // Don't remove path prefix - Grafana expects /grafana/* with SERVE_FROM_SUB_PATH=true
            new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
            new Dictionary<string, string>
            {
                ["RequestHeader"] = "X-Forwarded-Proto",
                ["Set"] = "https"
            },
            new Dictionary<string, string>
            {
                ["RequestHeader"] = "X-Forwarded-Prefix",
                ["Set"] = "/grafana"
            },
            new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
            new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
        }
    }
};
var initialProxyClusters = new List<ClusterConfig>
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
    },
    new ClusterConfig
    {
        ClusterId = "grafana",
        HttpRequest = new()
        {
            ActivityTimeout = TimeSpan.FromMinutes(5),
            Version = new Version(1, 1),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        },
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["g1"] = new() { Address = "http://grafana:3000" }
        }
    }
};

// Register a dynamic provider seeded with our initial config
var dynamicProvider = new TansuCloud.Gateway.Services.DynamicProxyConfigProvider(
    initialProxyRoutes,
    initialProxyClusters
);
builder.Services.AddSingleton<IProxyConfigProvider>(dynamicProvider);

// Also expose helper services for admin/runtime
builder.Services.AddSingleton<TansuCloud.Gateway.Services.DynamicProxyConfigProvider>(
    dynamicProvider
);
builder.Services.AddSingleton<IRoutesRuntime, RoutesRuntime>();

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

// Apply audit database migrations (EF-based, idempotent across all services)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions.ApplyAuditMigrationsAsync(
        scope.ServiceProvider,
        logger
    );
}

// Apply policy database migrations and load policies from database
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        // Apply migrations idempotently
        var policyDbContext =
            scope.ServiceProvider.GetRequiredService<TansuCloud.Gateway.Data.PolicyDbContext>();
        await policyDbContext.Database.MigrateAsync();
        logger.LogInformation("Policy database migrations applied successfully");

        // Load policies from database into runtime cache
        var policyRuntime = app.Services.GetRequiredService<IPolicyRuntime>();
        await policyRuntime.LoadFromStoreAsync();
        logger.LogInformation("Policies loaded from database into runtime cache");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to apply policy database migrations or load policies");
        throw;
    }
}

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
// DISABLED: CORS is now handled by PolicyEnforcementMiddleware for policy-based control
// app.UseCors("Default");

// Startup diagnostic: log OIDC metadata source choice (Task 38)
try
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OIDC-Config");
    var configuredIssuer = builder.Configuration["Oidc:Issuer"];
    var issuer = string.IsNullOrWhiteSpace(configuredIssuer)
        ? appUrls.GetIssuer("identity")
        : configuredIssuer!;
    var issuerTrim = issuer.TrimEnd('/');
    var configuredMd = builder.Configuration["Oidc:MetadataAddress"];
    var inContainer = string.Equals(
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
        "true",
        StringComparison.OrdinalIgnoreCase
    );
    var effectiveMd = string.IsNullOrWhiteSpace(configuredMd)
        ? appUrls.GetBackchannelMetadataAddress(inContainer, "identity")
        : configuredMd!;
    var source = string.IsNullOrWhiteSpace(configuredMd)
        ? (inContainer ? "container-gateway" : "issuer-derived")
        : "explicit-config";
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
        // If caller already provided X-Tansu-Tenant, honor it (for E2E tests and internal routing)
        var existingTenant = context.Request.Headers["X-Tansu-Tenant"].FirstOrDefault();
        string? tenantId = existingTenant;

        // Otherwise resolve from host/path
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var result = TenantResolver.Resolve(context.Request.Host.Host, context.Request.Path);
            tenantId = result.TenantId;
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            context.Request.Headers["X-Tansu-Tenant"] = tenantId!;

            // Propagate tenant via activity baggage so OTEL enrichment can tag spans
            var activity = System.Diagnostics.Activity.Current;
            if (activity is not null)
            {
                activity.SetBaggage(TelemetryConstants.Tenant, tenantId);
                // Also set the tag directly on the current activity since ASP.NET Core enrichment has already run
                activity.SetTag(TelemetryConstants.Tenant, tenantId.ToLowerInvariant());
            }
        }

        // Register a callback to set http.status_code after the response is generated
        context.Response.OnStarting(() =>
        {
            var currentActivity = System.Diagnostics.Activity.Current;
            if (currentActivity is not null)
            {
                currentActivity.SetTag("http.status_code", context.Response.StatusCode);
            }
            return Task.CompletedTask;
        });

        await next();
    }
); // End of Middleware Tenant Header

// Enforce simple per-route auth guard at gateway with a safe exception for presigned storage links
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path;
        var requiresAuth = false;
        if (
            path.StartsWithSegments("/db", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/storage", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Allow anonymous health endpoints and Scalar UI for downstream services (Development only for Scalar)
            if (
                !(
                    path.StartsWithSegments("/db/health", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments(
                        "/storage/health",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || (
                        app.Environment.IsDevelopment()
                        && (
                            path.StartsWithSegments(
                                "/db/scalar",
                                StringComparison.OrdinalIgnoreCase
                            )
                            || path.StartsWithSegments(
                                "/db/openapi",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    || (
                        app.Environment.IsDevelopment()
                        && (
                            path.StartsWithSegments(
                                "/storage/scalar",
                                StringComparison.OrdinalIgnoreCase
                            )
                            || path.StartsWithSegments(
                                "/storage/openapi",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                )
            )
            {
                requiresAuth = true;
            }
        }

        // Allow health endpoints unauthenticated for monitoring
        var isHealth =
            path.StartsWithSegments("/db/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/storage/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/identity/health", StringComparison.OrdinalIgnoreCase);

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
Task WriteHealthResponse(HttpContext httpContext, HealthReport report)
{
    httpContext.Response.ContentType = "application/json";
    var status = report.Status.ToString();
    var json = JsonSerializer.Serialize(
        new
        {
            status,
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = e.Value.Duration.TotalMilliseconds,
                    data = e.Value.Data
                }
            )
        }
    );
    return httpContext.Response.WriteAsync(json);
} // End of Method WriteHealthResponse

app.MapHealthChecks(
        "/health/live",
        new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("self"),
            ResponseWriter = WriteHealthResponse
        }
    )
    .AllowAnonymous();
app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = WriteHealthResponse
        }
    )
    .AllowAnonymous();

// Policy enforcement middleware (CORS, IP allow/deny with staged rollout)
app.UseMiddleware<PolicyEnforcementMiddleware>();

// Enable authentication/authorization (required for admin API JWT validation)
app.UseAuthentication();
app.UseAuthorization();

// Development resilience: cache OIDC discovery and JWKS at the gateway with short TTL and
// fallback to last known good response if the upstream temporarily fails (e.g., 502 during restarts).
if (app.Environment.IsDevelopment())
{
    var oidcCacheLogger = app
        .Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Gateway.OIDC-Cache");
    var cache =
        new ConcurrentDictionary<
            string,
            (DateTimeOffset Expires, string Content, string ContentType)
        >();

    // Helper to proxy and cache
    static bool IsValidOidcPayload(string cacheKey, string payload, ILogger logger)
    {
        if (string.Equals(cacheKey, "jwks", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (
                    doc.RootElement.TryGetProperty("keys", out var keysProp)
                    && keysProp.ValueKind == JsonValueKind.Array
                    && keysProp.GetArrayLength() > 0
                )
                {
                    return true;
                }

                logger.LogWarning("OIDC JWKS payload contains no signing keys");
                return false;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "OIDC JWKS payload deserialization failed");
                return false;
            }
        }

        if (string.Equals(cacheKey, "discovery", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (
                    doc.RootElement.TryGetProperty("jwks_uri", out var jwksProp)
                    && jwksProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(jwksProp.GetString())
                )
                {
                    return true;
                }

                logger.LogWarning("OIDC discovery payload missing jwks_uri");
                return false;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "OIDC discovery payload deserialization failed");
                return false;
            }
        }

        return true;
    } // End of Method IsValidOidcPayload

    async Task<IResult> ProxyWithCacheAsync(
        HttpContext http,
        string upstreamUrl,
        string cacheKey,
        string contentType
    )
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            EnableMultipleHttp2Connections = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
            using var resp = await client.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                http.RequestAborted
            );
            var text = await resp.Content.ReadAsStringAsync(http.RequestAborted);
            if (resp.IsSuccessStatusCode && IsValidOidcPayload(cacheKey, text, oidcCacheLogger))
            {
                // DO NOT rewrite issuer - pass through Identity's configured issuer unchanged
                // Identity is configured with the correct public issuer, and tokens match that
                // Rewriting based on request host causes issuer mismatches in container networks

                var ttl = TimeSpan.FromSeconds(30); // short TTL in dev
                cache[cacheKey] = (DateTimeOffset.UtcNow.Add(ttl), text, contentType);
                return Results.Content(text, contentType);
            }
            else
            {
                // Upstream returned a failure; fall back to cache if available and not expired
                if (
                    cache.TryGetValue(cacheKey, out var entry)
                    && entry.Expires > DateTimeOffset.UtcNow
                )
                {
                    oidcCacheLogger.LogWarning(
                        "OIDC cache hit for {Key} due to upstream {Status}",
                        cacheKey,
                        (int)resp.StatusCode
                    );
                    return Results.Content(entry.Content, entry.ContentType);
                }
                return Results.StatusCode((int)resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Network error; serve cached value if present
            if (cache.TryGetValue(cacheKey, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
            {
                oidcCacheLogger.LogWarning(
                    ex,
                    "OIDC cache fallback for {Key} due to exception",
                    cacheKey
                );
                return Results.Content(entry.Content, entry.ContentType);
            }
            oidcCacheLogger.LogError(ex, "OIDC upstream failure and no cache for {Key}", cacheKey);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    // Discovery document
    app.MapGet(
            "/identity/.well-known/openid-configuration",
            async (HttpContext http, IConfiguration cfg) =>
            {
                var identityBase = ResolveServiceBaseUrl("Services:IdentityBaseUrl", 5095, cfg);
                await IdentityReadiness.EnsureReadyAsync(
                    identityBase,
                    oidcCacheLogger,
                    http.RequestAborted
                );
                var url = identityBase.TrimEnd('/') + "/.well-known/openid-configuration";
                return await ProxyWithCacheAsync(http, url, "discovery", "application/json");
            }
        )
        .WithDisplayName("Dev: OIDC discovery with cache")
        .AllowAnonymous();

    // Root alias for discovery (/.well-known/openid-configuration)
    app.MapGet(
            "/.well-known/openid-configuration",
            async (HttpContext http, IConfiguration cfg) =>
            {
                var identityBase = ResolveServiceBaseUrl("Services:IdentityBaseUrl", 5095, cfg);
                await IdentityReadiness.EnsureReadyAsync(
                    identityBase,
                    oidcCacheLogger,
                    http.RequestAborted
                );
                var url = identityBase.TrimEnd('/') + "/.well-known/openid-configuration";
                return await ProxyWithCacheAsync(http, url, "discovery", "application/json");
            }
        )
        .WithDisplayName("Dev: OIDC discovery alias with cache")
        .AllowAnonymous();

    // JWKS endpoint
    app.MapGet(
            "/identity/.well-known/jwks",
            async (HttpContext http, IConfiguration cfg) =>
            {
                var identityBase = ResolveServiceBaseUrl("Services:IdentityBaseUrl", 5095, cfg);
                await IdentityReadiness.EnsureReadyAsync(
                    identityBase,
                    oidcCacheLogger,
                    http.RequestAborted
                );
                var url = identityBase.TrimEnd('/') + "/.well-known/jwks";
                return await ProxyWithCacheAsync(http, url, "jwks", "application/json");
            }
        )
        .WithDisplayName("Dev: OIDC JWKS with cache")
        .AllowAnonymous();

    // Root alias for JWKS (/.well-known/jwks)
    app.MapGet(
            "/.well-known/jwks",
            async (HttpContext http, IConfiguration cfg) =>
            {
                var identityBase = ResolveServiceBaseUrl("Services:IdentityBaseUrl", 5095, cfg);
                await IdentityReadiness.EnsureReadyAsync(
                    identityBase,
                    oidcCacheLogger,
                    http.RequestAborted
                );
                var url = identityBase.TrimEnd('/') + "/.well-known/jwks";
                return await ProxyWithCacheAsync(http, url, "jwks", "application/json");
            }
        )
        .WithDisplayName("Dev: OIDC JWKS alias with cache")
        .AllowAnonymous();
}

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
                    audit.TryEnqueueRedacted(
                        ev,
                        new { Path = http.Request?.Path.Value },
                        new[] { "Path" }
                    );
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
                        audit.TryEnqueueRedacted(
                            ev,
                            new { Path = http.Request?.Path.Value },
                            new[] { "Path" }
                        );
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

// Policy Center admin endpoints (Task 17 - Policy Center)
adminGroup
    .MapGet(
        "/policies",
        async (IPolicyRuntime runtime) =>
        {
            var policies = await runtime.GetAllAsync();
            return Results.Json(policies);
        }
    )
    .WithDisplayName("Admin: List all policies");

adminGroup
    .MapGet(
        "/policies/{id}",
        async (IPolicyRuntime runtime, string id) =>
        {
            var policy = await runtime.GetByIdAsync(id);
            return policy is not null ? Results.Json(policy) : Results.NotFound();
        }
    )
    .WithDisplayName("Admin: Get policy by ID");

adminGroup
    .MapPost(
        "/policies",
        async (
            HttpContext http,
            IPolicyRuntime runtime,
            JsonElement body,
            ILoggerFactory loggerFactory,
            IAuditLogger audit
        ) =>
        {
            var adminLogger = loggerFactory.CreateLogger("Gateway.Admin");
            var remote = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            try
            {
                // Parse policy entry
                var id = body.GetProperty("id").GetString();

                // Handle both integer and string formats for type
                var typeProp = body.GetProperty("type");
                string? typeStr =
                    typeProp.ValueKind == JsonValueKind.Number
                        ? typeProp.GetInt32().ToString()
                        : typeProp.GetString();

                // Handle both integer and string formats for mode
                var modeProp = body.GetProperty("mode");
                string? modeStr =
                    modeProp.ValueKind == JsonValueKind.Number
                        ? modeProp.GetInt32().ToString()
                        : modeProp.GetString();

                var description = body.TryGetProperty("description", out var descProp)
                    ? descProp.GetString()
                    : string.Empty;
                var config = body.GetProperty("config");

                if (
                    string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(typeStr)
                    || string.IsNullOrWhiteSpace(modeStr)
                )
                {
                    return Results.Problem(
                        title: "Invalid policy",
                        detail: "Policy must have id, type, and mode.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                if (
                    !Enum.TryParse<PolicyType>(typeStr, ignoreCase: true, out var policyType)
                    || !Enum.TryParse<PolicyEnforcementMode>(
                        modeStr,
                        ignoreCase: true,
                        out var enforcementMode
                    )
                )
                {
                    return Results.Problem(
                        title: "Invalid policy",
                        detail: "Invalid type or mode value.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                // Extract enabled flag (default to true if not specified)
                var enabled = body.TryGetProperty("enabled", out var enabledProp)
                    ? enabledProp.GetBoolean()
                    : true;

                var policy = new PolicyEntry
                {
                    Id = id!,
                    Type = policyType,
                    Mode = enforcementMode,
                    Description = description ?? string.Empty,
                    Config = config,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Enabled = enabled
                };

                await runtime.UpsertAsync(policy);

                adminLogger.LogInformation(
                    "Policy upserted by {Remote}: Id={PolicyId}, Type={Type}, Mode={Mode}",
                    remote,
                    policy.Id,
                    policy.Type,
                    policy.Mode
                );

                // Audit success
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "PolicyUpsert",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            PolicyId = policy.Id,
                            Type = policy.Type,
                            Mode = policy.Mode
                        },
                        new[] { "PolicyId", "Type", "Mode" }
                    );
                }
                catch { }

                return Results.Json(policy);
            }
            catch (Exception ex)
            {
                adminLogger.LogError(ex, "Failed to upsert policy");
                return Results.Problem(
                    title: "Error upserting policy",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        }
    )
    .WithDisplayName("Admin: Create or update policy");

adminGroup
    .MapDelete(
        "/policies/{id}",
        async (
            HttpContext http,
            IPolicyRuntime runtime,
            string id,
            ILoggerFactory loggerFactory,
            IAuditLogger audit
        ) =>
        {
            var adminLogger = loggerFactory.CreateLogger("Gateway.Admin");
            var remote = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var policy = await runtime.GetByIdAsync(id);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var removed = await runtime.DeleteAsync(id);
            if (removed)
            {
                adminLogger.LogInformation(
                    "Policy deleted by {Remote}: Id={PolicyId}, Type={Type}",
                    remote,
                    id,
                    policy.Type
                );

                // Audit success
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "PolicyDelete",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new { PolicyId = id, Type = policy.Type },
                        new[] { "PolicyId", "Type" }
                    );
                }
                catch { }

                return Results.NoContent();
            }

            return Results.Problem(
                title: "Error deleting policy",
                detail: "Failed to remove policy.",
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    )
    .WithDisplayName("Admin: Delete policy");

// Observability Settings endpoints (Task 47 Phase 5 - Retention/Sampling Management)
adminGroup
    .MapGet(
        "/observability/settings",
        async (TansuCloud.Gateway.Data.PolicyDbContext db) =>
        {
            var settings = await db.ObservabilitySettings.OrderBy(s => s.Component).ToListAsync();
            return Results.Json(settings);
        }
    )
    .WithDisplayName("Admin: List observability settings");

adminGroup
    .MapGet(
        "/observability/settings/{component}",
        async (TansuCloud.Gateway.Data.PolicyDbContext db, string component) =>
        {
            var setting = await db.ObservabilitySettings
                .FirstOrDefaultAsync(s => s.Component == component.ToLowerInvariant());
            return setting is not null ? Results.Json(setting) : Results.NotFound();
        }
    )
    .WithDisplayName("Admin: Get observability setting by component");

adminGroup
    .MapPut(
        "/observability/settings/{component}",
        async (
            HttpContext http,
            TansuCloud.Gateway.Data.PolicyDbContext db,
            string component,
            JsonElement body,
            ILoggerFactory loggerFactory,
            IAuditLogger audit
        ) =>
        {
            var logger = loggerFactory.CreateLogger("Gateway.ObservabilitySettings");
            var remote = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            try
            {
                // Get admin identity from claims
                var adminEmail = http.User.FindFirst("email")?.Value
                    ?? http.User.FindFirst("sub")?.Value
                    ?? "unknown";

                // Find existing setting
                var setting = await db.ObservabilitySettings
                    .FirstOrDefaultAsync(s => s.Component == component.ToLowerInvariant());

                if (setting is null)
                {
                    return Results.NotFound();
                }

                // Parse update request
                var retentionDays = body.TryGetProperty("retentionDays", out var retProp)
                    ? retProp.GetInt32()
                    : setting.RetentionDays;
                var samplingPercent = body.TryGetProperty("samplingPercent", out var sampProp)
                    ? sampProp.GetInt32()
                    : setting.SamplingPercent;
                var enabled = body.TryGetProperty("enabled", out var enabledProp)
                    ? enabledProp.GetBoolean()
                    : setting.Enabled;

                // Validation
                if (retentionDays < 1 || retentionDays > 365)
                {
                    return Results.Problem(
                        title: "Invalid retention days",
                        detail: "Retention days must be between 1 and 365.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                if (samplingPercent < 0 || samplingPercent > 100)
                {
                    return Results.Problem(
                        title: "Invalid sampling percent",
                        detail: "Sampling percent must be between 0 and 100.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                // Store old values for audit
                var oldValues = new
                {
                    RetentionDays = setting.RetentionDays,
                    SamplingPercent = setting.SamplingPercent,
                    Enabled = setting.Enabled
                };

                // Update setting
                setting.RetentionDays = retentionDays;
                setting.SamplingPercent = samplingPercent;
                setting.Enabled = enabled;
                setting.UpdatedAt = DateTime.UtcNow;
                setting.UpdatedBy = adminEmail;

                await db.SaveChangesAsync();

                // Audit log
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "ObservabilitySettingsUpdate",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            Component = component,
                            OldRetentionDays = oldValues.RetentionDays,
                            NewRetentionDays = setting.RetentionDays,
                            OldSamplingPercent = oldValues.SamplingPercent,
                            NewSamplingPercent = setting.SamplingPercent,
                            OldEnabled = oldValues.Enabled,
                            NewEnabled = setting.Enabled
                        },
                        new[] { "Component", "OldRetentionDays", "NewRetentionDays", "OldSamplingPercent", "NewSamplingPercent", "OldEnabled", "NewEnabled" }
                    );
                }
                catch { }

                logger.LogInformation(
                    "Admin {Admin} updated {Component} observability settings: Retention={Retention}d, Sampling={Sampling}%, Enabled={Enabled}",
                    adminEmail, component, retentionDays, samplingPercent, enabled
                );

                return Results.Json(setting);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update observability setting {Component}", component);
                return Results.Problem(
                    title: "Update failed",
                    detail: $"Failed to update {component} settings: {ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        }
    )
    .WithDisplayName("Admin: Update observability setting");

// Policy simulator endpoints (Task 17 - Cache & Rate Limit Policies)
adminGroup
    .MapPost(
        "/policies/simulate/cache",
        async (
            HttpContext http,
            HttpRequest request,
            JsonElement body,
            ILoggerFactory loggerFactory
        ) =>
        {
            var logger = loggerFactory.CreateLogger("Gateway.PolicySimulator");
            try
            {
                // Parse request
                var policyId = body.GetProperty("policyId").GetString();
                var config = body.GetProperty("config");
                var req = body.GetProperty("request");
                var url = req.GetProperty("url").GetString();
                var method = req.GetProperty("method").GetString() ?? "GET";
                var headers = req.TryGetProperty("headers", out var hdrProp)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(hdrProp.GetRawText())
                    : new Dictionary<string, string>();

                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return Results.Problem(
                        title: "Invalid request",
                        detail: "URL is required.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                // Parse cache config
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cacheConfig = JsonSerializer.Deserialize<CacheConfig>(
                    config.GetRawText(),
                    jsonOptions
                );
                if (cacheConfig is null)
                {
                    return Results.Problem(
                        title: "Invalid config",
                        detail: "Cache configuration is required.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                // Build cache key (simplified simulation)
                var keyParts = new List<string> { method.ToUpperInvariant(), url };

                var varyByParams = new List<string>();

                if (cacheConfig.VaryByHost && headers.ContainsKey("Host"))
                {
                    keyParts.Add($"h:{headers["Host"]}");
                    varyByParams.Add("Host");
                }

                if (cacheConfig.VaryByQuery is not null)
                {
                    var uri = new Uri(url, UriKind.RelativeOrAbsolute);
                    if (!uri.IsAbsoluteUri)
                    {
                        uri = new Uri("http://localhost" + url);
                    }
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

                    if (cacheConfig.VaryByQuery.Count == 0)
                    {
                        // Explicit empty list = don't vary by query
                    }
                    else
                    {
                        foreach (var key in cacheConfig.VaryByQuery)
                        {
                            var value = query[key];
                            if (value != null)
                            {
                                keyParts.Add($"q:{key}={value}");
                                varyByParams.Add($"Query.{key}");
                            }
                        }
                    }
                }

                if (cacheConfig.VaryByHeaders?.Count > 0)
                {
                    foreach (var header in cacheConfig.VaryByHeaders)
                    {
                        if (headers.ContainsKey(header))
                        {
                            keyParts.Add($"hdr:{header}={headers[header]}");
                            varyByParams.Add($"Header.{header}");
                        }
                    }
                }

                if (cacheConfig.VaryByRouteValues?.Count > 0)
                {
                    foreach (var route in cacheConfig.VaryByRouteValues)
                    {
                        // Simulated route extraction (simplified)
                        keyParts.Add($"route:{route}=<extracted>");
                        varyByParams.Add($"Route.{route}");
                    }
                }

                var cacheKey = string.Join("|", keyParts);

                // Simulate cache miss (always miss in simulation)
                var result = new
                {
                    cacheHit = false,
                    cacheKey,
                    ttlSeconds = cacheConfig.TtlSeconds, // Use actual TTL from config
                    varyByParameters = varyByParams,
                    message = "Simulation always returns MISS. In production, subsequent requests with identical cache key would HIT."
                };

                logger.LogInformation(
                    "Cache policy simulation: PolicyId={PolicyId}, CacheKey={CacheKey}, TTL={TtlSeconds}",
                    policyId,
                    cacheKey,
                    cacheConfig.TtlSeconds
                );

                return Results.Json(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cache policy simulation failed");
                return Results.Problem(
                    title: "Simulation error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        }
    )
    .WithDisplayName("Admin: Simulate cache policy");

// Helper function to extract client IP from headers or connection
static string GetClientIp(HttpContext http, Dictionary<string, string> headers)
{
    // Check X-Forwarded-For header first (for tests and proxied requests)
    if (
        headers.TryGetValue("X-Forwarded-For", out var forwardedFor)
        && !string.IsNullOrWhiteSpace(forwardedFor)
    )
    {
        // X-Forwarded-For can contain multiple IPs, take the first one
        var firstIp = forwardedFor.Split(',')[0].Trim();
        return $"ip:{firstIp}";
    }

    // Fallback to connection RemoteIpAddress
    var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return $"ip:{remoteIp}";
} // End of GetClientIp helper

adminGroup
    .MapPost(
        "/policies/simulate/rate-limit",
        async (
            HttpContext http,
            HttpRequest request,
            JsonElement body,
            ILoggerFactory loggerFactory
        ) =>
        {
            var logger = loggerFactory.CreateLogger("Gateway.PolicySimulator");
            try
            {
                // Parse request
                var policyId = body.GetProperty("policyId").GetString();
                var config = body.GetProperty("config");
                var req = body.GetProperty("request");
                var url = req.GetProperty("url").GetString();
                var method = req.GetProperty("method").GetString() ?? "GET";
                var headers = req.TryGetProperty("headers", out var hdrProp)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(hdrProp.GetRawText())
                    : new Dictionary<string, string>();
                var userId = req.TryGetProperty("userId", out var userProp)
                    ? userProp.GetString()
                    : null;

                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return Results.Problem(
                        title: "Invalid request",
                        detail: "URL is required.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                // Parse rate limit config
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var rateLimitConfig = JsonSerializer.Deserialize<RateLimitConfig>(
                    config.GetRawText(),
                    jsonOptions
                );
                if (rateLimitConfig is null)
                {
                    return Results.Problem(
                        title: "Invalid config",
                        detail: "Rate limit configuration is required.",
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                // Determine partition key based on strategy
                string partitionKey = rateLimitConfig.PartitionStrategy switch
                {
                    "Global" => "global",
                    "PerIp" => GetClientIp(http, headers),
                    "PerUser"
                        => string.IsNullOrWhiteSpace(userId) ? "user:anonymous" : $"user:{userId}",
                    "PerHost"
                        => headers.ContainsKey("Host") ? $"host:{headers["Host"]}" : "host:unknown",
                    _ => "global"
                };

                // Simulate rate limit evaluation (always allow in simulation)
                var result = new
                {
                    allowed = true,
                    partitionKey,
                    permitLimit = rateLimitConfig.PermitLimit,
                    permitsRemaining = rateLimitConfig.PermitLimit - 1, // Simulate 1 request consumed
                    windowSeconds = rateLimitConfig.WindowSeconds,
                    retryAfterSeconds = rateLimitConfig.RetryAfterSeconds
                        ?? rateLimitConfig.WindowSeconds,
                    message = $"Simulation uses partition strategy '{rateLimitConfig.PartitionStrategy}'. In production, requests would be counted per partition key."
                };

                logger.LogInformation(
                    "Rate limit policy simulation: PolicyId={PolicyId}, PartitionKey={PartitionKey}, Strategy={Strategy}",
                    policyId,
                    partitionKey,
                    rateLimitConfig.PartitionStrategy
                );

                return Results.Json(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rate limit policy simulation failed");
                return Results.Problem(
                    title: "Simulation error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        }
    )
    .WithDisplayName("Admin: Simulate rate limit policy");

// Domains/TLS admin endpoints (scaffold)
adminGroup
    .MapGet("/domains", (IDomainTlsRuntime runtime) => Results.Json(runtime.List()))
    .WithDisplayName("Admin: List domain bindings");

adminGroup
    .MapPost(
        "/domains",
        (
            HttpContext http,
            IDomainTlsRuntime runtime,
            DomainBindRequestDto body,
            ILoggerFactory loggerFactory,
            IAuditLogger audit
        ) =>
        {
            if (body is null)
            {
                return Results.Problem(
                    title: "Invalid body",
                    detail: "Request body is required.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(body.Host))
                errors.Add("Host is required.");
            if (string.IsNullOrWhiteSpace(body.PfxBase64))
                errors.Add("PfxBase64 is required (PEM not yet supported).");
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["domains"] = errors.ToArray() },
                    statusCode: 400,
                    title: "Validation failed"
                );
            }

            try
            {
                var info = runtime.AddOrReplace(body);
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "DomainBind",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            info.Host,
                            info.Thumbprint,
                            info.NotAfter
                        },
                        new[] { "Host", "Thumbprint", "NotAfter" }
                    );
                }
                catch { }
                return Results.Json(info);
            }
            catch (FormatException)
            {
                return Results.Problem(
                    title: "Invalid certificate",
                    detail: "PFX data is not valid base64.",
                    statusCode: 400
                );
            }
            catch (CryptographicException ex)
            {
                return Results.Problem(
                    title: "Certificate error",
                    detail: ex.Message,
                    statusCode: 400
                );
            }
        }
    )
    .WithDisplayName("Admin: Add or replace domain binding");

// Add/replace via PEM (certificate + private key)
adminGroup
    .MapPost(
        "/domains/pem",
        (
            HttpContext http,
            IDomainTlsRuntime runtime,
            DomainBindPemRequestDto body,
            IAuditLogger audit
        ) =>
        {
            if (body is null)
            {
                return Results.Problem(
                    title: "Invalid body",
                    detail: "Request body is required.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(body.Host))
                errors.Add("Host is required.");
            if (string.IsNullOrWhiteSpace(body.CertPem))
                errors.Add("CertPem is required.");
            if (string.IsNullOrWhiteSpace(body.KeyPem))
                errors.Add("KeyPem is required.");
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["domains"] = errors.ToArray() },
                    statusCode: 400,
                    title: "Validation failed"
                );
            }
            try
            {
                var info = runtime.AddOrReplacePem(body);
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "DomainBindPem",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            info.Host,
                            info.Thumbprint,
                            info.NotAfter
                        },
                        new[] { "Host", "Thumbprint", "NotAfter" }
                    );
                }
                catch { }
                return Results.Json(info);
            }
            catch (CryptographicException ex)
            {
                return Results.Problem(
                    title: "Certificate error",
                    detail: ex.Message,
                    statusCode: 400
                );
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "Invalid input", detail: ex.Message, statusCode: 400);
            }
        }
    )
    .WithDisplayName("Admin: Add or replace domain binding (PEM)");

// Rotate binding with new cert (returns previous metadata too)
adminGroup
    .MapPost(
        "/domains/rotate",
        (
            HttpContext http,
            IDomainTlsRuntime runtime,
            DomainRotateRequestDto body,
            IAuditLogger audit
        ) =>
        {
            if (body is null)
            {
                return Results.Problem(
                    title: "Invalid body",
                    detail: "Request body is required.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            if (string.IsNullOrWhiteSpace(body.Host))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["domains"] = new[] { "Host is required." }
                    },
                    statusCode: 400,
                    title: "Validation failed"
                );
            }
            var hasPfx = !string.IsNullOrWhiteSpace(body.PfxBase64);
            var hasPem =
                !string.IsNullOrWhiteSpace(body.CertPem) && !string.IsNullOrWhiteSpace(body.KeyPem);
            if (!hasPfx && !hasPem)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["domains"] = new[] { "Provide PfxBase64 or (CertPem + KeyPem)." }
                    },
                    statusCode: 400,
                    title: "Validation failed"
                );
            }
            try
            {
                var (current, previous) = runtime.Rotate(body);
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "DomainRotate",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            current.Host,
                            current.Thumbprint,
                            current.NotAfter,
                            PreviousThumbprint = previous?.Thumbprint
                        },
                        new[] { "Host", "Thumbprint", "NotAfter", "PreviousThumbprint" }
                    );
                }
                catch { }
                return Results.Json(new { current, previous });
            }
            catch (FormatException)
            {
                return Results.Problem(
                    title: "Invalid certificate",
                    detail: "PFX data is not valid base64.",
                    statusCode: 400
                );
            }
            catch (CryptographicException ex)
            {
                return Results.Problem(
                    title: "Certificate error",
                    detail: ex.Message,
                    statusCode: 400
                );
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "Invalid input", detail: ex.Message, statusCode: 400);
            }
        }
    )
    .WithDisplayName("Admin: Rotate domain binding");

adminGroup
    .MapDelete(
        "/domains/{host}",
        (HttpContext http, IDomainTlsRuntime runtime, string host, IAuditLogger audit) =>
        {
            if (string.IsNullOrWhiteSpace(host))
                return Results.Problem("Host is required", statusCode: 400);
            var removed = runtime.Remove(host);
            if (!removed)
                return Results.NotFound();
            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "DomainUnbind",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev, new { Host = host }, new[] { "Host" });
            }
            catch { }
            return Results.NoContent();
        }
    )
    .WithDisplayName("Admin: Remove domain binding");

// Routes (YARP) admin endpoints - list/apply/rollback
adminGroup
    .MapGet(
        "/routes",
        (DynamicProxyConfigProvider provider) =>
        {
            var (routes, clusters) = provider.GetSnapshot();
            return Results.Json(new { routes, clusters });
        }
    )
    .WithDisplayName("Admin: Get YARP routes");

adminGroup
    .MapPost(
        "/routes",
        (
            HttpContext http,
            DynamicProxyConfigProvider provider,
            IRoutesRuntime runtime,
            IAuditLogger audit,
            [FromBody] RoutesUpdateDto dto
        ) =>
        {
            if (dto is null)
            {
                return Results.Problem(
                    title: "Invalid body",
                    detail: "Request body is required.",
                    statusCode: 400
                );
            }
            dto.Routes ??= new();
            dto.Clusters ??= new();
            // Minimal validation: unique RouteId/ClusterId and referenced clusters exist
            var errors = new List<string>();
            var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in dto.Routes)
            {
                if (string.IsNullOrWhiteSpace(r.RouteId))
                    errors.Add("RouteId is required for all routes.");
                else if (!routeIds.Add(r.RouteId))
                    errors.Add($"Duplicate RouteId '{r.RouteId}'.");
                if (r.Match?.Path is null)
                    errors.Add($"Route '{r.RouteId}' must specify Match.Path.");
                if (string.IsNullOrWhiteSpace(r.ClusterId))
                    errors.Add($"Route '{r.RouteId}' must specify ClusterId.");
            }
            var clusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in dto.Clusters)
            {
                if (string.IsNullOrWhiteSpace(c.ClusterId))
                    errors.Add("ClusterId is required for all clusters.");
                else if (!clusterIds.Add(c.ClusterId))
                    errors.Add($"Duplicate ClusterId '{c.ClusterId}'.");
                if (c.Destinations is null || c.Destinations.Count == 0)
                    errors.Add($"Cluster '{c.ClusterId}' must have at least one destination.");
            }
            // Verify route->cluster references
            foreach (var r in dto.Routes)
            {
                if (!string.IsNullOrWhiteSpace(r.ClusterId) && !clusterIds.Contains(r.ClusterId))
                    errors.Add($"Route '{r.RouteId}' references missing cluster '{r.ClusterId}'.");
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["routes"] = errors.ToArray() },
                    statusCode: 400,
                    title: "Validation failed"
                );
            }

            // Save previous snapshot for rollback then apply
            var (prevRoutes, prevClusters) = provider.GetSnapshot();
            runtime.SetPrevious(prevRoutes, prevClusters);
            provider.Update(dto.Routes, dto.Clusters);

            // Audit success (no secrets)
            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "RoutesUpdate",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new { Routes = dto.Routes.Count, Clusters = dto.Clusters.Count },
                    new[] { "Routes", "Clusters" }
                );
            }
            catch { }

            var (routesAfter, clustersAfter) = provider.GetSnapshot();
            return Results.Json(new { routes = routesAfter, clusters = clustersAfter });
        }
    )
    .WithDisplayName("Admin: Set YARP routes");

adminGroup
    .MapPost(
        "/routes/rollback",
        (
            HttpContext http,
            DynamicProxyConfigProvider provider,
            IRoutesRuntime runtime,
            IAuditLogger audit
        ) =>
        {
            var prev = runtime.GetPrevious();
            if (prev is null)
            {
                return Results.Problem(
                    title: "Nothing to rollback",
                    detail: "No previous snapshot stored.",
                    statusCode: 400
                );
            }
            provider.Update(prev.Value.Routes, prev.Value.Clusters);
            runtime.ClearPrevious();

            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "RoutesRollback",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev, new { ok = true }, new[] { "ok" });
            }
            catch { }

            var (routesAfter, clustersAfter) = provider.GetSnapshot();
            return Results.Json(new { routes = routesAfter, clusters = clustersAfter });
        }
    )
    .WithDisplayName("Admin: Rollback YARP routes");

// Routes (YARP) health probe: per-cluster destination readiness snapshot
adminGroup
    .MapGet(
        "/routes/health",
        async (HttpContext http, DynamicProxyConfigProvider provider, [FromQuery] string? path) =>
        {
            // Default health path if not specified: "/health/ready"
            var healthPath = string.IsNullOrWhiteSpace(path) ? "/health/ready" : path!.Trim();
            if (!healthPath.StartsWith('/'))
                healthPath = "/" + healthPath;

            var (_, clusters) = provider.GetSnapshot();
            var results = new List<object>();

            // Local HttpClient configured similar to our proxy constraints
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                EnableMultipleHttp2Connections = false,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            foreach (var c in clusters)
            {
                var dests = new List<object>();
                if (c.Destinations is not null)
                {
                    foreach (var kv in c.Destinations)
                    {
                        var name = kv.Key;
                        var address = kv.Value?.Address ?? string.Empty;
                        var url = string.IsNullOrWhiteSpace(address)
                            ? string.Empty
                            : address.TrimEnd('/') + healthPath;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        int status = 0;
                        bool ok = false;
                        string? error = null;
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            using var resp = await client.SendAsync(
                                req,
                                HttpCompletionOption.ResponseHeadersRead,
                                http.RequestAborted
                            );
                            status = (int)resp.StatusCode;
                            ok = resp.IsSuccessStatusCode;
                        }
                        catch (OperationCanceledException)
                        {
                            error = "canceled";
                        }
                        catch (Exception ex)
                        {
                            error = ex.GetType().Name + ": " + ex.Message;
                        }
                        finally
                        {
                            sw.Stop();
                        }
                        dests.Add(
                            new
                            {
                                name,
                                address,
                                url,
                                status,
                                ok,
                                elapsedMs = sw.ElapsedMilliseconds,
                                error
                            }
                        );
                    }
                }
                results.Add(new { clusterId = c.ClusterId, destinations = dests });
            }

            return Results.Json(new { path = healthPath, clusters = results });
        }
    )
    .WithDisplayName("Admin: YARP clusters health");

// OutputCache admin endpoints - get/update TTL configuration
adminGroup
    .MapGet("/output-cache", (IOutputCacheRuntime runtime) => Results.Json(runtime.GetCurrent()))
    .WithDisplayName("Admin: Get output cache config");

adminGroup
    .MapPost(
        "/output-cache",
        (
            HttpContext http,
            IOutputCacheRuntime runtime,
            IAuditLogger audit,
            [FromBody] OutputCacheConfig body
        ) =>
        {
            if (body is null)
            {
                return Results.Problem(
                    title: "Invalid body",
                    detail: "Request body is required.",
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            var errors = new List<string>();
            if (body.DefaultTtlSeconds < 0)
                errors.Add("DefaultTtlSeconds must be >= 0.");
            if (body.StaticTtlSeconds < 0)
                errors.Add("StaticTtlSeconds must be >= 0.");
            if (errors.Count > 0)
            {
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "OutputCacheUpdate",
                        Outcome = "Failure",
                        ReasonCode = "ValidationError",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(ev, new { Errors = errors }, new[] { "Errors" });
                }
                catch { }
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["output-cache"] = errors.ToArray() },
                    statusCode: 400,
                    title: "Validation failed"
                );
            }

            try
            {
                var before = runtime.GetCurrent();
                runtime.Update(body);
                var after = runtime.GetCurrent();

                // Audit success event
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "OutputCacheUpdate",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        ev,
                        new
                        {
                            BeforeDefault = before.DefaultTtlSeconds,
                            AfterDefault = after.DefaultTtlSeconds,
                            BeforeStatic = before.StaticTtlSeconds,
                            AfterStatic = after.StaticTtlSeconds
                        },
                        new[] { "BeforeDefault", "AfterDefault", "BeforeStatic", "AfterStatic" }
                    );
                }
                catch { }

                return Results.Json(after);
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Update failed", detail: ex.Message, statusCode: 500);
            }
        }
    )
    .WithDisplayName("Admin: Update output cache config");

// Observability governance admin endpoint - return static dev defaults
// Note: In production, this would read from SigNoz API or mounted config file
adminGroup
    .MapGet(
        "/observability/governance",
        () =>
        {
            // Return static governance config (dev defaults)
            // This matches SigNoz/governance.dev.json structure
            var config = new
            {
                retentionDays = new
                {
                    traces = 7,
                    logs = 7,
                    metrics = 14
                },
                sampling = new { traceRatio = 1.0 },
                alertSLOs = new[]
                {
                    new
                    {
                        id = "gateway_error_rate",
                        description = "Gateway 5xx rate over 5m should stay < 1%",
                        service = "tansu.gateway",
                        kind = "error_rate",
                        windowMinutes = 5,
                        threshold = 0.01,
                        comparison = "<"
                    },
                    new
                    {
                        id = "identity_latency",
                        description = "Identity p95 latency over 5m should be < 300ms",
                        service = "tansu.identity",
                        kind = "latency_p95",
                        windowMinutes = 5,
                        threshold = 300.0,
                        comparison = "<"
                    }
                }
            };

            return Results.Json(config);
        }
    )
    .WithDisplayName("Admin: Get observability governance config");

// Observability governance admin endpoint - save configuration (dev-only for now)
adminGroup
    .MapPost(
        "/observability/governance",
        async (HttpContext http) =>
        {
            try
            {
                // Read the incoming config from request body
                var config = await http.Request.ReadFromJsonAsync<JsonElement>();

                if (
                    config.ValueKind == JsonValueKind.Undefined
                    || config.ValueKind == JsonValueKind.Null
                )
                {
                    return Results.BadRequest(new { error = "Invalid configuration payload" });
                }

                // In development, write to SigNoz/governance.dev.json
                var governanceFilePath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "SigNoz",
                    "governance.dev.json"
                );

                // Ensure directory exists
                var governanceDir = Path.GetDirectoryName(governanceFilePath);
                if (!string.IsNullOrEmpty(governanceDir) && !Directory.Exists(governanceDir))
                {
                    Directory.CreateDirectory(governanceDir);
                }

                // Write formatted JSON to file
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var jsonString = JsonSerializer.Serialize(config, jsonOptions);
                await File.WriteAllTextAsync(governanceFilePath, jsonString);

                // Log the save operation
                var logger = http.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogInformation(
                    "Observability governance configuration saved to {FilePath} by user {User}",
                    governanceFilePath,
                    http.User.Identity?.Name ?? "anonymous"
                );

                return Results.Ok(
                    new
                    {
                        message = "Configuration saved successfully. Run 'SigNoz: governance (apply)' task to apply changes.",
                        filePath = governanceFilePath
                    }
                );
            }
            catch (Exception ex)
            {
                var logger = http.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Error saving observability governance configuration");
                return Results.Problem(
                    title: "Failed to save configuration",
                    detail: ex.Message,
                    statusCode: 500
                );
            }
        }
    )
    .WithDisplayName("Admin: Save observability governance config");

// Observability config aliases (backwards compatibility + cleaner URLs)
// These are identical to the /governance endpoints above, just cleaner naming
adminGroup
    .MapGet(
        "/observability/config",
        () =>
        {
            // Return static governance config (dev defaults)
            var config = new
            {
                retentionDays = new
                {
                    traces = 7,
                    logs = 7,
                    metrics = 14
                },
                sampling = new { traceRatio = 1.0 },
                alertSLOs = new[]
                {
                    new
                    {
                        id = "gateway_error_rate",
                        description = "Gateway 5xx rate over 5m should stay < 1%",
                        service = "tansu.gateway",
                        kind = "error_rate",
                        windowMinutes = 5,
                        threshold = 0.01,
                        comparison = "<"
                    }
                }
            };

            return Results.Json(config);
        }
    )
    .WithDisplayName("Admin: Get observability config");

adminGroup
    .MapPost(
        "/observability/config",
        async (HttpContext http) =>
        {
            try
            {
                var config = await http.Request.ReadFromJsonAsync<JsonElement>();

                if (
                    config.ValueKind == JsonValueKind.Undefined
                    || config.ValueKind == JsonValueKind.Null
                )
                {
                    return Results.BadRequest(new { error = "Invalid configuration payload" });
                }

                var governanceFilePath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "SigNoz",
                    "governance.dev.json"
                );

                var governanceDir = Path.GetDirectoryName(governanceFilePath);
                if (!string.IsNullOrEmpty(governanceDir) && !Directory.Exists(governanceDir))
                {
                    Directory.CreateDirectory(governanceDir);
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var jsonString = JsonSerializer.Serialize(config, jsonOptions);
                await File.WriteAllTextAsync(governanceFilePath, jsonString);

                var logger = http.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogInformation(
                    "Observability configuration saved to {FilePath} by user {User}",
                    governanceFilePath,
                    http.User.Identity?.Name ?? "anonymous"
                );

                return Results.Ok(
                    new
                    {
                        message = "Configuration saved successfully. Run 'SigNoz: governance (apply)' task to apply changes.",
                        filePath = governanceFilePath
                    }
                );
            }
            catch (Exception ex)
            {
                var logger = http.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Error saving observability configuration");
                return Results.Problem(
                    title: "Failed to save configuration",
                    detail: ex.Message,
                    statusCode: 500
                );
            }
        }
    )
    .WithDisplayName("Admin: Save observability config");

// Phase 0: dynamic log level override shim (Development only)
if (app.Environment.IsDevelopment())
{
    adminGroup
        .MapPost(
            "/logging/overrides",
            (
                HttpContext http,
                IDynamicLogLevelOverride ovr,
                [AsParameters] LogOverrideRequest req,
                IAuditLogger audit
            ) =>
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
                        audit.TryEnqueueRedacted(
                            ev,
                            new { Error = "CategoryRequired" },
                            new[] { "Error" }
                        );
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
                        audit.TryEnqueueRedacted(
                            ev,
                            new { Error = "TtlSecondsInvalid" },
                            new[] { "Error" }
                        );
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
                        new
                        {
                            req.Category,
                            Level = req.Level.ToString(),
                            req.TtlSeconds
                        },
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

// Expose Prometheus metrics endpoint for scraping (Task 47 Phase 4)
app.MapPrometheusScrapingEndpoint();

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

static class IdentityReadiness
{
    internal static readonly SemaphoreSlim Lock = new(1, 1);
    internal static int ReadyFlag;

    internal static async Task EnsureReadyAsync(
        string identityBase,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (Volatile.Read(ref ReadyFlag) == 1)
        {
            return;
        }

        await Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref ReadyFlag) == 1)
            {
                return;
            }

            var trimmedBase = identityBase.TrimEnd('/');
            var healthUrl = string.Concat(trimmedBase, "/health/live");
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(2)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            const int maxAttempts = 10;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var healthResp = await client
                        .GetAsync(healthUrl, cancellationToken)
                        .ConfigureAwait(false);
                    if (!healthResp.IsSuccessStatusCode)
                    {
                        logger.LogDebug(
                            "Identity health check attempt {Attempt} returned status {Status}",
                            attempt,
                            (int)healthResp.StatusCode
                        );
                        continue;
                    }

                    var configUrl = string.Concat(trimmedBase, "/.well-known/openid-configuration");
                    using var configResp = await client
                        .GetAsync(configUrl, cancellationToken)
                        .ConfigureAwait(false);
                    if (!configResp.IsSuccessStatusCode)
                    {
                        logger.LogDebug(
                            "Identity discovery attempt {Attempt} returned status {Status}",
                            attempt,
                            (int)configResp.StatusCode
                        );
                        continue;
                    }

                    await using var configStream = await configResp
                        .Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var configDoc = await JsonDocument
                        .ParseAsync(configStream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (
                        !configDoc.RootElement.TryGetProperty("jwks_uri", out var jwksProp)
                        || jwksProp.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(jwksProp.GetString())
                    )
                    {
                        logger.LogDebug(
                            "Identity discovery attempt {Attempt} missing jwks_uri",
                            attempt
                        );
                        continue;
                    }

                    var advertisedJwksUrl = jwksProp.GetString();
                    var upstreamJwksUrl = string.Concat(trimmedBase, "/.well-known/jwks");
                    if (
                        !string.IsNullOrWhiteSpace(advertisedJwksUrl)
                        && !string.Equals(
                            advertisedJwksUrl,
                            upstreamJwksUrl,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        logger.LogDebug(
                            "Identity advertised JWKS URL {Advertised} differs from upstream {Upstream}",
                            advertisedJwksUrl,
                            upstreamJwksUrl
                        );
                    }

                    using var jwksResp = await client
                        .GetAsync(upstreamJwksUrl, cancellationToken)
                        .ConfigureAwait(false);
                    if (!jwksResp.IsSuccessStatusCode)
                    {
                        logger.LogDebug(
                            "Identity JWKS attempt {Attempt} returned status {Status}",
                            attempt,
                            (int)jwksResp.StatusCode
                        );
                        continue;
                    }

                    await using var jwksStream = await jwksResp
                        .Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var jwksDoc = await JsonDocument
                        .ParseAsync(jwksStream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (
                        !jwksDoc.RootElement.TryGetProperty("keys", out var keysProp)
                        || keysProp.ValueKind != JsonValueKind.Array
                        || keysProp.GetArrayLength() == 0
                    )
                    {
                        logger.LogDebug(
                            "Identity JWKS attempt {Attempt} returned no signing keys",
                            attempt
                        );
                        continue;
                    }

                    Volatile.Write(ref ReadyFlag, 1);
                    logger.LogInformation(
                        "Identity readiness confirmed after {Attempt} attempt(s); JWKS keys={KeyCount}",
                        attempt,
                        keysProp.GetArrayLength()
                    );
                    return;
                }
                catch (HttpRequestException ex)
                    when (ex.InnerException is SocketException or IOException)
                {
                    logger.LogDebug(
                        ex,
                        "Identity readiness attempt {Attempt} failed to connect to {Url}",
                        attempt,
                        healthUrl
                    );
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug(
                        "Identity readiness attempt {Attempt} timed out for {Url}",
                        attempt,
                        healthUrl
                    );
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            logger.LogWarning(
                "Identity readiness check for {Url} timed out after {Attempts} attempts",
                healthUrl,
                maxAttempts
            );
        }
        finally
        {
            Lock.Release();
        }
    } // End of Method EnsureReadyAsync
} // End of Class IdentityReadiness

public sealed record LogOverrideRequest(string Category, LogLevel Level, int TtlSeconds);
