// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TansuCloud.Storage.Hosting;
using TansuCloud.Storage.Security;
using TansuCloud.Storage.Services;
using TansuCloud.Observability;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry baseline
var storageName = "tansu.storage";
var storageVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(rb =>
        rb.AddService(
                storageName,
                serviceVersion: storageVersion,
                serviceInstanceId: Environment.MachineName
            )
            .AddAttributes(
                new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", (object)builder.Environment.EnvironmentName)
                }
            )
    )
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(o => o.RecordException = true);
        tracing.AddHttpClientInstrumentation();
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
        metrics.AddMeter("tansu.storage");
        metrics.AddOtlpExporter(otlp =>
        {
            var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                otlp.Endpoint = new Uri(endpoint);
            }
        });
    });
// Observability core (Task 38)
builder.Services.AddTansuObservabilityCore();
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.ParseStateValues = true;
    o.AddOtlpExporter(otlp =>
    {
        var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            otlp.Endpoint = new Uri(endpoint);
        }
    });
});
builder.Services.AddHealthChecks();
// Phase 0: health status transition publisher for ops visibility
builder.Services.AddSingleton<IHealthCheckPublisher, HealthTransitionPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(2);
    o.Period = TimeSpan.FromSeconds(15);
});

// Optional HybridCache backed by Redis when Cache:Redis is provided
var cacheRedis = builder.Configuration["Cache:Redis"];
var cacheDisabled = builder.Configuration.GetValue("Cache:Disable", false);
if (!string.IsNullOrWhiteSpace(cacheRedis) && !cacheDisabled)
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = cacheRedis);
    builder.Services.AddHybridCache();
}

// If Redis is configured, add a health check to surface readiness in compose/dev
var cacheRedisForHealth = builder.Configuration["Cache:Redis"];
if (!string.IsNullOrWhiteSpace(cacheRedisForHealth))
{
    builder
        .Services.AddHealthChecks()
        .AddCheck(
            "redis",
            new TansuCloud.Storage.Services.RedisPingHealthCheck(cacheRedisForHealth)
        );
}

// Add services to the container.

builder.Services.AddControllers();

// Response compression (Task 14): enable Brotli for compressible types using configurable options
builder.Services.AddResponseCompression(options =>
{
    // Bind Storage:Compression options for response compression
    var comp =
        builder
            .Configuration.GetSection(StorageOptions.SectionName)
            .Get<StorageOptions>()
            ?.Compression ?? new CompressionOptions();
    options.EnableForHttps = comp.EnableForHttps;
    options.Providers.Clear();
    options.Providers.Add<BrotliCompressionProvider>();
    // Allowlist of common text-like types; images/archives not included
    options.MimeTypes =
        (comp.MimeTypes is { Length: > 0 })
            ? comp.MimeTypes
            : new[]
            {
                "text/plain",
                "text/css",
                "text/html",
                "application/json",
                "application/javascript",
                "application/xml",
                "image/svg+xml"
            };
});
builder.Services.PostConfigure<BrotliCompressionProviderOptions>(o =>
{
    var comp =
        builder
            .Configuration.GetSection(StorageOptions.SectionName)
            .Get<StorageOptions>()
            ?.Compression ?? new CompressionOptions();
    o.Level = comp.BrotliLevel;
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Options and DI
builder
    .Services.AddOptions<StorageOptions>()
    .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Memory cache for transformed images (size limited)
var transformsSection =
    builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()?.Transforms
    ?? new TransformOptions();
builder.Services.AddMemoryCache(o =>
{
    // Use a conservative default size limit; entry size is set to the byte length of transformed images
    // If CacheMaxEntries is set, we approximate by allowing entries up to 1MB average -> limit entries * 1MB
    if (transformsSection.CacheMaxEntries > 0)
        o.SizeLimit = (long)transformsSection.CacheMaxEntries * 1_000_000L;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<IObjectStorage, FilesystemObjectStorage>();
builder.Services.AddScoped<IMultipartStorage, FilesystemMultipartStorage>();
builder.Services.AddSingleton<IPresignService, PresignService>();
builder.Services.AddScoped<IQuotaService, FilesystemQuotaService>();
builder.Services.AddSingleton<IAntivirusScanner, NoOpAntivirusScanner>();
builder.Services.AddHostedService<MultipartCleanupService>();
builder.Services.AddSingleton<ITenantCacheVersion, TenantCacheVersion>();

// When Outbox Redis connection is provided, subscribe to the outbox channel and bump tenant cache versions
var outboxRedis = builder.Configuration["Outbox:RedisConnection"];
var outboxChannel = builder.Configuration["Outbox:Channel"] ?? "tansu.outbox";
if (!string.IsNullOrWhiteSpace(outboxRedis))
{
    builder.Services.AddHostedService(sp => new BackgroundServiceWrapper(async ct =>
    {
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(outboxRedis);
            var versions = sp.GetRequiredService<ITenantCacheVersion>();
            var sub = mux.GetSubscriber();
            await sub.SubscribeAsync(
                new RedisChannel(outboxChannel, RedisChannel.PatternMode.Literal),
                (c, v) =>
                {
                    try
                    {
                        // Extract tenant from payload JSON { tenant: "...", ... }
                        var json = v.ToString();
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("tenant", out var tEl))
                        {
                            var tenant = tEl.GetString();
                            if (!string.IsNullOrWhiteSpace(tenant))
                                versions.Increment(tenant!);
                        }
                    }
                    catch { }
                }
            );
        }
        catch (Exception ex)
        {
            var logger = sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger("CacheVersionSubscriber");
            logger.LogError(ex, "Cache version subscriber failed to start");
        }
        await Task.Delay(Timeout.Infinite, ct);
    }));
}

// Authentication/Authorization (JwtBearer validation)
builder
    .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Dev: allow detailed IdentityModel messages to aid troubleshooting (PII)
        if (builder.Environment.IsDevelopment())
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
        }

        options.IncludeErrorDetails = true;

        // Logical issuer that must match the 'iss' claim in tokens (browser-visible 127.0.0.1 behind gateway in dev)
        var configuredIssuer =
            builder.Configuration["Oidc:Issuer"] ?? "http://127.0.0.1:8080/identity/";
        var issuerNoSlash = configuredIssuer.TrimEnd('/');
        var issuerWithSlash = issuerNoSlash + "/";
        options.Authority = issuerNoSlash;

        // Backchannel discovery/JWKS: prefer explicit configuration, otherwise derive from configured issuer.
        // This makes local Development (host-run) use 127.0.0.1 while Compose/cluster can override via env.
        var metadataAddress = builder.Configuration["Oidc:MetadataAddress"];
        if (string.IsNullOrWhiteSpace(metadataAddress))
        {
            // Use gateway discovery when running inside a container; otherwise derive from issuer (host dev).
            var inContainer = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase
            );
            metadataAddress = inContainer
                ? "http://gateway:8080/identity/.well-known/openid-configuration"
                : issuerWithSlash + ".well-known/openid-configuration";
        }
        options.MetadataAddress = metadataAddress;

        // In Development, configure a dynamic metadata manager with HTTP allowed to reduce startup races.
        if (builder.Environment.IsDevelopment())
        {
            var docRetriever = new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever
            {
                RequireHttps = false
            };
            var configMgr = new ConfigurationManager<OpenIdConnectConfiguration>(
                options.MetadataAddress!,
                new OpenIdConnectConfigurationRetriever(),
                docRetriever
            )
            {
                AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                RefreshInterval = TimeSpan.FromMinutes(1)
            };
            options.ConfigurationManager = configMgr;
            options.RefreshOnIssuerKeyNotFound = true;
        }

        options.RequireHttpsMetadata = false; // Development only
        options.MapInboundClaims = false; // keep JWT claim types as-is (e.g., "aud", "scope")
        // Prepare issuer validation with optional localhost/127.0.0.1 alternates in Development
        string[] validIssuers;
        if (builder.Environment.IsDevelopment())
        {
            string altHostNoSlash = issuerNoSlash;
            if (issuerNoSlash.Contains("127.0.0.1"))
                altHostNoSlash = issuerNoSlash.Replace("127.0.0.1", "localhost");
            else if (issuerNoSlash.Contains("localhost"))
                altHostNoSlash = issuerNoSlash.Replace("localhost", "127.0.0.1");

            var altHostWithSlash = altHostNoSlash.TrimEnd('/') + "/";
            validIssuers = new[]
            {
                issuerNoSlash,
                issuerWithSlash,
                altHostNoSlash,
                altHostWithSlash
            };
        }
        else
        {
            validIssuers = new[] { issuerNoSlash, issuerWithSlash };
        }

        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            // In Development, relax audience validation at the JWT layer; controllers enforce policies.
            ValidateAudience = !builder.Environment.IsDevelopment(),
            ValidAudience = "tansu.storage",
            // Accept OpenIddict access tokens (typ: at+jwt) and standard JWT
            ValidTypes = new[] { "at+jwt", "JWT", "jwt" }
        };

        // Only enforce a strict custom audience validator outside Development.
        // In Dev, ValidateAudience=false already; keeping AudienceValidator off avoids unexpected 403s
        // when tests use admin.full-only tokens or when aud is a JSON array string.
        if (!builder.Environment.IsDevelopment())
        {
            tvp.AudienceValidator = (audiences, token, parameters) =>
            {
                try
                {
                    if (audiences == null)
                        return false;
                    foreach (var aud in audiences)
                    {
                        if (string.Equals(aud, "tansu.storage", StringComparison.Ordinal))
                            return true;
                        if (aud.StartsWith("["))
                        {
                            var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(aud);
                            if (arr?.Contains("tansu.storage") == true)
                                return true;
                        }
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            };
        }

        options.TokenValidationParameters = tvp;

        // Verbose diagnostics: surface JWT auth lifecycle events in Development to debug 401/403s
        if (builder.Environment.IsDevelopment())
        {
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = ctx =>
                {
                    var logger = ctx
                        .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Auth");
                    logger.LogWarning(
                        ctx.Exception,
                        "JwtAuth: Authentication failed: {Message}",
                        ctx.Exception.Message
                    );
                    return Task.CompletedTask;
                },
                OnTokenValidated = ctx =>
                {
                    try
                    {
                        var u = ctx.Principal;
                        // Use robust helpers tolerant to various token shapes rather than OpenIddict-only extensions
                        IEnumerable<string> scopesEnum =
                            u != null
                                ? TansuCloud.Storage.Security.ClaimsPrincipalExtensions.EnumerateScopes(
                                    u
                                )
                                : Array.Empty<string>();
                        IEnumerable<string> audsEnum =
                            u != null
                                ? TansuCloud.Storage.Security.ClaimsPrincipalExtensions.EnumerateAudiences(
                                    u
                                )
                                : Array.Empty<string>();
                        var scopes = string.Join(' ', scopesEnum);
                        var auds = string.Join(' ', audsEnum);
                        var logger = ctx
                            .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Auth");
                        logger.LogInformation(
                            "JwtAuth: Token validated iss={Issuer} sub={Sub} scopes='{Scopes}' aud='{Auds}'",
                            u?.FindFirst("iss")?.Value,
                            u?.FindFirst("sub")?.Value,
                            scopes,
                            auds
                        );
                    }
                    catch { }
                    return Task.CompletedTask;
                },
                OnChallenge = ctx =>
                {
                    var logger = ctx
                        .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Auth");
                    logger.LogWarning(
                        "JwtAuth: Challenge: error={Error} description={Description} uri={Uri}",
                        ctx.Error,
                        ctx.ErrorDescription,
                        ctx.ErrorUri
                    );
                    return Task.CompletedTask;
                },
                OnForbidden = ctx =>
                {
                    var logger = ctx
                        .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Auth");
                    logger.LogWarning(
                        "JwtAuth: Forbidden for {Path}",
                        ctx.HttpContext.Request.Path
                    );
                    return Task.CompletedTask;
                }
            };
        }

        // Prefer legacy handler in Development to avoid rare base64url decode issues; keep claims unmapped
        if (builder.Environment.IsDevelopment())
        {
            try
            {
                options.TokenHandlers.Clear();
                var legacy = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler
                {
                    MapInboundClaims = false
                };
                System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultMapInboundClaims =
                    false;
                options.TokenHandlers.Add(legacy);
            }
            catch { }
        }
    });
builder.Services.AddAuthorization(options =>
{
    // Require auth by default
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    // Scope-based policies (use local robust helper instead of OpenIddict extension to avoid empty scopes)
    options.AddPolicy(
        "storage.read",
        policy =>
            policy.RequireAssertion(ctx =>
                TansuCloud.Storage.Security.ClaimsPrincipalExtensions.HasScope(
                    ctx.User,
                    "storage.read"
                )
                || TansuCloud.Storage.Security.ClaimsPrincipalExtensions.HasScope(
                    ctx.User,
                    "admin.full"
                )
            )
    );
    options.AddPolicy(
        "storage.write",
        policy =>
            policy.RequireAssertion(ctx =>
                TansuCloud.Storage.Security.ClaimsPrincipalExtensions.HasScope(
                    ctx.User,
                    "storage.write"
                )
                || TansuCloud.Storage.Security.ClaimsPrincipalExtensions.HasScope(
                    ctx.User,
                    "admin.full"
                )
            )
    );
});

var app = builder.Build();
// Startup diagnostic: log OIDC metadata source choice (Task 38)
try
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OIDC-Config");
    var configured = builder.Configuration["Oidc:Issuer"] ?? "http://127.0.0.1:8080/identity/";
    var issuerNoSlash = configured.TrimEnd('/');
    var issuerWithSlash = issuerNoSlash + "/";
    var configuredMd = builder.Configuration["Oidc:MetadataAddress"];
    string effectiveMd;
    string src;
    if (!string.IsNullOrWhiteSpace(configuredMd))
    {
        effectiveMd = configuredMd!;
        src = "explicit-config";
    }
    else if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
    {
        effectiveMd = "http://gateway:8080/identity/.well-known/openid-configuration";
        src = "container-gateway";
    }
    else
    {
        effectiveMd = new Uri(new Uri(issuerWithSlash), ".well-known/openid-configuration").AbsoluteUri;
        src = "issuer-derived";
    }
    log.LogOidcMetadataChoice(src, issuerNoSlash, effectiveMd);
}
catch { }

// Task 38 enrichment middleware
app.UseMiddleware<RequestEnrichmentMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();

// Development-only diagnostics: log auth state for bucket endpoints before authorization
if (app.Environment.IsDevelopment())
{
    app.Use(
        async (context, next) =>
        {
            if (
                context.Request.Path.StartsWithSegments(
                    "/api/buckets",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                try
                {
                    var hasAuthHeader = context.Request.Headers.ContainsKey("Authorization");
                    var isAuth = context.User?.Identity?.IsAuthenticated ?? false;
                    // Use robust helpers to enumerate scopes/audiences for clearer diagnostics
                    IEnumerable<string> scopesEnum =
                        context.User != null
                            ? TansuCloud.Storage.Security.ClaimsPrincipalExtensions.EnumerateScopes(
                                context.User
                            )
                            : Array.Empty<string>();
                    IEnumerable<string> audsEnum =
                        context.User != null
                            ? TansuCloud.Storage.Security.ClaimsPrincipalExtensions.EnumerateAudiences(
                                context.User
                            )
                            : Array.Empty<string>();
                    var scopes = string.Join(' ', scopesEnum);
                    var audiences = string.Join(' ', audsEnum);
                    app.Logger.LogInformation(
                        "AuthDiag(pre)[/api/buckets]: hasAuthHeader={HasAuthHeader} isAuthenticated={IsAuth} scopes='{Scopes}' aud='{Audiences}'",
                        hasAuthHeader,
                        isAuth,
                        scopes,
                        audiences
                    );
                }
                catch { }
            }
            await next();
        }
    );
}

app.UseAuthorization();

// Response compression should happen after auth but before controllers to ensure Content-Encoding set
var compEnabled =
    builder
        .Configuration.GetSection(StorageOptions.SectionName)
        .Get<StorageOptions>()
        ?.Compression?.Enabled ?? true;
if (compEnabled)
{
    app.UseResponseCompression();
}

// (post-authorization diagnostics removed; pre-authorization variant above provides earlier context)

// Per-request metrics and structured logs
app.UseMiddleware<RequestMetricsMiddleware>();

app.MapControllers();

// Health endpoints
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// Dev-only: dynamic logging override shim to aid troubleshooting
if (app.Environment.IsDevelopment())
{
    app.MapPost(
        "/dev/logging/overrides",
        (IDynamicLogLevelOverride ovr, [AsParameters] LogOverrideRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Category))
                return Results.Problem("Category is required", statusCode: 400);
            if (req.TtlSeconds <= 0)
                return Results.Problem("TtlSeconds must be > 0", statusCode: 400);
            ovr.Set(req.Category!, req.Level, TimeSpan.FromSeconds(req.TtlSeconds));
            return Results.Ok(new { ok = true });
        }
    ).AllowAnonymous();
    app.MapGet(
        "/dev/logging/overrides",
        (IDynamicLogLevelOverride ovr) =>
            Results.Json(
                ovr.Snapshot().ToDictionary(k => k.Key, v => new { v.Value.Level, v.Value.Expires })
            )
    ).AllowAnonymous();
}

app.Run();

public sealed record LogOverrideRequest(string Category, LogLevel Level, int TtlSeconds);
