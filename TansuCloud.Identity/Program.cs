// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Threading.Tasks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Infrastructure;
using TansuCloud.Identity.Infrastructure.External;
using TansuCloud.Identity.Infrastructure.Keys;
using TansuCloud.Identity.Infrastructure.Options;
using TansuCloud.Identity.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry baseline: traces, metrics, logs
var svcName = "tansu.identity";
var svcVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(svcName, serviceVersion: svcVersion, serviceInstanceId: Environment.MachineName)
                             .AddAttributes(new KeyValuePair<string, object>[]
                             {
                                 new("deployment.environment", (object)builder.Environment.EnvironmentName)
                             }))
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
        metrics.AddOtlpExporter(otlp =>
        {
            var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                otlp.Endpoint = new Uri(endpoint);
            }
        });
    });
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
    var connectionString =
        builder.Configuration.GetConnectionString("Default")
        ?? "Host=localhost;Port=5432;Database=tansu_identity;Username=postgres;Password=postgres"; // dev fallback

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
builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
builder.Services.AddScoped<ISecurityAuditLogger, SecurityAuditLogger>();
builder.Services.AddSingleton<JwksRotationService>();
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
        var issuer = builder.Configuration["Oidc:Issuer"] ?? "https://localhost:7299/identity/";
        if (!issuer.EndsWith('/'))
            issuer += "/";
        options.SetIssuer(new Uri(issuer));

        // Default relative endpoints; PathBase and Issuer ensure advertised URLs include "/identity"
        options
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .SetIntrospectionEndpointUris("/connect/introspect");

        options
            .AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow() // Required for issuing/using refresh tokens with offline_access
            .RequireProofKeyForCodeExchange();

        // Encryption key is required by OpenIddict server. Use an ephemeral key for dev/test.
        // For production, persist encryption keys similarly to signing keys.
        options.AddEphemeralEncryptionKey();

        options.RegisterScopes(
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

        // Ensure discovery advertises endpoints under the canonical "/identity" base
        options.AddEventHandler<OpenIddictServerEvents.ApplyConfigurationResponseContext>(builder =>
            builder
                .UseInlineHandler(context =>
                {
                    var issuerUri = context.Options.Issuer ?? new Uri("https://localhost:7299/identity/");
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

        // Token lifetimes from configuration
        var policy =
            builder
                .Configuration.GetSection(IdentityPolicyOptions.SectionName)
                .Get<IdentityPolicyOptions>() ?? new IdentityPolicyOptions();
        options.SetAccessTokenLifetime(policy.AccessTokenLifetime);
        options.SetRefreshTokenLifetime(policy.RefreshTokenLifetime);

        options
            .UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableStatusCodePagesIntegration();

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
builder.Services.AddControllers();

// Dynamic external providers from DB (baseline: load once at startup)
builder.Services.AddDynamicExternalOidcProviders();
builder.Services.AddHttpClient(
    "local",
    c =>
    {
        c.BaseAddress = new Uri("http://localhost:5095");
    }
);

var app = builder.Build();

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

// Honor X-Forwarded-Prefix from the Gateway so generated URLs (discovery, redirects) include "/identity"
app.Use(
    async (context, next) =>
    {
        var prefix = context.Request.Headers["X-Forwarded-Prefix"].ToString();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            context.Request.PathBase = prefix;
        }
        await next();
    }
); // End of Middleware Forwarded Prefix -> PathBase

app.UseRouting();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

// Health endpoints
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

// Placeholder for a future JWKS rotation background job
// app.Services.GetRequiredService<IHostedService>();

app.Run();
