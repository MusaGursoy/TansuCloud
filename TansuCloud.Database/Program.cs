// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TansuCloud.Database.Caching;
using TansuCloud.Database.Hosting;
using TansuCloud.Database.Outbox;
using TansuCloud.Database.Provisioning;
using TansuCloud.Database.Security;
using TansuCloud.Observability;
using TansuCloud.Observability.Auditing;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry baseline
var dbName = "tansu.db";
var dbVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(rb =>
        rb.AddService(dbName, serviceVersion: dbVersion, serviceInstanceId: Environment.MachineName)
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
    // Export custom Outbox meter so SigNoz records outbox_* counters via OTLP
        metrics.AddMeter("TansuCloud.Database.Outbox");
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

// Observability core (Task 38)
builder.Services.AddTansuObservabilityCore();

// Audit SDK (Task 31 Phase 1)
builder.Services.AddTansuAudit(builder.Configuration);
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
builder
    .Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self" });

// Audit retention options and background job (Phase 3)
builder
    .Services.AddOptions<TansuCloud.Database.Services.AuditRetentionOptions>()
    .Bind(builder.Configuration.GetSection("AuditRetention"));
builder.Services.AddHostedService<TansuCloud.Database.Services.AuditRetentionWorker>();
builder.Services.AddSingleton<
    TansuCloud.Database.Services.IAuditDbConnectionFactory,
    TansuCloud.Database.Services.NpgsqlAuditDbConnectionFactory
>();

// Phase 0: health transition publisher for Info logs on state changes
builder.Services.AddSingleton<IHealthCheckPublisher, HealthTransitionPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(2);
    o.Period = TimeSpan.FromSeconds(15);
});

// Observability shared primitives (Task 38)
builder.Services.AddTansuObservabilityCore();

// If Redis is configured, add a health check to surface readiness in compose/dev
var cacheRedisForHealth = builder.Configuration["Cache:Redis"];
if (!string.IsNullOrWhiteSpace(cacheRedisForHealth))
{
    builder
        .Services.AddHealthChecks()
        .AddCheck(
            "redis",
            new TansuCloud.Database.Services.RedisPingHealthCheck(cacheRedisForHealth),
            tags: new[] { "ready", "redis" }
        );
}

// Add services to the container.

builder.Services.AddControllers();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Options binding for provisioning and DI registrations
builder
    .Services.AddOptions<ProvisioningOptions>()
    .Bind(builder.Configuration.GetSection("Provisioning"))
    .ValidateDataAnnotations();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITenantProvisioner, TenantProvisioner>();
builder.Services.AddScoped<
    TansuCloud.Database.Services.ITenantDbContextFactory,
    TansuCloud.Database.Services.TenantDbContextFactory
>();
builder.Services.AddSingleton<
    TansuCloud.Database.Outbox.IOutboxProducer,
    TansuCloud.Database.Outbox.OutboxProducer
>();
builder.Services.AddSingleton<
    TansuCloud.Database.Services.IAuditQueryService,
    TansuCloud.Database.Services.AuditQueryService
>();

// Optional HybridCache backed by Redis when Cache:Redis is set
var cacheRedis = builder.Configuration["Cache:Redis"];
var cacheDisabled = builder.Configuration.GetValue("Cache:Disable", false);
if (!string.IsNullOrWhiteSpace(cacheRedis) && !cacheDisabled)
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = cacheRedis);
    builder.Services.AddHybridCache();
}

// Tenant cache versions for invalidation
builder.Services.AddSingleton<ITenantCacheVersion, TenantCacheVersion>();

// When Outbox Redis connection is provided, also subscribe to outbox channel and bump tenant cache versions
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

// see BackgroundServiceWrapper in Hosting/BackgroundServiceWrapper.cs

// Outbox options and conditional background dispatcher
builder
    .Services.AddOptions<OutboxOptions>()
    .Bind(builder.Configuration.GetSection("Outbox"))
    .ValidateDataAnnotations();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Outbox:RedisConnection"]))
{
    builder.Services.AddHostedService<OutboxDispatcher>();
    builder.Services.AddSingleton<IOutboxPublisher>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<OutboxOptions>>().Value;
        if (string.IsNullOrWhiteSpace(opts.RedisConnection))
        {
            return new NoopOutboxPublisher();
        }
        var mux = ConnectionMultiplexer.Connect(opts.RedisConnection);
        return new RedisOutboxPublisher(mux);
    });
}

// NoopOutboxPublisher moved to Outbox/NoopOutboxPublisher.cs (cannot declare types after top-level statements)

// Authentication/Authorization (JWT Bearer validation via Identity issuer)
builder
    .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Dev: allow detailed IdentityModel messages to aid troubleshooting (PII)
        if (builder.Environment.IsDevelopment())
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
        }
        // Include detailed error descriptions in WWW-Authenticate for easier diagnostics (Dev only)
        options.IncludeErrorDetails = true;
        // Accept both issuer forms (with and without trailing slash)
        var configured = builder.Configuration["Oidc:Issuer"] ?? "http://127.0.0.1:8080/identity/";
        var issuerNoSlash = configured.TrimEnd('/');
        var issuerWithSlash = issuerNoSlash + "/";

        options.Authority = issuerNoSlash; // logical issuer (matches token 'iss')
        // Backchannel discovery/JWKS: prefer explicit configuration, otherwise derive from configured issuer.
        // When running in a container, default to gateway for discovery to avoid localhost loopback issues.
        var metadataAddress = builder.Configuration["Oidc:MetadataAddress"];
        if (string.IsNullOrWhiteSpace(metadataAddress))
        {
            var issuerUri = new Uri(issuerWithSlash);
            var inContainer = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase
            );
            if (inContainer)
            {
                metadataAddress = "http://gateway:8080/identity/.well-known/openid-configuration";
            }
            else
            {
                metadataAddress = new Uri(
                    issuerUri,
                    ".well-known/openid-configuration"
                ).AbsoluteUri;
            }
        }
        options.MetadataAddress = metadataAddress;

        // Defer logging until app is built to avoid BuildServiceProvider duplication

        // In Development, configure a dynamic metadata manager with aggressive refresh to reduce startup races.
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
                // Note: AutomaticRefreshInterval must be >= 5 minutes per IdentityModel.
                // Keep it at 5 minutes in dev and reduce RefreshInterval for faster proactive checks.
                AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                RefreshInterval = TimeSpan.FromMinutes(1)
            };
            options.ConfigurationManager = configMgr;
            // If a signature key is initially missing, immediately refresh metadata/JWKS and retry once.
            options.RefreshOnIssuerKeyNotFound = true;
        }
        options.RequireHttpsMetadata = false; // Development only
        options.MapInboundClaims = false; // keep JWT claim types as-is (e.g., "aud", "scope")
        options.IncludeErrorDetails = true;
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

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            // In Development, relax audience validation at the JWT layer and rely on our explicit controller checks.
            // This helps avoid edge cases where 'aud' is emitted as a JSON array/string by different token handlers.
            ValidateAudience = !builder.Environment.IsDevelopment(),
            ValidAudience = "tansu.db",
            // Accept OpenIddict access tokens (typ: at+jwt) and standard JWT
            ValidTypes = new[] { "at+jwt", "JWT", "jwt" },
            // Extra-resilient audience validator if validation is enabled (e.g., in non-dev environments).
            AudienceValidator = (audiences, token, parameters) =>
            {
                try
                {
                    if (audiences == null)
                        return false;
                    foreach (var aud in audiences)
                    {
                        if (string.Equals(aud, "tansu.db", StringComparison.Ordinal))
                            return true;
                        if (aud.StartsWith("["))
                        {
                            var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(aud);
                            if (arr?.Contains("tansu.db") == true)
                                return true;
                        }
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }
        };
        // In Development, prefer legacy JwtSecurityTokenHandler to avoid rare base64url decode issues
        // Ensure claims are NOT remapped (keep 'aud' and 'scope' literal) so our policies work.
        if (builder.Environment.IsDevelopment())
        {
            try
            {
                options.TokenHandlers.Clear();
                // Disable inbound claims mapping both on the handler instance and globally for safety
                var legacy = new JwtSecurityTokenHandler { MapInboundClaims = false };
                JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
                options.TokenHandlers.Add(legacy);
            }
            catch { }
        }
        // Use default validators to maximize compatibility outside Development.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Auth.JwtBearer");
                var hasAuth = ctx.Request.Headers.ContainsKey("Authorization");
                if (hasAuth)
                {
                    // Capture the raw Authorization header(s) for diagnostics
                    var authVals = ctx.Request.Headers["Authorization"].ToArray();
                    var rawCombined = string.Join(",", authVals);
                    string rawPrefix =
                        rawCombined.Length > 64 ? rawCombined.Substring(0, 64) : rawCombined;
                    ctx.HttpContext.Items["authPrefix"] = rawPrefix;

                    // Robust extraction: find the first segment that looks like a JWT (three base64url parts)
                    static bool LooksLikeJwt(string s) =>
                        !string.IsNullOrWhiteSpace(s)
                        && System.Text.RegularExpressions.Regex.IsMatch(
                            s.Trim(),
                            "^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$"
                        );

                    string? candidate = null;
                    foreach (var valRaw in authVals)
                    {
                        var val = valRaw ?? string.Empty;
                        // Split on spaces and commas to handle repeated prefixes or multiple values
                        var parts = val.Split(
                            new[] { ' ', ',' },
                            StringSplitOptions.RemoveEmptyEntries
                        );
                        foreach (var p in parts)
                        {
                            if (LooksLikeJwt(p))
                            {
                                candidate = p;
                                break;
                            }
                        }
                        if (candidate is not null)
                            break;
                        // Fallback: trim a single Bearer prefix
                        if (val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            var t = val.Substring(7).Trim();
                            if (LooksLikeJwt(t))
                            {
                                candidate = t;
                                break;
                            }
                        }
                    }

                    if (candidate is not null)
                    {
                        // Normalize: remove surrounding quotes/whitespace just in case
                        candidate = candidate.Trim();
                        if (candidate.Length > 1 && candidate[0] == '"' && candidate[^1] == '"')
                        {
                            candidate = candidate.Substring(1, candidate.Length - 2);
                        }
                        ctx.Token = candidate;
                        // Normalize Authorization header to a single canonical value to avoid comma/dup issues
                        try
                        {
                            ctx.Request.Headers["Authorization"] = $"Bearer {candidate}";
                        }
                        catch { }
                        // Stash candidate preview for later diagnostics
                        try
                        {
                            ctx.HttpContext.Items["jwtCandidate"] = candidate;
                        }
                        catch { }
                        try
                        {
                            var segParts = candidate.Split('.');
                            var segs = segParts.Length;
                            var left =
                                candidate.Length >= 16 ? candidate.Substring(0, 16) : candidate;
                            logger.LogDebug(
                                "[JWT] Extracted bearer token segs={Segs} head={Head}...",
                                segs,
                                left
                            );
                            // Try to decode header to validate base64url input
                            try
                            {
                                var headerSeg = segParts[0];
                                ctx.HttpContext.Items["jwtSegs"] = segs;
                                ctx.HttpContext.Items["jwtSeg0Len"] = headerSeg?.Length ?? 0;
                                string DecodeB64Url(string s)
                                {
                                    var p = s.Replace('-', '+').Replace('_', '/');
                                    switch (p.Length % 4)
                                    {
                                        case 2:
                                            p += "==";
                                            break;
                                        case 3:
                                            p += "=";
                                            break;
                                    }
                                    return System.Text.Encoding.UTF8.GetString(
                                        Convert.FromBase64String(p)
                                    );
                                }
                                var hdrJson = DecodeB64Url(headerSeg ?? string.Empty);
                                ctx.HttpContext.Items["jwtHdrOk"] = true;
                                ctx.HttpContext.Items["jwtHdr"] = hdrJson;
                                ctx.HttpContext.Items["jwtHdrB64"] = headerSeg;
                            }
                            catch (Exception ex)
                            {
                                ctx.HttpContext.Items["jwtHdrOk"] = false;
                                ctx.HttpContext.Items["jwtHdrErr"] =
                                    ex.GetType().Name + ": " + ex.Message;
                            }
                        }
                        catch { }
                    }

                    logger.LogDebug(
                        "[JWT] Authorization header received for {Path} rawPrefix='{RawPrefix}'",
                        ctx.Request.Path,
                        rawPrefix
                    );
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Auth.JwtBearer");
                // Surface the captured auth prefix to help diagnose malformed Authorization headers (Dev only)
                try
                {
                    var env =
                        ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    if (
                        env.IsDevelopment()
                        && ctx.HttpContext.Items.TryGetValue("authPrefix", out var raw)
                        && raw is string rp
                        && !string.IsNullOrEmpty(rp)
                    )
                    {
                        ctx.Response.Headers["X-Auth-Prefix"] = rp.Replace("\r", " ")
                            .Replace("\n", " ");
                    }
                }
                catch { }
                // Surface header decode diagnostics
                try
                {
                    var env =
                        ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    if (env.IsDevelopment())
                    {
                        // Token preview vs candidate
                        try
                        {
                            if (
                                ctx.HttpContext.Items.TryGetValue("jwtCandidate", out var cand)
                                && cand is string ctoken
                                && !string.IsNullOrEmpty(ctoken)
                            )
                            {
                                var thead = ctoken.Length >= 24 ? ctoken.Substring(0, 24) : ctoken;
                                ctx.Response.Headers["X-Jwt-TokenHead"] = thead;
                                ctx.Response.Headers["X-Jwt-TokenEqCandidate"] = "true";
                            }
                        }
                        catch { }
                        // Exception details (dev only)
                        if (ctx.Exception is not null)
                        {
                            var t = ctx.Exception.GetType().Name;
                            var m = ctx.Exception.Message ?? string.Empty;
                            if (m.Length > 256)
                                m = m.Substring(0, 256);
                            ctx.Response.Headers["X-Jwt-ErrType"] = t;
                            ctx.Response.Headers["X-Jwt-Err"] = m.Replace("\r", " ")
                                .Replace("\n", " ");
                        }
                        // Issuer/metadata context
                        try
                        {
                            ctx.Response.Headers["X-Jwt-Issuer"] = issuerWithSlash;
                            if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                            {
                                ctx.Response.Headers["X-Jwt-Metadata"] = options.MetadataAddress!;
                            }
                            try
                            {
                                if (
                                    ctx.HttpContext.Items.TryGetValue("jwtCandidate", out var cand)
                                    && cand is string ctoken
                                    && !string.IsNullOrEmpty(ctoken)
                                )
                                {
                                    var thead =
                                        ctoken.Length >= 24 ? ctoken.Substring(0, 24) : ctoken;
                                    ctx.Response.Headers["X-Jwt-TokenHead"] = thead;
                                    ctx.Response.Headers["X-Jwt-TokenEqCandidate"] = "true";
                                }
                            }
                            catch { }
                        }
                        catch { }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdrOk", out var okObj)
                            && okObj is bool ok
                        )
                        {
                            ctx.Response.Headers["X-Jwt-HdrOk"] = ok ? "true" : "false";
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdr", out var hdr)
                            && hdr is string h
                            && !string.IsNullOrEmpty(h)
                        )
                        {
                            ctx.Response.Headers["X-Jwt-Hdr"] =
                                h.Length > 256 ? h.Substring(0, 256) : h;
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdrB64", out var hdrB64)
                            && hdrB64 is string hb
                            && !string.IsNullOrEmpty(hb)
                        )
                        {
                            ctx.Response.Headers["X-Jwt-HdrB64"] =
                                hb.Length > 128 ? hb.Substring(0, 128) : hb;
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtSegs", out var sCount)
                            && sCount is int sc
                        )
                        {
                            ctx.Response.Headers["X-Jwt-Segs"] = sc.ToString();
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtSeg0Len", out var s0)
                            && s0 is int s0i
                        )
                        {
                            ctx.Response.Headers["X-Jwt-Seg0-Len"] = s0i.ToString();
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdrErr", out var err)
                            && err is string e
                            && !string.IsNullOrEmpty(e)
                        )
                        {
                            ctx.Response.Headers["X-Jwt-HdrErr"] = e.Replace("\r", " ")
                                .Replace("\n", " ");
                        }
                        // Candidate token preview
                        try
                        {
                            if (
                                ctx.HttpContext.Items.TryGetValue("jwtCandidate", out var cand)
                                && cand is string ctoken
                                && !string.IsNullOrEmpty(ctoken)
                            )
                            {
                                var clen = ctoken.Length.ToString();
                                var chead = ctoken.Length >= 24 ? ctoken.Substring(0, 24) : ctoken;
                                ctx.Response.Headers["X-Jwt-CandidateLen"] = clen;
                                ctx.Response.Headers["X-Jwt-CandidateHead"] = chead;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                logger.LogError(
                    ctx.Exception,
                    "[JWT] Authentication failed. Issuer={Issuer} Metadata={Metadata} Exception={Exception}",
                    issuerWithSlash,
                    options.MetadataAddress,
                    (ctx.Exception?.ToString()) ?? string.Empty
                );
                return Task.CompletedTask;
            },
            OnTokenValidated = ctx =>
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Auth.JwtBearer");
                var sub = ctx.Principal?.FindFirst("sub")?.Value;
                var auds = string.Join(
                    ',',
                    ctx.Principal?.FindAll("aud").Select(c => c.Value) ?? Array.Empty<string>()
                );
                var scopes = string.Join(
                    ' ',
                    ctx.Principal?.FindAll("scope").Select(c => c.Value) ?? Array.Empty<string>()
                );
                logger.LogInformation(
                    "[JWT] Token validated. sub={Sub} aud={Audiences} scopes={Scopes}",
                    sub,
                    auds,
                    scopes
                );
                return Task.CompletedTask;
            },
            OnChallenge = ctx =>
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Auth.JwtBearer");
                // Also attach the auth prefix on challenge to help clients debug
                try
                {
                    var env =
                        ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    if (
                        env.IsDevelopment()
                        && ctx.HttpContext.Items.TryGetValue("authPrefix", out var raw)
                        && raw is string rp
                        && !string.IsNullOrEmpty(rp)
                    )
                    {
                        ctx.Response.Headers["X-Auth-Prefix"] = rp.Replace("\r", " ")
                            .Replace("\n", " ");
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdrOk", out var okObj)
                            && okObj is bool ok
                        )
                        {
                            ctx.Response.Headers["X-Jwt-HdrOk"] = ok ? "true" : "false";
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdr", out var hdr)
                            && hdr is string h
                            && !string.IsNullOrEmpty(h)
                        )
                        {
                            ctx.Response.Headers["X-Jwt-Hdr"] =
                                h.Length > 256 ? h.Substring(0, 256) : h;
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdrB64", out var hdrB64)
                            && hdrB64 is string hb
                            && !string.IsNullOrEmpty(hb)
                        )
                        {
                            ctx.Response.Headers["X-Jwt-HdrB64"] =
                                hb.Length > 128 ? hb.Substring(0, 128) : hb;
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtSegs", out var sCount)
                            && sCount is int sc
                        )
                        {
                            ctx.Response.Headers["X-Jwt-Segs"] = sc.ToString();
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtSeg0Len", out var s0)
                            && s0 is int s0i
                        )
                        {
                            ctx.Response.Headers["X-Jwt-Seg0-Len"] = s0i.ToString();
                        }
                        if (
                            ctx.HttpContext.Items.TryGetValue("jwtHdrErr", out var err)
                            && err is string e
                            && !string.IsNullOrEmpty(e)
                        )
                        {
                            ctx.Response.Headers["X-Jwt-HdrErr"] = e.Replace("\r", " ")
                                .Replace("\n", " ");
                        }
                        // Candidate token preview
                        try
                        {
                            if (
                                ctx.HttpContext.Items.TryGetValue("jwtCandidate", out var cand)
                                && cand is string ctoken
                                && !string.IsNullOrEmpty(ctoken)
                            )
                            {
                                var clen = ctoken.Length.ToString();
                                var chead = ctoken.Length >= 24 ? ctoken.Substring(0, 24) : ctoken;
                                ctx.Response.Headers["X-Jwt-CandidateLen"] = clen;
                                ctx.Response.Headers["X-Jwt-CandidateHead"] = chead;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                logger.LogWarning(
                    "[JWT] Challenge. error={Error} desc={Description} uri={Uri}",
                    ctx.Error,
                    ctx.ErrorDescription,
                    ctx.ErrorUri
                );
                try
                {
                    var env =
                        ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    if (env.IsDevelopment() && ctx.AuthenticateFailure is not null)
                    {
                        var t = ctx.AuthenticateFailure.GetType().Name;
                        var m = ctx.AuthenticateFailure.Message ?? string.Empty;
                        if (m.Length > 256)
                            m = m.Substring(0, 256);
                        ctx.Response.Headers["X-Jwt-ErrType"] = t;
                        ctx.Response.Headers["X-Jwt-Err"] = m.Replace("\r", " ").Replace("\n", " ");
                        try
                        {
                            ctx.Response.Headers["X-Jwt-Issuer"] = issuerWithSlash;
                            if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                            {
                                ctx.Response.Headers["X-Jwt-Metadata"] = options.MetadataAddress!;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Require auth by default
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    // Scope-based policies
    if (builder.Environment.IsDevelopment())
    {
        // In Development, relax audience checks to reduce friction during local runs/E2E.
        // Treat admin.full as a superset for read/write to simplify test tokens.
        options.AddPolicy(
            "db.read",
            policy =>
                policy.RequireAssertion(ctx =>
                    ClaimsPrincipalExtensions.HasScope(ctx.User, "db.read")
                    || ClaimsPrincipalExtensions.HasScope(ctx.User, "admin.full")
                )
        );
        options.AddPolicy(
            "db.write",
            policy =>
                policy.RequireAssertion(ctx =>
                    ClaimsPrincipalExtensions.HasScope(ctx.User, "db.write")
                    || ClaimsPrincipalExtensions.HasScope(ctx.User, "admin.full")
                )
        );
        options.AddPolicy(
            "db.provision",
            policy =>
                policy.RequireAssertion(ctx =>
                    ClaimsPrincipalExtensions.HasScope(ctx.User, "admin.full")
                    || ClaimsPrincipalExtensions.HasScope(ctx.User, "db.write")
                )
        );
    }
    else
    {
        // In non-Development, enforce both scope and explicit audience checks.
        options.AddPolicy(
            "db.read",
            policy =>
                policy.RequireAssertion(ctx =>
                    ClaimsPrincipalExtensions.HasScope(ctx.User, "db.read")
                    && ClaimsPrincipalExtensions.HasAudience(ctx.User, "tansu.db")
                )
        );
        options.AddPolicy(
            "db.write",
            policy =>
                policy.RequireAssertion(ctx =>
                    ClaimsPrincipalExtensions.HasScope(ctx.User, "db.write")
                    && ClaimsPrincipalExtensions.HasAudience(ctx.User, "tansu.db")
                )
        );
        // Provision requires elevated admin or db.write. Adjust later for fine-grained admin scope.
        options.AddPolicy(
            "db.provision",
            policy =>
                policy.RequireAssertion(ctx =>
                    (
                        ClaimsPrincipalExtensions.HasScope(ctx.User, "admin.full")
                        || ClaimsPrincipalExtensions.HasScope(ctx.User, "db.write")
                    ) && ClaimsPrincipalExtensions.HasAudience(ctx.User, "tansu.db")
                )
        );
    }
});

// No OpenIddict validation here; JwtBearer handles access token validation against the issuer's JWKS

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
    else if (
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase
        )
    )
    {
        effectiveMd = "http://gateway:8080/identity/.well-known/openid-configuration";
        src = "container-gateway";
    }
    else
    {
        effectiveMd = new Uri(
            new Uri(issuerWithSlash),
            ".well-known/openid-configuration"
        ).AbsoluteUri;
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

// Global exception handler to surface ProblemDetails on unhandled errors (helps E2E diagnose 500s)
app.Use(
    async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            var logger = ctx
                .RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GlobalException");
            logger.LogError(
                ex,
                "Unhandled exception processing {Method} {Path}",
                ctx.Request?.Method,
                ctx.Request?.Path.Value
            );
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/problem+json";
            var problem = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                title = "An unexpected error occurred.",
                status = 500,
                traceId = ctx.TraceIdentifier
            };
            try
            {
                await ctx.Response.WriteAsJsonAsync(problem);
            }
            catch
            { /* ignore */
            }
        }
    }
);

app.UseAuthentication();
app.UseAuthorization();

// Attach enrichment early
app.UseMiddleware<RequestEnrichmentMiddleware>();

app.MapControllers();

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

// Dev-only: dynamic logging override shim to aid troubleshooting (audited)
if (app.Environment.IsDevelopment())
{
    app.MapPost(
            "/dev/logging/overrides",
            (
                IDynamicLogLevelOverride ovr,
                [AsParameters] LogOverrideRequest req,
                TansuCloud.Observability.Auditing.IAuditLogger audit,
                HttpContext http
            ) =>
            {
                if (string.IsNullOrWhiteSpace(req.Category))
                {
                    // Emit failure audit (validation)
                    var evFail = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "LogLevelOverride",
                        Subject = http.User?.Identity?.Name ?? "system",
                        Outcome = "Failure",
                        ReasonCode = "ValidationError",
                        RouteTemplate = "/dev/logging/overrides",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        evFail,
                        req,
                        new[]
                        {
                            nameof(LogOverrideRequest.Category),
                            nameof(LogOverrideRequest.Level),
                            nameof(LogOverrideRequest.TtlSeconds)
                        }
                    );
                    return Results.Problem("Category is required", statusCode: 400);
                }
                if (req.TtlSeconds <= 0)
                {
                    var evFail = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "LogLevelOverride",
                        Subject = http.User?.Identity?.Name ?? "system",
                        Outcome = "Failure",
                        ReasonCode = "ValidationError",
                        RouteTemplate = "/dev/logging/overrides",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        evFail,
                        req,
                        new[]
                        {
                            nameof(LogOverrideRequest.Category),
                            nameof(LogOverrideRequest.Level),
                            nameof(LogOverrideRequest.TtlSeconds)
                        }
                    );
                    return Results.Problem("TtlSeconds must be > 0", statusCode: 400);
                }

                ovr.Set(req.Category!, req.Level, TimeSpan.FromSeconds(req.TtlSeconds));

                // Emit success audit
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "LogLevelOverride",
                    Subject = http.User?.Identity?.Name ?? "system",
                    Outcome = "Success",
                    RouteTemplate = "/dev/logging/overrides",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    req,
                    new[]
                    {
                        nameof(LogOverrideRequest.Category),
                        nameof(LogOverrideRequest.Level),
                        nameof(LogOverrideRequest.TtlSeconds)
                    }
                );

                return Results.Ok(new { ok = true });
            }
        )
        .AllowAnonymous();

    app.MapGet(
            "/dev/logging/overrides",
            (
                IDynamicLogLevelOverride ovr,
                TansuCloud.Observability.Auditing.IAuditLogger audit,
                HttpContext http
            ) =>
            {
                var snapshot = ovr.Snapshot();
                // Emit read audit with minimal details (count only)
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "LogLevelOverridesRead",
                    Subject = http.User?.Identity?.Name ?? "system",
                    Outcome = "Success",
                    RouteTemplate = "/dev/logging/overrides",
                    CorrelationId = http.TraceIdentifier
                };
                var details = new { count = snapshot.Count };
                audit.TryEnqueueRedacted(ev, details, new[] { "count" });

                return Results.Json(
                    snapshot.ToDictionary(k => k.Key, v => new { v.Value.Level, v.Value.Expires })
                );
            }
        )
        .AllowAnonymous();
}

app.Run();

public sealed record LogOverrideRequest(string Category, LogLevel Level, int TtlSeconds);
