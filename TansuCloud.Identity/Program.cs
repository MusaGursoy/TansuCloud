// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Infrastructure;
using TansuCloud.Identity.Infrastructure.External;
using TansuCloud.Identity.Infrastructure.Keys;
using TansuCloud.Identity.Infrastructure.Options;
using TansuCloud.Identity.Infrastructure.Security;
using TansuCloud.Observability;
using TansuCloud.Observability.Auditing;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry baseline: traces, metrics, logs
var svcName = "tansu.identity";
var svcVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(rb =>
        rb.AddService(
                svcName,
                serviceVersion: svcVersion,
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
    // Self liveness check
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self" });

// Phase 0: health transition publisher for Info logs on status change
builder.Services.AddSingleton<IHealthCheckPublisher, HealthTransitionPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(2);
    o.Period = TimeSpan.FromSeconds(15);
});

// Database: Prefer PostgreSQL; allow Sqlite fallback for tests
var useSqlite = builder.Configuration.GetValue(
    "UseSqlite",
    builder.Environment.IsEnvironment("E2E")
);
if (useSqlite)
{
    var sqliteConn = "Data Source=identity-e2e.db";
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseSqlite(sqliteConn);
        options.UseOpenIddict();
    });
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        // Prefer PgCat if running in compose with defaults
        var user = builder.Configuration["POSTGRES_USER"] ?? "postgres";
        var pass = builder.Configuration["POSTGRES_PASSWORD"] ?? "postgres";
        connectionString =
            $"Host=pgcat;Port=6432;Database=tansu_identity;Username={user};Password={pass}";
    }

    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseNpgsql(connectionString);
        options.UseOpenIddict();
    });
}

// Options and services
builder.Services.Configure<IdentityPolicyOptions>(
    builder.Configuration.GetSection(IdentityPolicyOptions.SectionName)
);

// HybridCache + Redis (optional)
var redisConn = builder.Configuration["Cache:Redis"];
var cacheDisabled = builder.Configuration.GetValue("Cache:Disable", false);
if (!string.IsNullOrWhiteSpace(redisConn) && !cacheDisabled)
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConn);
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
            new TansuCloud.Identity.Infrastructure.RedisPingHealthCheck(cacheRedisForHealth),
            tags: new[] { "ready", "redis" }
        );
}
builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
builder.Services.AddScoped<ISecurityAuditLogger, SecurityAuditLogger>();
builder.Services.AddSingleton<JwksRotationService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<JwksRotationService>());
builder.Services.AddSingleton<IKeyRotationCoordinator>(sp =>
    sp.GetRequiredService<JwksRotationService>()
);
builder.Services.AddScoped<IKeyStore, KeyStore>();

// Configure OpenIddict options via DI to register persisted signing keys without building a temporary provider
builder.Services.AddTransient<
    IConfigureOptions<OpenIddict.Server.OpenIddictServerOptions>,
    TansuCloud.Identity.Infrastructure.Options.OpenIddictSigningOptionsSetup
>();

// ASP.NET Identity
builder
    .Services.AddIdentity<IdentityUser, IdentityRole>(o =>
    {
        o.Password.RequireDigit = false;
        o.Password.RequiredLength = 6;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI();

// Ensure the login/denied/logout paths align with Identity UI and gateway routing
builder.Services.ConfigureApplicationCookie(options =>
{
    // Use backend-relative paths; the gateway maps "/identity/*" to this app with PathRemovePrefix.
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.LogoutPath = "/Identity/Account/Logout";
    // Emit audit events for sign-in/out via application cookie lifecycle (Auth category)
    options.Events = new CookieAuthenticationEvents
    {
        OnSignedIn = context =>
        {
            try
            {
                var audit = context.HttpContext.RequestServices.GetRequiredService<IAuditLogger>();
                var ev = new AuditEvent
                {
                    Category = "Auth",
                    Action = "SignIn",
                    Subject = context.Principal?.Identity?.Name ?? "unknown",
                    Outcome = "Success",
                    CorrelationId = context.HttpContext.TraceIdentifier
                };
                // Minimal allowlisted details
                audit.TryEnqueueRedacted(
                    ev,
                    new { Path = context.Request?.Path.Value },
                    new[] { "Path" }
                );
            }
            catch
            { /* never throw from auth events */
            }
            return Task.CompletedTask;
        },
        OnSigningOut = context =>
        {
            try
            {
                var audit = context.HttpContext.RequestServices.GetRequiredService<IAuditLogger>();
                var ev = new AuditEvent
                {
                    Category = "Auth",
                    Action = "SignOut",
                    Subject = context.HttpContext.User?.Identity?.Name ?? "unknown",
                    Outcome = "Success",
                    CorrelationId = context.HttpContext.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new { Path = context.Request?.Path.Value },
                    new[] { "Path" }
                );
            }
            catch { }
            return Task.CompletedTask;
        }
    };
});

// OpenIddict
builder
    .Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore().UseDbContext<AppDbContext>();
    })
    .AddServer(options =>
    {
        // Explicit issuer so discovery and endpoints include the gateway path base ("/identity")
        // dev default: go through gateway HTTP endpoint
        var issuer = builder.Configuration["Oidc:Issuer"] ?? "http://localhost:8080/identity/";
        if (!issuer.EndsWith('/'))
            issuer += "/";
        // Development normalization: align localhost host with 127.0.0.1 so Dashboard authority host matches token issuer
        try
        {
            var issuerUri = new Uri(issuer);
            if (issuerUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                var ub = new UriBuilder(issuerUri) { Host = "127.0.0.1" };
                issuer = ub.Uri.ToString();
            }
        }
        catch
        { /* ignore */
        }
        options.SetIssuer(new Uri(issuer));

        // Default relative endpoints; PathBase and Issuer ensure advertised URLs include "/identity"
        options
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .SetIntrospectionEndpointUris("/connect/introspect");
        // JWKS endpoint is served via JwksController at "/.well-known/jwks"; discovery override below advertises it.

        options
            .AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow() // Required for issuing/using refresh tokens with offline_access
            .RequireProofKeyForCodeExchange();

        // Dev-only: allow client credentials flow to enable automation/E2E without browser.
        // Controlled via configuration and environment to avoid enabling in production by default.
        var enableClientCreds =
            builder.Environment.IsDevelopment()
            && builder.Configuration.GetValue("Oidc:EnableClientCredentials", true);
        if (enableClientCreds)
        {
            options.AllowClientCredentialsFlow();
        }

        // Encryption key is required by OpenIddict server. Use an ephemeral key for dev/test.
        // For production, persist encryption keys similarly to signing keys.
        options.AddEphemeralEncryptionKey();

        options.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Roles,
            OpenIddictConstants.Scopes.OfflineAccess,
            // Custom API scopes
            "db.read",
            "db.write",
            "storage.read",
            "storage.write",
            "admin.full"
        );

        // Enrich issued tokens with tenant/plan/roles
        options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(builder =>
            builder.UseScopedHandler<TokenClaimsHandler>().Build()
        );

        // Audit successful sign-ins (token issuance) via OpenIddict ProcessSignIn
        options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(builder =>
            builder.UseScopedHandler<AuthAuditHandlers.ProcessSignInAuditHandler>().Build()
        );

        options.AddEventHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>(builder =>
            builder
                .UseScopedHandler<AuthAuditHandlers.ApplyAuthorizationResponseAuditHandler>()
                .Build()
        );

        options.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(builder =>
            builder.UseScopedHandler<AuthAuditHandlers.ApplyTokenResponseAuditHandler>().Build()
        );

        // Handlers above will emit audit entries for success/failure paths

        // Handle client_credentials grant by producing a ClaimsPrincipal for the client
        options.AddEventHandler<OpenIddictServerEvents.HandleTokenRequestContext>(builder =>
            builder.UseScopedHandler<ClientCredentialsHandler>().Build()
        );

        // Ensure discovery advertises endpoints under the canonical "/identity" base
        options.AddEventHandler<OpenIddictServerEvents.ApplyConfigurationResponseContext>(builder =>
            builder
                .UseInlineHandler(context =>
                {
                    var issuerUri =
                        context.Options.Issuer ?? new Uri("http://localhost:8080/identity/");
                    if (!issuerUri.AbsoluteUri.EndsWith('/'))
                    {
                        issuerUri = new Uri(issuerUri.AbsoluteUri + "/");
                    }
                    context.Response["issuer"] = issuerUri.AbsoluteUri;
                    context.Response["authorization_endpoint"] = new Uri(
                        issuerUri,
                        "connect/authorize"
                    ).AbsoluteUri;
                    context.Response["token_endpoint"] = new Uri(
                        issuerUri,
                        "connect/token"
                    ).AbsoluteUri;
                    context.Response["introspection_endpoint"] = new Uri(
                        issuerUri,
                        "connect/introspect"
                    ).AbsoluteUri;
                    context.Response["jwks_uri"] = new Uri(
                        issuerUri,
                        ".well-known/jwks"
                    ).AbsoluteUri;
                    return default;
                })
                .SetOrder(int.MaxValue) // run last so our values win
                .Build()
        );

        // Emit signed (not encrypted) JWT access tokens so resource servers can validate via JWKS
        options.DisableAccessTokenEncryption();

        // Token lifetimes from configuration
        var policy =
            builder
                .Configuration.GetSection(IdentityPolicyOptions.SectionName)
                .Get<IdentityPolicyOptions>() ?? new IdentityPolicyOptions();
        options.SetAccessTokenLifetime(policy.AccessTokenLifetime);
        options.SetRefreshTokenLifetime(policy.RefreshTokenLifetime);

        // ASP.NET Core host integration
        var aspnet = options
            .UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableStatusCodePagesIntegration();
        // In Development, allow HTTP (useful for local testing and when fronted by a gateway)
        if (builder.Environment.IsDevelopment())
        {
            aspnet.DisableTransportSecurityRequirement();
        }

        // Signing keys are added via OpenIddictSigningOptionsSetup (IConfigureOptions<OpenIddictServerOptions>)
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// For TokenClaimsHandler to read gateway headers
// HttpContextAccessor already registered above

builder.Services.AddRazorPages();

// Observability core (Task 38)
builder.Services.AddTansuObservabilityCore();
builder.Services.AddControllers();

// Dynamic external providers from DB (baseline: load once at startup)
builder.Services.AddDynamicExternalOidcProviders();
builder.Services.AddHttpClient(
    "local",
    c =>
    {
        c.BaseAddress = new Uri("http://127.0.0.1:5095");
    }
);

var app = builder.Build();

// Task 38 enrichment middleware
app.UseMiddleware<RequestEnrichmentMiddleware>();

// Apply migrations & seed dev data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch
    {
        // For Sqlite in-memory or when migrations unavailable in CI, ensure created
        db.Database.EnsureCreated();
    }

    await DevSeeder.SeedAsync(scope.ServiceProvider, app.Logger, app.Configuration);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Respect proxy headers from the Gateway (so Request.Scheme reflects client HTTPS)
app.UseForwardedHeaders(
    new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        // We trust the in-front gateway; clear defaults to accept any (dev only)
        ForwardLimit = null,
        AllowedHosts = { "*" }
    }
);

// Note: We avoid mutating PathBase from X-Forwarded-Prefix here.
// The gateway already applies PathRemovePrefix and forwards the prefix via headers
// only for informational purposes. Changing PathBase at this layer can interfere
// with endpoint matching for OpenIddict (e.g., /connect/token).

app.UseRouting();

// Enrichment middleware
app.UseMiddleware<RequestEnrichmentMiddleware>();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Map endpoints using minimal routing (OpenIddict registers its routes during UseOpenIddict)
app.MapControllers();
app.MapRazorPages();

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
        )
        .AllowAnonymous();
    app.MapGet(
            "/dev/logging/overrides",
            (IDynamicLogLevelOverride ovr) =>
                Results.Json(
                    ovr.Snapshot()
                        .ToDictionary(k => k.Key, v => new { v.Value.Level, v.Value.Expires })
                )
        )
        .AllowAnonymous();
}

// Placeholder for a future JWKS rotation background job
// app.Services.GetRequiredService<IHostedService>();

app.Run();

public sealed record LogOverrideRequest(string Category, LogLevel Level, int TtlSeconds);
