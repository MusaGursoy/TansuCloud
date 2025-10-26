// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt; // For token inspection logging
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Protocols; // For ConfigurationManager
using Microsoft.IdentityModel.Protocols.OpenIdConnect; // For OpenIdConnectConfigurationRetriever
using Microsoft.IdentityModel.Tokens;
using MudBlazor;
using MudBlazor.Services;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TansuCloud.Dashboard.Components;
using TansuCloud.Dashboard.Hosting;
using TansuCloud.Dashboard.Observability;
using TansuCloud.Dashboard.Observability.Logging;
using TansuCloud.Dashboard.Observability.SigNoz;
using TansuCloud.Observability;
using TansuCloud.Observability.Auditing;
using TansuCloud.Observability.Shared.Configuration;
using TansuCloud.Telemetry.Contracts;

var builder = WebApplication.CreateBuilder(args);

var appUrlsOptions = AppUrlsOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(appUrlsOptions);

static IReadOnlyList<SecurityKey> DeduplicateSigningKeys(IEnumerable<SecurityKey> keys)
{
    var result = new List<SecurityKey>();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var key in keys)
    {
        if (key is null)
        {
            continue;
        }

        var kid = key.KeyId;
        if (kid is null || seen.Add(kid))
        {
            result.Add(key);
        }
    }

    return result;
} // End of Method DeduplicateSigningKeys

static void LogOidcDiag(string message)
{
    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    var line = $"[OIDC-Diag] {timestamp} {message}";
    Console.WriteLine(line);
    try
    {
        var path = Path.Combine(AppContext.BaseDirectory, "oidc-diag.log");
        File.AppendAllText(path, line + Environment.NewLine);
    }
    catch
    {
        // Diagnostics logging should never throw
    }
} // End of Method LogOidcDiag

// Centralized base URLs (browser-visible and gateway backchannel)
var publicBaseUri = new Uri(appUrlsOptions.PublicBaseUrl!.TrimEnd('/') + "/");
var publicBaseUrl = publicBaseUri.GetLeftPart(UriPartial.Authority);
var gatewayBaseUrl = appUrlsOptions.GatewayBaseUrl;

// OpenTelemetry baseline for Dashboard
var dashName = "tansu.dashboard";
var dashVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(rb =>
        rb.AddService(
                dashName,
                serviceVersion: dashVersion,
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
        tracing.AddTansuAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        tracing.AddRedisInstrumentation();
        tracing.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
    })
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation();
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddMeter("TansuCloud.Audit");
        // Export OTLP diagnostics/gauges
        metrics.AddMeter("tansu.otel.exporter");
        metrics.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
    });

// Observability core (Task 38)
builder.Services.AddTansuObservabilityCore();
builder.Services.AddTansuAudit(builder.Configuration);
builder.Services.Configure<SigNozOptions>(
    builder.Configuration.GetSection(SigNozOptions.SectionName)
);
builder.Services.AddSingleton<SigNozMetricsCatalog>();

// MudBlazor services (MIT License - no keys required!)
builder.Services.AddMudServices();

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.ParseStateValues = true;
    o.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
});

// Elevate our custom OIDC diagnostic categories
builder.Logging.AddFilter("OIDC-Diagnostics", LogLevel.Information);
builder.Logging.AddFilter("OIDC-Callback", LogLevel.Information);

// Ensure custom diagnostics for metrics auth probe and ticket events are emitted
builder.Logging.AddFilter("MetricsAuthDiag", LogLevel.Information);
builder.Logging.AddFilter("OIDC-Ticket", LogLevel.Information);
builder
    .Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self" });

// Readiness: verify OTLP exporter reachability and W3C Activity id format
builder
    .Services.AddHealthChecks()
    .AddCheck<OtlpConnectivityHealthCheck>("otlp", tags: new[] { "ready", "otlp" });

// Phase 0: publish health transitions
builder.Services.AddSingleton<IHealthCheckPublisher, HealthTransitionPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(2);
    o.Period = TimeSpan.FromSeconds(15);
});

// Enable detailed IdentityModel logs in Development to diagnose OIDC metadata retrieval
if (builder.Environment.IsDevelopment())
{
    Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
}

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Configure SignalR (used by Blazor Server) keep-alives and client timeout for stability under load
builder.Services.AddSignalR(o =>
{
    // Server ping to clients; keep this short to detect broken connections promptly
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    // Allow clients to miss a few pings before the server considers them disconnected
    o.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
});
builder.Services.AddHttpContextAccessor();

// Observability: Metrics flow through SigNoz; legacy in-app Prometheus proxy removed.
Console.WriteLine(
    "[Observability] SigNoz now provides metrics, traces, and logs. In-app Prometheus proxy disabled."
);

// Log capture and reporting: options, buffer, provider, runtime switch, reporter, background service
builder.Services.Configure<LoggingReportOptions>(builder.Configuration.GetSection("LoggingReport"));

// Bind product telemetry reporting options (used by background reporter and admin APIs)
builder.Services.Configure<LogReportingOptions>(builder.Configuration.GetSection("LogReporting"));
builder.Services.AddSingleton<ILogBuffer>(sp =>
{
    var opts =
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LoggingReportOptions>>().CurrentValue;
    return new InMemoryLogBuffer(Math.Max(100, opts.MaxBufferEntries));
});
builder.Services.AddSingleton<ILoggerProvider, BufferedLoggerProvider>();
builder.Services.AddSingleton<ILogReportingRuntimeSwitch>(sp =>
{
    var opts =
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LoggingReportOptions>>().CurrentValue;
    return new LogReportingRuntimeSwitch(opts.Enabled);
});
builder.Services.AddHttpClient("log-reporter");
builder.Services.AddScoped<ILogReporter, ConfigurableLogReporter>();
builder.Services.AddHostedService<LogReportingBackgroundService>();

// Persist Data Protection keys so antiforgery and auth cookies can be decrypted across restarts/instances
// Prefer Redis when available; otherwise fall back to filesystem under /keys
var dp = builder.Services.AddDataProtection().SetApplicationName("TansuCloud.Dashboard");
var redisConn = builder.Configuration["Cache:Redis"] ?? builder.Configuration["Cache__Redis"];
if (!string.IsNullOrWhiteSpace(redisConn))
{
    try
    {
        var mux = ConnectionMultiplexer.Connect(redisConn);
        builder.Services.AddSingleton<IConnectionMultiplexer>(mux);
        dp.PersistKeysToStackExchangeRedis(mux, "DataProtection-Keys:Dashboard");
        Console.WriteLine(
            "[DataProtection] Using Redis key ring at 'DataProtection-Keys:Dashboard' (StackExchange.Redis)"
        );
    }
    catch
    {
        // fall back to filesystem if Redis connect fails
        var fallback = builder.Configuration["DataProtection:KeysPath"] ?? "/keys";
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch { }
        dp.PersistKeysToFileSystem(new DirectoryInfo(fallback));
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
    catch { }
    dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    Console.WriteLine($"[DataProtection] Using filesystem key ring at '{keysPath}'");
}

// One-time startup diagnostic (avoid building a provider here to prevent duplicate singletons)
try
{
    Console.WriteLine(
        "[DataProtection] Startup complete. DataProtection configured for TansuCloud.Dashboard"
    );
}
catch { }

// OIDC sign-in (simplified deterministic preload)
var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = "oidc";
});

authBuilder.AddCookie(
    "Cookies",
    o =>
    {
        o.Cookie.Path = "/";
        // In Development over HTTP, SameSite=None without Secure is rejected by modern browsers.
        // Use Lax so cookies flow on top-level GET redirects (OIDC callback) while remaining safe for subrequests.
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.None;
    }
);

authBuilder.AddOpenIdConnect(
    "oidc",
    options =>
    {
        // Ensure authentication cookies (correlation, nonce, main auth) are scoped to root so they survive path base/prefix changes via gateway
        options.CorrelationCookie.Path = "/"; // important behind /dashboard prefix
        options.NonceCookie.Path = "/";
        // In dev over HTTP, prefer Lax so the cookies are accepted and sent on the top-level GET callback.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        // Allow non-secure cookies in Development so they flow over HTTP
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.None;
        options.SaveTokens = true;
        // Default authority now uses the gateway-exposed public base URL to ensure discovery + JWKS succeed.
        var configuredAuthority = builder.Configuration["Oidc:Authority"];
        options.Authority = string.IsNullOrWhiteSpace(configuredAuthority)
            ? appUrlsOptions.GetAuthority("identity")
            : configuredAuthority.Trim().TrimEnd('/');
        // Prefer gateway backchannel for discovery when available/in-container
        var inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );
        var metadataOverride = builder.Configuration["Oidc:MetadataAddress"];
        options.MetadataAddress = string.IsNullOrWhiteSpace(metadataOverride)
            ? appUrlsOptions.GetBackchannelMetadataAddress(inContainer, "identity")
            : metadataOverride.Trim();
        options.ClientId = builder.Configuration["Oidc:ClientId"] ?? "tansu-dashboard";
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? "dev-secret";
        options.ResponseType = "code";
        // Use query response mode to ensure callback is a top-level GET navigation, which works with Lax cookies in dev HTTP
        options.ResponseMode = "query";
        options.UsePkce = true;
        options.SaveTokens = true;
        // Rely on the id_token for profile claims; avoid hitting the UserInfo endpoint (Identity has no handler yet).
        // Ref: https://learn.microsoft.com/aspnet/core/security/authentication/claims?view=aspnetcore-9.0#mapping-claims-using-openid-connect-authentication
        options.GetClaimsFromUserInfoEndpoint = false;
        // Behind the gateway, "/dashboard" is stripped before reaching this app
        // Keep local callback endpoints at root and register gateway URLs in Identity seeder
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("roles");
        // Enable refresh tokens for long-lived sessions in dev
        options.Scope.Add("offline_access");
        options.Scope.Add("admin.full");

        // If the initial metadata fetch yields no signing keys (e.g., JWKS not yet published when container is warming up)
        // automatically trigger a configuration refresh instead of failing the sign-in.
        options.RefreshOnIssuerKeyNotFound = true;

        // Allow overriding HTTPS metadata requirement via configuration (useful for local gateway HTTP)
        // Default: false in Development, true otherwise
        var requireHttps = builder.Configuration.GetValue(
            "Oidc:RequireHttpsMetadata",
            builder.Environment.IsDevelopment() ? false : true
        );
        options.RequireHttpsMetadata = requireHttps;
        // In local/dev scenarios, optionally accept any server certs for HTTPS backchannel
        var acceptAny = builder.Configuration.GetValue(
            "Oidc:AcceptAnyServerCert",
            builder.Environment.IsDevelopment()
        );
        if (!requireHttps && acceptAny)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            // Explicitly set the Backchannel so OIDC uses this handler for metadata/token/userinfo
            var backchannel = new HttpClient(handler);
            // Prefer HTTP/1.1 to avoid dev HTTP/2/TLS quirks
            backchannel.DefaultRequestVersion = System.Net.HttpVersion.Version11;
            backchannel.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            options.Backchannel = backchannel;
        }

        // Validation parameter baseline (can be temporarily relaxed via env var DASHBOARD_DISABLE_SIGKEY_VALIDATION=1 for diagnostics)
        var disableSigValidation = Environment.GetEnvironmentVariable(
            "DASHBOARD_DISABLE_SIGKEY_VALIDATION"
        );
        options.TokenValidationParameters.ValidateIssuerSigningKey = string.Equals(
            disableSigValidation,
            "1",
            StringComparison.OrdinalIgnoreCase
        )
            ? false
            : true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudience = options.ClientId; // OpenIddict ID token aud = client id
        // Claims enrichment is handled inside the consolidated oidcEvents.OnTokenValidated below.

        static IEnumerable<SecurityKey> FilterKeys(IEnumerable<SecurityKey> source, string? kid)
        {
            if (string.IsNullOrWhiteSpace(kid))
            {
                return source;
            }

            return source.Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal));
        }

        options.TokenValidationParameters.IssuerSigningKeyResolver = (
            token,
            securityToken,
            kid,
            validationParameters
        ) =>
        {
            try
            {
                Console.WriteLine(
                    $"[OidcPreload] IssuerSigningKeyResolver invoked (kid={kid ?? "<null>"})"
                );
                var resolved = new List<SecurityKey>();

                if (options.Configuration?.SigningKeys is { Count: > 0 } cfgKeys)
                {
                    resolved.AddRange(FilterKeys(cfgKeys, kid));
                }

                if (resolved.Count == 0 && options.ConfigurationManager is not null)
                {
                    var cfg = options
                        .ConfigurationManager.GetConfigurationAsync(CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    if (cfg?.SigningKeys is { Count: > 0 } liveKeys)
                    {
                        if (!ReferenceEquals(options.Configuration, cfg))
                        {
                            options.Configuration = cfg;
                        }

                        resolved.AddRange(FilterKeys(liveKeys, kid));
                    }
                }

                if (
                    resolved.Count == 0
                    && options.TokenValidationParameters.IssuerSigningKeys
                        is IEnumerable<SecurityKey> fallback
                )
                {
                    resolved.AddRange(FilterKeys(fallback, kid));
                }

                if (resolved.Count == 0)
                {
                    options.ConfigurationManager?.RequestRefresh();
                }

                return DeduplicateSigningKeys(resolved);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[OidcPreload] IssuerSigningKeyResolver exception {ex.GetType().Name}: {ex.Message}"
                );
                options.ConfigurationManager?.RequestRefresh();
                return Array.Empty<SecurityKey>();
            }
        };

        // Optional DEV-only bypass of signature validation for isolation: DASHBOARD_BYPASS_IDTOKEN_SIGNATURE=1
        var bypassSig = Environment.GetEnvironmentVariable("DASHBOARD_BYPASS_IDTOKEN_SIGNATURE");
        if (string.Equals(bypassSig, "1", StringComparison.OrdinalIgnoreCase))
        {
            // In ASP.NET Core 8+/IdentityModel 6+, SignatureValidator must return a JsonWebToken when handling JWS/JWT strings.
            // We also disable issuer signing key validation entirely. This is DEV-ONLY to isolate other issues.
            options.TokenValidationParameters.ValidateIssuerSigningKey = false;
            options.TokenValidationParameters.SignatureValidator = (token, parameters) =>
            {
                try
                {
                    return new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[BypassSignatureValidator] Exception {ex.GetType().Name}: {ex.Message}"
                    );
                    // Fallback: parse a minimal structurally valid unsigned JWT (header.payload.)
                    // header: {"alg":"none"} -> eyJhbGciOiJub25lIn0
                    // payload: {} -> e30
                    var minimal = "eyJhbGciOiJub25lIn0.e30.";
                    return new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(minimal);
                }
            };
            Console.WriteLine(
                "[BypassSignatureValidator] ACTIVE: id_token signature verification is bypassed (DEV ONLY)"
            );
        }

        // Deterministic metadata + JWKS preload (always) before handler constructed.
        var metadataCandidates = new List<(string Address, string Label)>();
        if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
        {
            metadataCandidates.Add((options.MetadataAddress!, "configured"));
        }

        if (!string.IsNullOrWhiteSpace(appUrlsOptions.GatewayBaseUrl))
        {
            try
            {
                var derived =
                    $"{appUrlsOptions.GatewayBaseUrl!.TrimEnd('/')}/identity/.well-known/openid-configuration";
                metadataCandidates.Add((derived, "gateway-base"));
            }
            catch { }
        }

        var identityBaseForFallback = builder.Configuration["Services:IdentityBaseUrl"];
        if (!string.IsNullOrWhiteSpace(identityBaseForFallback))
        {
            var fallback =
                identityBaseForFallback.TrimEnd('/') + "/.well-known/openid-configuration";
            metadataCandidates.Add((fallback, "identity-service"));
        }

        static bool IsRunningInContainer() =>
            string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase
            );

        if (IsRunningInContainer())
        {
            // Compose deploys Identity under the in-cluster DNS name "identity". Fetching metadata
            // directly avoids a bootstrap deadlock when the gateway depends on the dashboard's health
            // but the dashboard needs the gateway to serve discovery. This fallback keeps container
            // startup deterministic while still honoring the gateway/public URLs once configuration is applied.
            const string containerIdentityMetadata =
                "http://identity:8080/.well-known/openid-configuration";
            metadataCandidates.Add((containerIdentityMetadata, "container-identity"));
        }

        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ConfigurationManager<OpenIdConnectConfiguration>? selectedManager = null;
        OpenIdConnectConfiguration? selectedConfig = null;
        string? selectedMetadata = null;
        string selectedSource = "";
        Exception? lastMetadataError = null;

        Console.WriteLine(
            $"[OidcPreload] Authority={options.Authority} evaluating {metadataCandidates.Count} metadata candidate(s)"
        );

        const int metadataMaxAttempts = 6;
        var metadataBaseDelay = TimeSpan.FromMilliseconds(200);

        foreach (var (address, label) in metadataCandidates)
        {
            if (string.IsNullOrWhiteSpace(address) || !seenAddresses.Add(address))
            {
                continue;
            }

            Console.WriteLine($"[OidcPreload] Attempting metadata ({label}) {address}");
            var docRetriever = new HttpDocumentRetriever
            {
                RequireHttps = options.RequireHttpsMetadata
            };
            var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                address,
                new OpenIdConnectConfigurationRetriever(),
                docRetriever
            );

            Exception? attemptError = null;
            for (var attempt = 1; attempt <= metadataMaxAttempts; attempt++)
            {
                try
                {
                    var cfg = manager
                        .GetConfigurationAsync(CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    selectedManager = manager;
                    selectedConfig = cfg;
                    selectedMetadata = address;
                    selectedSource = label;
                    Console.WriteLine(
                        $"[OidcPreload] Metadata ({label}) succeeded on attempt {attempt}"
                    );
                    attemptError = null;
                    break;
                }
                catch (Exception ex)
                {
                    attemptError = ex;
                    Console.WriteLine(
                        $"[OidcPreload] Metadata attempt ({label}) try {attempt} failed: {ex.GetType().Name}: {ex.Message}"
                    );

                    if (attempt < metadataMaxAttempts)
                    {
                        var backoff = TimeSpan.FromMilliseconds(
                            Math.Min(
                                metadataBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
                                2000
                            )
                        );
                        try
                        {
                            System.Threading.Thread.Sleep(backoff);
                        }
                        catch { }
                    }
                }
            }

            if (selectedConfig is not null)
            {
                break;
            }

            if (attemptError is not null)
            {
                lastMetadataError = attemptError;
                Console.WriteLine(
                    $"[OidcPreload] Metadata attempt ({label}) exhausted after {metadataMaxAttempts} tries: {attemptError.GetType().Name}: {attemptError.Message}"
                );
            }
        }

        void ApplyOidcConfiguration(
            OpenIdConnectConfiguration cfg,
            ConfigurationManager<OpenIdConnectConfiguration> cfgManager,
            string metadataAddress,
            string sourceLabel
        )
        {
            try
            {
                var publicRoot = new Uri(publicBaseUrl);
                cfg.AuthorizationEndpoint = new Uri(publicRoot, "connect/authorize").AbsoluteUri;

                try
                {
                    var md = new Uri(metadataAddress);
                    var gatewayBase = new Uri(md, "../");
                    var gatewayBaseStr = gatewayBase.ToString();
                    if (!gatewayBaseStr.EndsWith('/'))
                    {
                        gatewayBaseStr += "/";
                    }

                    var gatewayBaseUri = new Uri(gatewayBaseStr);
                    cfg.TokenEndpoint = new Uri(gatewayBaseUri, "connect/token").AbsoluteUri;
                    if (!string.IsNullOrWhiteSpace(cfg.UserInfoEndpoint))
                    {
                        cfg.UserInfoEndpoint = new Uri(
                            gatewayBaseUri,
                            "connect/userinfo"
                        ).AbsoluteUri;
                    }
                    if (!string.IsNullOrWhiteSpace(cfg.IntrospectionEndpoint))
                    {
                        cfg.IntrospectionEndpoint = new Uri(
                            gatewayBaseUri,
                            "connect/introspect"
                        ).AbsoluteUri;
                    }
                    cfg.JwksUri = new Uri(gatewayBaseUri, ".well-known/jwks").AbsoluteUri;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(cfg.EndSessionEndpoint))
                {
                    cfg.EndSessionEndpoint = new Uri(publicRoot, "connect/endsession").AbsoluteUri;
                }
            }
            catch { }

            if (cfg.SigningKeys.Count == 0 && !string.IsNullOrWhiteSpace(cfg.JwksUri))
            {
                using var http = options.Backchannel ?? new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var jwksJson = http.GetStringAsync(cfg.JwksUri).GetAwaiter().GetResult();
                var jwks = new JsonWebKeySet(jwksJson);
                foreach (var k in jwks.GetSigningKeys())
                {
                    cfg.SigningKeys.Add(k);
                }
            }

            options.Configuration = cfg;
            options.MetadataAddress = metadataAddress;

            var freeze = Environment.GetEnvironmentVariable("DASHBOARD_FREEZE_OIDC_CONFIG");
            var shouldFreeze = string.Equals(freeze, "1", StringComparison.OrdinalIgnoreCase);

            if (cfg.SigningKeys.Count > 0)
            {
                options.TokenValidationParameters.IssuerSigningKeys = cfg.SigningKeys.ToList();
                if (shouldFreeze)
                {
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(cfg);
                    Console.WriteLine(
                        $"[OidcPreload] Loaded Issuer={cfg.Issuer} KeyCount={cfg.SigningKeys.Count} (static via {sourceLabel})"
                    );
                }
                else
                {
                    options.ConfigurationManager = cfgManager;
                    Console.WriteLine(
                        $"[OidcPreload] Loaded Issuer={cfg.Issuer} KeyCount={cfg.SigningKeys.Count} (dynamic via {sourceLabel})"
                    );
                }
            }
            else
            {
                options.ConfigurationManager = cfgManager;
                Console.WriteLine(
                    $"[OidcPreload] Loaded Issuer={cfg.Issuer} KeyCount=0 (dynamic via {sourceLabel})"
                );
            }

            try
            {
                var issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(cfg.Issuer))
                {
                    issuers.Add(cfg.Issuer.TrimEnd('/'));
                    issuers.Add(cfg.Issuer.TrimEnd('/') + "/");
                }
                if (!string.IsNullOrWhiteSpace(options.Authority))
                {
                    issuers.Add(options.Authority.TrimEnd('/'));
                    issuers.Add(options.Authority.TrimEnd('/') + "/");
                }
                if (issuers.Count > 0)
                {
                    options.TokenValidationParameters.ValidIssuers = issuers;
                    if (!options.TokenValidationParameters.ValidateIssuer)
                    {
                        options.TokenValidationParameters.ValidateIssuer = true;
                    }
                    Console.WriteLine($"[OidcPreload] ValidIssuers={string.Join(',', issuers)}");
                }
            }
            catch { }
        }

        if (
            selectedConfig is not null
            && selectedManager is not null
            && !string.IsNullOrWhiteSpace(selectedMetadata)
        )
        {
            ApplyOidcConfiguration(
                selectedConfig,
                selectedManager,
                selectedMetadata!,
                selectedSource
            );
        }
        else if (lastMetadataError is not null)
        {
            Console.WriteLine(
                $"[OidcPreload] FAILED {lastMetadataError.GetType().Name}: {lastMetadataError.Message}"
            );
        }
        else
        {
            Console.WriteLine("[OidcPreload] FAILED: No valid metadata candidates resolved");
        }

        // Minimal events: adjust redirect URI behind prefix and log token validation summary
        var oidcEvents = new OpenIdConnectEvents();
        oidcEvents.OnMessageReceived = async ctx =>
        {
            try
            {
                var sp = ctx.HttpContext.RequestServices;
                var logger = sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-Diagnostics");
                var optsDiag = ctx.Options;
                var keyCount = optsDiag.Configuration?.SigningKeys.Count ?? 0;
                logger.LogWarning(
                    "OIDC message received. Path={Path} KeyCount={KeyCount} HasConfig={HasConfig}",
                    ctx.Request.Path,
                    keyCount,
                    optsDiag.Configuration != null
                );
                LogOidcDiag(
                    $"OnMessageReceived Path={ctx.Request.Path} KeyCount={keyCount} HasConfig={optsDiag.Configuration != null}"
                );

                // If no signing keys are loaded yet, fetch JWKS eagerly to avoid first-hit signature failures.
                if ((optsDiag.Configuration == null || keyCount == 0))
                {
                    try
                    {
                        // Ensure a configuration manager exists
                        if (
                            optsDiag.ConfigurationManager == null
                            && !string.IsNullOrWhiteSpace(optsDiag.MetadataAddress)
                        )
                        {
                            optsDiag.ConfigurationManager =
                                new ConfigurationManager<OpenIdConnectConfiguration>(
                                    optsDiag.MetadataAddress,
                                    new OpenIdConnectConfigurationRetriever(),
                                    new HttpDocumentRetriever
                                    {
                                        RequireHttps = optsDiag.RequireHttpsMetadata
                                    }
                                );
                        }

                        // Try to get configuration (discovery) if missing
                        if (
                            optsDiag.ConfigurationManager is not null
                            && optsDiag.Configuration is null
                        )
                        {
                            var cfg = await optsDiag.ConfigurationManager.GetConfigurationAsync(
                                ctx.HttpContext.RequestAborted
                            );
                            optsDiag.Configuration = cfg;
                            keyCount = cfg.SigningKeys.Count;
                            LogOidcDiag(
                                $"Configuration fetched via manager. Keys={keyCount} Metadata={optsDiag.MetadataAddress ?? "<null>"}"
                            );
                        }

                        // If still no keys, query JWKS directly and hydrate
                        if (
                            optsDiag.Configuration is not null
                            && optsDiag.Configuration.SigningKeys.Count == 0
                        )
                        {
                            // Prefer rebasing JWKS to the gateway host if MetadataAddress is set
                            var jwksUrl = optsDiag.Configuration.JwksUri;
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(optsDiag.MetadataAddress))
                                {
                                    var md = new Uri(optsDiag.MetadataAddress);
                                    var gatewayBase = new Uri(
                                        new Uri(md, "../").ToString().TrimEnd('/') + "/"
                                    );
                                    jwksUrl = new Uri(gatewayBase, ".well-known/jwks").AbsoluteUri;
                                }
                            }
                            catch { }

                            using var http = optsDiag.Backchannel ?? new HttpClient();
                            http.Timeout = TimeSpan.FromSeconds(3);
                            var jwksJson = await http.GetStringAsync(
                                jwksUrl,
                                ctx.HttpContext.RequestAborted
                            );
                            var jwks = new JsonWebKeySet(jwksJson);
                            foreach (var k in jwks.GetSigningKeys())
                            {
                                optsDiag.Configuration.SigningKeys.Add(k);
                            }

                            if (
                                optsDiag.Configuration.SigningKeys.Count > 0
                                && (
                                    optsDiag.TokenValidationParameters.IssuerSigningKeys == null
                                    || !optsDiag.TokenValidationParameters.IssuerSigningKeys.Any()
                                )
                            )
                            {
                                optsDiag.TokenValidationParameters.IssuerSigningKeys =
                                    optsDiag.Configuration.SigningKeys.ToList();
                                logger.LogWarning(
                                    "OIDC JWKS hydrated via OnMessageReceived. Keys={KeyCount}",
                                    optsDiag.TokenValidationParameters.IssuerSigningKeys.Count()
                                );
                                LogOidcDiag(
                                    $"JWKS hydrated. Keys={optsDiag.TokenValidationParameters.IssuerSigningKeys.Count()}"
                                );
                            }
                        }
                    }
                    catch (Exception jwksEx)
                    {
                        // Log and continue; the handler may refresh on key-not-found and succeed on retry
                        var msg = jwksEx.Message;
                        logger.LogWarning(
                            jwksEx,
                            "OIDC OnMessageReceived JWKS preload failed: {Message}",
                            msg
                        );
                        LogOidcDiag($"JWKS preload exception {jwksEx.GetType().Name}: {msg}");
                    }
                }
            }
            catch { }
        }; // End of OnMessageReceived
        // Log id_token header as soon as token response is received (code flow)
        oidcEvents.OnTokenResponseReceived = ctx =>
        {
            try
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-Diagnostics");
                var idt = ctx.TokenEndpointResponse?.IdToken;
                if (!string.IsNullOrWhiteSpace(idt))
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwt = handler.ReadJwtToken(idt);
                    var kid = jwt.Header.Kid;
                    var alg = jwt.Header.Alg;
                    // Stash for later, e.g., AuthenticationFailed
                    ctx.HttpContext.Items["diag.id_token.kid"] = kid ?? string.Empty;
                    ctx.HttpContext.Items["diag.id_token.alg"] = alg ?? string.Empty;
                    logger.LogWarning(
                        "OIDC token response received. IdTokenHeader Kid={Kid} Alg={Alg}",
                        kid,
                        alg
                    );
                    LogOidcDiag(
                        $"Token response received. Kid={kid ?? "<null>"} Alg={alg ?? "<null>"}"
                    );
                }
            }
            catch { }
            return Task.CompletedTask;
        }; // End of OnTokenResponseReceived
        oidcEvents.OnRedirectToIdentityProvider = ctx =>
        {
            try
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-RedirectDiag");
                var queryKeys = string.Join(",", ctx.Request.Query.Keys);
                var parameterKeys = string.Join(
                    ",",
                    ctx.ProtocolMessage.Parameters?.Keys ?? Array.Empty<string>()
                );
                logger.LogInformation(
                    "OIDC redirect (start) Path={Path} QueryKeys={QueryKeys} ParamKeys={ParamKeys} PropertiesRedirectUri={PropertiesRedirectUri}",
                    ctx.Request.Path,
                    queryKeys,
                    parameterKeys,
                    ctx.Properties?.RedirectUri ?? "<null>"
                );
            }
            catch
            {
                // Diagnostic logging must never affect runtime behavior
            }
            // Ensure browser-visible RedirectUri honors the dashboard prefix.
            // Prefer X-Forwarded-Prefix from the gateway; fallback to PathBase (behaves like UsePathBase)
            // and only then infer from the incoming request path.
            var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
            if (string.IsNullOrEmpty(prefix))
            {
                var pb = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value! : string.Empty;
                if (
                    !string.IsNullOrEmpty(pb)
                    && pb.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase)
                )
                {
                    prefix = "/dashboard";
                }
                else if (
                    ctx.Request.Path.HasValue && ctx.Request.Path.StartsWithSegments("/dashboard")
                )
                {
                    prefix = "/dashboard";
                }
            }
            if (!string.IsNullOrEmpty(prefix))
            {
                var callback = ctx.Options.CallbackPath.HasValue
                    ? ctx.Options.CallbackPath.Value!
                    : "/signin-oidc";
                var full = $"{prefix}{callback}";
                ctx.ProtocolMessage.RedirectUri = new Uri(publicBaseUri, full).ToString();
            }

            // Always set the post-authentication return URL explicitly to the original requested URL
            // built from PathBase/Path/Query, ensuring the dashboard prefix is included.
            try
            {
                var pathBase = ctx.Request.PathBase.HasValue
                    ? ctx.Request.PathBase.Value!
                    : string.Empty;
                var path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : string.Empty;
                var query = ctx.Request.QueryString.HasValue
                    ? ctx.Request.QueryString.Value
                    : string.Empty;
                var pathAndQuery = string.Concat(pathBase, path, query);
                // If no PathBase was applied but we can infer the dashboard prefix, prepend it.
                if (
                    string.IsNullOrEmpty(pathBase)
                    && !string.IsNullOrEmpty(prefix)
                    && !pathAndQuery.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                )
                {
                    var joiner = (pathAndQuery.StartsWith("/") ? string.Empty : "/");
                    pathAndQuery = prefix.TrimEnd('/') + joiner + pathAndQuery.TrimStart('/');
                }
                var absoluteDesired = new Uri(publicBaseUri, pathAndQuery).ToString();
                // Persist our canonical desired URL so callback can use it deterministically
                if (ctx.Properties is AuthenticationProperties propsSet1)
                {
                    propsSet1.RedirectUri = absoluteDesired;
                    propsSet1.Items["tansu.returnUri"] = absoluteDesired;
                }
            }
            catch { }

            // Force the authorize endpoint to use the public 127.0.0.1 host to avoid 'gateway' redirects in the browser
            try
            {
                // Prefer the gateway's root alias for OIDC authorize with the public host (127.0.0.1)
                // This avoids exposing the in-cluster name 'gateway' to the browser and works with our YARP root alias.
                var authorize = new Uri(publicBaseUri, "connect/authorize").AbsoluteUri;
                ctx.ProtocolMessage.IssuerAddress = authorize;
            }
            catch { }

            // Also normalize any pre-existing post-authentication return URL to include the dashboard prefix
            try
            {
                // If we previously stashed our canonical desired URL, prefer it and avoid overriding it later.
                if (
                    ctx.Properties?.Items is not null
                    && ctx.Properties.Items.TryGetValue("tansu.returnUri", out var stashedAbs)
                    && !string.IsNullOrWhiteSpace(stashedAbs)
                )
                {
                    if (ctx.Properties is AuthenticationProperties props)
                    {
                        props.RedirectUri = stashedAbs;
                    }
                    return Task.CompletedTask;
                }
                // If we previously stashed our canonical desired URL, prefer it.
                if (
                    ctx.Properties?.Items is not null
                    && ctx.Properties.Items.TryGetValue("tansu.returnUri", out var stashedStr)
                    && !string.IsNullOrWhiteSpace(stashedStr)
                )
                {
                    if (ctx.Properties is AuthenticationProperties propsSet2)
                    {
                        propsSet2.RedirectUri = stashedStr;
                    }
                }
                else
                {
                    var desired = ctx.Properties?.RedirectUri;
                    if (!string.IsNullOrWhiteSpace(desired))
                    {
                        var effPrefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
                        if (
                            string.IsNullOrEmpty(effPrefix)
                            && ctx.Request.Path.HasValue
                            && ctx.Request.Path.StartsWithSegments("/dashboard")
                        )
                        {
                            effPrefix = "/dashboard";
                        }
                        string pathAndQuery;
                        if (Uri.TryCreate(desired, UriKind.Absolute, out var abs))
                        {
                            pathAndQuery = abs.PathAndQuery;
                        }
                        else
                        {
                            pathAndQuery = desired;
                        }
                        // Prepend prefix if missing
                        if (
                            !string.IsNullOrEmpty(effPrefix)
                            && !pathAndQuery.StartsWith(
                                effPrefix,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            var joiner = pathAndQuery.StartsWith("/") ? string.Empty : "/";
                            pathAndQuery =
                                effPrefix.TrimEnd('/') + joiner + pathAndQuery.TrimStart('/');
                        }
                        // Build absolute public URL
                        if (ctx.Properties is AuthenticationProperties propsSet3)
                        {
                            propsSet3.RedirectUri = new Uri(publicBaseUri, pathAndQuery).ToString();
                        }
                    }
                }
            }
            catch { }
            try
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-RedirectDiag");
                var hasStashed = ctx.Properties?.Items?.ContainsKey("tansu.returnUri") ?? false;
                var stashed = "<none>";
                if (
                    hasStashed
                    && ctx.Properties!.Items.TryGetValue("tansu.returnUri", out var stashedStr)
                    && !string.IsNullOrWhiteSpace(stashedStr)
                )
                {
                    stashed = stashedStr;
                }
                logger.LogInformation(
                    "OIDC redirect (final) IssuerAddress={IssuerAddress} RedirectUri={RedirectUri} PropertiesRedirectUri={PropertiesRedirectUri} StashedReturnUri={StashedReturnUri}",
                    ctx.ProtocolMessage.IssuerAddress,
                    ctx.ProtocolMessage.RedirectUri,
                    ctx.Properties?.RedirectUri ?? "<null>",
                    stashed
                );
            }
            catch
            {
                // Diagnostic logging must never affect runtime behavior
            }
            return Task.CompletedTask;
        }; // End of OnRedirectToIdentityProvider
        oidcEvents.OnRedirectToIdentityProviderForSignOut = ctx =>
        {
            var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
            if (string.IsNullOrEmpty(prefix))
            {
                var pb = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value! : string.Empty;
                if (
                    !string.IsNullOrEmpty(pb)
                    && pb.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase)
                )
                {
                    prefix = "/dashboard";
                }
                else if (
                    ctx.Request.Path.HasValue && ctx.Request.Path.StartsWithSegments("/dashboard")
                )
                {
                    prefix = "/dashboard";
                }
            }
            if (!string.IsNullOrEmpty(prefix))
            {
                var callback = ctx.Options.SignedOutCallbackPath.HasValue
                    ? ctx.Options.SignedOutCallbackPath.Value!
                    : "/signout-callback-oidc";
                var full = $"{prefix}{callback}";
                ctx.ProtocolMessage.PostLogoutRedirectUri = new Uri(publicBaseUri, full).ToString();
            }
            return Task.CompletedTask;
        }; // End of OnRedirectToIdentityProviderForSignOut
        oidcEvents.OnTokenValidated = ctx =>
        {
            try
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-Diagnostics");
                if (ctx.SecurityToken is JwtSecurityToken jwt)
                {
                    logger.LogInformation(
                        "OIDC token validated. Kid={Kid} Aud={Aud} Sub={Sub}",
                        jwt.Header.Kid,
                        string.Join(',', jwt.Audiences),
                        jwt.Subject
                    );
                }

                // Enrich the cookie principal with scopes/roles from the access token so server-side
                // authorization (AdminOnly) works for API calls using the cookie auth.
                try
                {
                    var accessToken =
                        ctx.TokenEndpointResponse?.AccessToken ?? ctx.ProtocolMessage.AccessToken;
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        var handler = new JwtSecurityTokenHandler();
                        var at = handler.ReadJwtToken(accessToken);
                        var newClaims = new List<System.Security.Claims.Claim>();

                        // scope (space-delimited)
                        foreach (
                            var sc in at.Claims.Where(c =>
                                string.Equals(c.Type, "scope", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            var parts = sc.Value.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            );
                            newClaims.AddRange(
                                parts.Select(p => new System.Security.Claims.Claim("scope", p))
                            );
                        }
                        // scp (Azure style)
                        foreach (
                            var scp in at.Claims.Where(c =>
                                string.Equals(c.Type, "scp", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            var parts = scp.Value.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            );
                            newClaims.AddRange(
                                parts.Select(p => new System.Security.Claims.Claim("scp", p))
                            );
                        }
                        // roles / role
                        foreach (
                            var role in at.Claims.Where(c =>
                                string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            newClaims.Add(
                                new System.Security.Claims.Claim(
                                    System.Security.Claims.ClaimTypes.Role,
                                    role.Value
                                )
                            );
                        }
                        foreach (
                            var roles in at.Claims.Where(c =>
                                string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            newClaims.Add(
                                new System.Security.Claims.Claim(
                                    System.Security.Claims.ClaimTypes.Role,
                                    roles.Value
                                )
                            );
                        }

                        if (newClaims.Count > 0)
                        {
                            var identity =
                                ctx.Principal?.Identities.FirstOrDefault()
                                ?? new System.Security.Claims.ClaimsIdentity("oidc");
                            identity.AddClaims(newClaims);
                            if (ctx.Principal is null)
                            {
                                ctx.Principal = new System.Security.Claims.ClaimsPrincipal(
                                    identity
                                );
                            }
                        }
                    }
                }
                catch
                {
                    // best-effort enrichment
                }

                // Normalize the post-authentication RedirectUri to include the dashboard prefix when hosted behind the gateway.
                // In some flows, the saved RedirectUri is a root-anchored path like "/admin/metrics" without the "/dashboard" prefix,
                // which leads to a 404 at the gateway. Force-prefix it when needed and build an absolute public URL.
                // Prefer our stashed canonical desired URL, if present, and do not override it.
                if (
                    ctx.Properties?.Items is not null
                    && ctx.Properties.Items.TryGetValue("tansu.returnUri", out var stashedDesired)
                    && !string.IsNullOrWhiteSpace(stashedDesired)
                )
                {
                    if (ctx.Properties is AuthenticationProperties propsSet4)
                    {
                        propsSet4.RedirectUri = stashedDesired;
                    }
                    return Task.CompletedTask;
                }
                var desired = ctx.Properties?.RedirectUri;
                var effPrefix = ctx.HttpContext.Request.Headers["X-Forwarded-Prefix"].ToString();
                if (string.IsNullOrEmpty(effPrefix))
                {
                    var req = ctx.HttpContext.Request;
                    if (req.Path.HasValue && req.Path.StartsWithSegments("/dashboard"))
                    {
                        effPrefix = "/dashboard";
                    }
                    else if (
                        req.PathBase.HasValue
                        && req.PathBase.Value!.StartsWith(
                            "/dashboard",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        effPrefix = "/dashboard";
                    }
                }
                if (!string.IsNullOrEmpty(effPrefix))
                {
                    string pathAndQuery;
                    if (string.IsNullOrWhiteSpace(desired))
                    {
                        // Fall back to original request path if RedirectUri is empty
                        var req = ctx.HttpContext.Request;
                        var pathBase = req.PathBase.HasValue ? req.PathBase.Value! : string.Empty;
                        var path = req.Path.HasValue ? req.Path.Value! : string.Empty;
                        var query = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;
                        pathAndQuery = string.Concat(pathBase, path, query);
                    }
                    else if (Uri.TryCreate(desired, UriKind.Absolute, out var abs))
                    {
                        pathAndQuery = abs.PathAndQuery;
                    }
                    else
                    {
                        pathAndQuery = desired;
                    }
                    if (!pathAndQuery.StartsWith(effPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var joiner = pathAndQuery.StartsWith('/') ? string.Empty : "/";
                        pathAndQuery =
                            effPrefix.TrimEnd('/') + joiner + pathAndQuery.TrimStart('/');
                    }
                    // Build the absolute URL using the public base (browser-visible) origin
                    if (ctx.Properties is AuthenticationProperties propsSet5)
                    {
                        propsSet5.RedirectUri = new Uri(
                            new Uri(publicBaseUrl),
                            pathAndQuery
                        ).ToString();
                    }
                    logger.LogInformation(
                        "OIDC post-auth RedirectUri normalized to {RedirectUri}",
                        ctx.Properties?.RedirectUri
                    );
                }
            }
            catch { }
            return Task.CompletedTask;
        }; // End of OnTokenValidated
        oidcEvents.OnAuthenticationFailed = ctx =>
        {
            try
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-Diagnostics");
                var monitor =
                    ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>();
                var optsDiag = monitor.Get("oidc");
                var keyCount = optsDiag.Configuration?.SigningKeys.Count ?? 0;
                var tvpKeyCount =
                    optsDiag.TokenValidationParameters.IssuerSigningKeys?.Count() ?? 0;
                var resolverSet =
                    optsDiag.TokenValidationParameters.IssuerSigningKeyResolver != null;
                var cfgKids = (
                    optsDiag.Configuration?.SigningKeys ?? Enumerable.Empty<SecurityKey>()
                )
                    .OfType<JsonWebKey>()
                    .Select(k => k.Kid ?? k.KeyId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                var tvpKids = (
                    optsDiag.TokenValidationParameters.IssuerSigningKeys
                    ?? Enumerable.Empty<SecurityKey>()
                )
                    .OfType<JsonWebKey>()
                    .Select(k => k.Kid ?? k.KeyId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                // Try to capture the KID/ALG the handler was looking for from the previously stored token response
                ctx.HttpContext.Items.TryGetValue("diag.id_token.kid", out var kidObj);
                ctx.HttpContext.Items.TryGetValue("diag.id_token.alg", out var algObj);
                var tokenKid = kidObj as string ?? string.Empty;
                var tokenAlg = algObj as string ?? string.Empty;
                // If the exception is a SecurityTokenSignatureKeyNotFoundException, surface that explicitly
                var exType = ctx.Exception.GetType().Name;
                logger.LogError(
                    ctx.Exception,
                    "OIDC authentication failed: {Message}. ExType={ExType} ConfigKeyCount={KeyCount} TVPKeyCount={TVPKeyCount} ResolverSet={ResolverSet} ValidateIssuerSigningKey={ValidateIssuerSigningKey} Authority={Authority} Metadata={Metadata} TokenKid={TokenKid} TokenAlg={TokenAlg} CfgKids=[{CfgKids}] TvpKids=[{TvpKids}]",
                    ctx.Exception.Message,
                    exType,
                    keyCount,
                    tvpKeyCount,
                    resolverSet,
                    optsDiag.TokenValidationParameters.ValidateIssuerSigningKey,
                    optsDiag.Authority,
                    optsDiag.MetadataAddress,
                    tokenKid,
                    tokenAlg,
                    string.Join(',', cfgKids),
                    string.Join(',', tvpKids)
                );
            }
            catch { }
            return Task.CompletedTask;
        }; // End of OnAuthenticationFailed

        // Ensure the final post-login redirect targets the prefixed dashboard path when behind the gateway
        oidcEvents.OnTicketReceived = ctx =>
        {
            try
            {
                var logger = ctx
                    .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-Diagnostics");
                // If our canonical return target was stashed, enforce it and stop.
                if (
                    ctx.Properties?.Items is not null
                    && ctx.Properties.Items.TryGetValue("tansu.returnUri", out var stashedStr)
                    && !string.IsNullOrWhiteSpace(stashedStr)
                )
                {
                    ctx.ReturnUri = stashedStr;
                    return Task.CompletedTask;
                }
                var effPrefix = ctx.HttpContext.Request.Headers["X-Forwarded-Prefix"].ToString();
                if (string.IsNullOrEmpty(effPrefix))
                {
                    var req = ctx.HttpContext.Request;
                    if (
                        req.PathBase.HasValue
                        && req.PathBase.Value!.StartsWith(
                            "/dashboard",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        effPrefix = "/dashboard";
                    }
                }
                if (!string.IsNullOrEmpty(effPrefix))
                {
                    var returnUri = ctx.ReturnUri;
                    string pathAndQuery;
                    if (string.IsNullOrWhiteSpace(returnUri))
                    {
                        var req = ctx.HttpContext.Request;
                        var pathBase = req.PathBase.HasValue ? req.PathBase.Value! : string.Empty;
                        var path = req.Path.HasValue ? req.Path.Value! : string.Empty;
                        var query = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;
                        pathAndQuery = string.Concat(pathBase, path, query);
                    }
                    else if (Uri.TryCreate(returnUri, UriKind.Absolute, out var abs))
                    {
                        pathAndQuery = abs.PathAndQuery;
                    }
                    else
                    {
                        pathAndQuery = returnUri;
                    }
                    if (!pathAndQuery.StartsWith(effPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var joiner = pathAndQuery.StartsWith('/') ? string.Empty : "/";
                        pathAndQuery =
                            effPrefix.TrimEnd('/') + joiner + pathAndQuery.TrimStart('/');
                    }
                    ctx.ReturnUri = new Uri(new Uri(publicBaseUrl), pathAndQuery).ToString();
                    logger.LogInformation(
                        "OIDC TicketReceived: ReturnUri normalized to {ReturnUri}",
                        ctx.ReturnUri
                    );
                }

                // Additionally, enrich the cookie principal with scopes/roles from the access token
                // at this late stage where tokens are guaranteed to be present in the auth properties.
                try
                {
                    var accessToken = ctx.Properties?.GetTokenValue("access_token");
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        var handler = new JwtSecurityTokenHandler();
                        var at = handler.ReadJwtToken(accessToken);

                        var identity =
                            ctx.Principal?.Identities.FirstOrDefault()
                            ?? new System.Security.Claims.ClaimsIdentity("oidc");

                        // Avoid duplicate additions: collect existing values
                        var existingScopes = new HashSet<string>(
                            identity.FindAll("scope").Select(c => c.Value),
                            StringComparer.OrdinalIgnoreCase
                        );
                        foreach (
                            var sc in at.Claims.Where(c =>
                                string.Equals(c.Type, "scope", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            var parts = sc.Value.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            );
                            foreach (var p in parts)
                            {
                                if (existingScopes.Add(p))
                                    identity.AddClaim(new System.Security.Claims.Claim("scope", p));
                            }
                        }

                        var existingScp = new HashSet<string>(
                            identity.FindAll("scp").Select(c => c.Value),
                            StringComparer.OrdinalIgnoreCase
                        );
                        foreach (
                            var scp in at.Claims.Where(c =>
                                string.Equals(c.Type, "scp", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            var parts = scp.Value.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            );
                            foreach (var p in parts)
                            {
                                if (existingScp.Add(p))
                                    identity.AddClaim(new System.Security.Claims.Claim("scp", p));
                            }
                        }

                        var existingRoles = new HashSet<string>(
                            identity
                                .FindAll(System.Security.Claims.ClaimTypes.Role)
                                .Select(c => c.Value),
                            StringComparer.OrdinalIgnoreCase
                        );
                        foreach (
                            var role in at.Claims.Where(c =>
                                string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            if (existingRoles.Add(role.Value))
                                identity.AddClaim(
                                    new System.Security.Claims.Claim(
                                        System.Security.Claims.ClaimTypes.Role,
                                        role.Value
                                    )
                                );
                        }
                        foreach (
                            var roles in at.Claims.Where(c =>
                                string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase)
                            )
                        )
                        {
                            if (existingRoles.Add(roles.Value))
                                identity.AddClaim(
                                    new System.Security.Claims.Claim(
                                        System.Security.Claims.ClaimTypes.Role,
                                        roles.Value
                                    )
                                );
                        }

                        if (ctx.Principal is null)
                        {
                            ctx.Principal = new System.Security.Claims.ClaimsPrincipal(identity);
                        }
                    }
                }
                catch
                {
                    // best-effort enrichment
                }
            }
            catch { }
            return Task.CompletedTask;
        }; // End of OnTicketReceived
        options.Events = oidcEvents; // End of OIDC events assignment

        // Removed dynamic resolver: keys are deterministically preloaded.
    }
);

authBuilder.AddJwtBearer(
    JwtBearerDefaults.AuthenticationScheme,
    options =>
    {
        if (builder.Environment.IsDevelopment())
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
        }

        options.IncludeErrorDetails = true;

        var configuredIssuer = builder.Configuration["Oidc:Issuer"];
        if (string.IsNullOrWhiteSpace(configuredIssuer))
        {
            configuredIssuer = builder.Configuration["Oidc:Authority"];
        }
        var issuerResolved = string.IsNullOrWhiteSpace(configuredIssuer)
            ? appUrlsOptions.GetIssuer("identity")
            : configuredIssuer.Trim();
        var issuerNoSlash = issuerResolved.TrimEnd('/');
        var issuerWithSlash = issuerNoSlash + "/";
        options.Authority = issuerNoSlash;

        var metadataOverrideBearer = builder.Configuration["Oidc:MetadataAddress"];
        var metadataAddress = string.IsNullOrWhiteSpace(metadataOverrideBearer)
            ? appUrlsOptions.GetBackchannelMetadataAddress(
                string.Equals(
                    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                    "true",
                    StringComparison.OrdinalIgnoreCase
                ),
                "identity"
            )
            : metadataOverrideBearer.Trim();
        options.MetadataAddress = metadataAddress;

        if (builder.Environment.IsDevelopment())
        {
            var docRetriever = new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever
            {
                RequireHttps = false
            };
            var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                options.MetadataAddress!,
                new OpenIdConnectConfigurationRetriever(),
                docRetriever
            )
            {
                AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                RefreshInterval = TimeSpan.FromMinutes(1)
            };
            options.ConfigurationManager = configManager;
            options.RefreshOnIssuerKeyNotFound = true;
        }

        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;

        string[] validIssuers;
        if (builder.Environment.IsDevelopment())
        {
            var altHostNoSlash = issuerNoSlash.Contains(
                "127.0.0.1",
                StringComparison.OrdinalIgnoreCase
            )
                ? issuerNoSlash.Replace(
                    "127.0.0.1",
                    "localhost",
                    StringComparison.OrdinalIgnoreCase
                )
                : issuerNoSlash.Replace(
                    "localhost",
                    "127.0.0.1",
                    StringComparison.OrdinalIgnoreCase
                );
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
            ValidateAudience = false,
            ValidTypes = new[] { "at+jwt", "JWT", "jwt" }
        };
    }
);

// PostConfigure to log final key count after all option modifications (verifies preload succeeded before handler construction)
builder.Services.PostConfigure<OpenIdConnectOptions>(
    "oidc",
    opts =>
    {
        try
        {
            var retriever = new HttpDocumentRetriever { RequireHttps = opts.RequireHttpsMetadata };

            var effectiveMetadata = opts.MetadataAddress;
            if (
                string.IsNullOrWhiteSpace(effectiveMetadata)
                && !string.IsNullOrWhiteSpace(opts.Authority)
            )
            {
                effectiveMetadata =
                    $"{opts.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            }

            if (!string.IsNullOrWhiteSpace(effectiveMetadata) && opts.ConfigurationManager is null)
            {
                var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    effectiveMetadata,
                    new OpenIdConnectConfigurationRetriever(),
                    retriever
                )
                {
                    AutomaticRefreshInterval = TimeSpan.FromHours(12),
                    RefreshInterval = TimeSpan.FromMinutes(5)
                };

                opts.ConfigurationManager = manager;
                opts.MetadataAddress = effectiveMetadata;
            }

            if (opts.Configuration is null && opts.ConfigurationManager is not null)
            {
                opts.Configuration = opts
                    .ConfigurationManager.GetConfigurationAsync(
                        System.Threading.CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult();
            }

            if (opts.Configuration is OpenIdConnectConfiguration cfg)
            {
                if (!string.IsNullOrWhiteSpace(gatewayBaseUrl))
                {
                    try
                    {
                        var gatewayBase = new Uri(gatewayBaseUrl.TrimEnd('/') + "/");
                        cfg.TokenEndpoint = new Uri(
                            gatewayBase,
                            "identity/connect/token"
                        ).AbsoluteUri;
                        cfg.UserInfoEndpoint = new Uri(
                            gatewayBase,
                            "identity/connect/userinfo"
                        ).AbsoluteUri;
                        cfg.IntrospectionEndpoint = new Uri(
                            gatewayBase,
                            "identity/connect/introspect"
                        ).AbsoluteUri;
                        cfg.JwksUri = new Uri(gatewayBase, "identity/.well-known/jwks").AbsoluteUri;
                    }
                    catch { }
                }

                if (cfg.SigningKeys.Count == 0 && !string.IsNullOrWhiteSpace(cfg.JwksUri))
                {
                    HttpClient? client = null;
                    var ownsClient = false;
                    try
                    {
                        client = opts.Backchannel ?? new HttpClient();
                        ownsClient = opts.Backchannel is null;
                        client.Timeout = TimeSpan.FromSeconds(5);
                        var jwksJson = client.GetStringAsync(cfg.JwksUri).GetAwaiter().GetResult();
                        var jwks = new JsonWebKeySet(jwksJson);
                        cfg.SigningKeys.Clear();
                        foreach (var key in DeduplicateSigningKeys(jwks.GetSigningKeys()))
                        {
                            cfg.SigningKeys.Add(key);
                        }
                    }
                    finally
                    {
                        if (ownsClient)
                        {
                            client?.Dispose();
                        }
                    }
                }

                if (cfg.SigningKeys.Count > 0)
                {
                    var deduped = DeduplicateSigningKeys(cfg.SigningKeys);
                    opts.TokenValidationParameters.IssuerSigningKeys = deduped;
                }

                if (
                    (
                        opts.TokenValidationParameters.ValidIssuers == null
                        || !opts.TokenValidationParameters.ValidIssuers.Any()
                    )
                    && (
                        !string.IsNullOrWhiteSpace(cfg.Issuer)
                        || !string.IsNullOrWhiteSpace(opts.Authority)
                    )
                )
                {
                    var issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(cfg.Issuer))
                    {
                        issuers.Add(cfg.Issuer.TrimEnd('/'));
                        issuers.Add(cfg.Issuer.TrimEnd('/') + "/");
                    }
                    if (!string.IsNullOrWhiteSpace(opts.Authority))
                    {
                        issuers.Add(opts.Authority.TrimEnd('/'));
                        issuers.Add(opts.Authority.TrimEnd('/') + "/");
                    }
                    opts.TokenValidationParameters.ValidIssuers = issuers;
                    opts.TokenValidationParameters.ValidateIssuer = true;
                }
            }

            var keyCount = opts.Configuration?.SigningKeys.Count ?? 0;
            var tvpKeyCount = opts.TokenValidationParameters.IssuerSigningKeys?.Count() ?? 0;
            var validIssuersDisplay = opts.TokenValidationParameters.ValidIssuers is null
                ? "<null>"
                : string.Join('|', opts.TokenValidationParameters.ValidIssuers);
            Console.WriteLine(
                $"[OidcPostConfigure] ConfigKeyCount={keyCount} TVPKeyCount={tvpKeyCount} ValidateIssuerSigningKey={opts.TokenValidationParameters.ValidateIssuerSigningKey}"
            );
            Console.WriteLine(
                $"[OidcPostConfigure] Authority={opts.Authority} Metadata={opts.MetadataAddress} ValidIssuers={validIssuersDisplay}"
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OidcPostConfigure] Exception {ex.GetType().Name}: {ex.Message}");
        }
    }
);

// Removed warm-up hosted service & post-configure: deterministic preload above handles key availability.

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "AdminOnly",
        policy =>
        {
            // Include OIDC scheme explicitly so that challenges can flow to Identity when needed.
            policy.AddAuthenticationSchemes(
                "oidc",
                CookieAuthenticationDefaults.AuthenticationScheme,
                JwtBearerDefaults.AuthenticationScheme
            );
            policy.RequireAssertion(ctx =>
            {
                try
                {
                    var user = ctx.User;
                    if (user == null)
                        return false;
                    if (!(user.Identity?.IsAuthenticated ?? false))
                        return false;
                    if (user.IsInRole("Admin"))
                        return true;
                    var scopes = user.FindAll("scope")
                        .Select(c => c.Value)
                        .Concat(user.FindAll("scp").Select(c => c.Value));
                    return scopes.Any(s =>
                        s.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            )
                            .Contains("admin.full", StringComparer.OrdinalIgnoreCase)
                    );
                }
                catch
                {
                    return false;
                }
            });
        }
    );
});

// HttpClient for server-side calls to backend via Gateway
builder.Services.AddTransient<TansuCloud.Dashboard.Security.BearerTokenHandler>();

// Register tenant context service for tenant management pages
builder.Services.AddScoped<
    TansuCloud.Dashboard.Services.ITenantContextService,
    TansuCloud.Dashboard.Services.TenantContextService
>();

builder
    .Services.AddHttpClient(
        "Gateway",
        client =>
        {
            client.BaseAddress = new Uri(appUrlsOptions.GatewayBaseUrl!, UriKind.Absolute);
        }
    )
    // Attach the access token from the OIDC sign-in when present
    .AddHttpMessageHandler<TansuCloud.Dashboard.Security.BearerTokenHandler>();

// Provide default HttpClient from the named one so @inject HttpClient works
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Gateway")
);

var app = builder.Build();

// Apply audit database migrations on startup (Task 31, EF-based)
await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions.ApplyAuditMigrationsAsync(
    app.Services,
    app.Logger
);

// Startup diagnostic: log OIDC metadata source choice (Task 38)
try
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OIDC-Config");
    var authorityConfigured = builder.Configuration["Oidc:Authority"];
    var authority = string.IsNullOrWhiteSpace(authorityConfigured)
        ? appUrlsOptions.GetAuthority("identity")
        : authorityConfigured.Trim().TrimEnd('/');
    var configuredMd = builder.Configuration["Oidc:MetadataAddress"];
    var inContainer = string.Equals(
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
        "true",
        StringComparison.OrdinalIgnoreCase
    );
    var effectiveMd = string.IsNullOrWhiteSpace(configuredMd)
        ? appUrlsOptions.GetBackchannelMetadataAddress(inContainer, "identity")
        : configuredMd.Trim();
    var src = string.IsNullOrWhiteSpace(configuredMd)
        ? (inContainer ? "container-gateway" : "authority-derived")
        : "explicit-config";
    log.LogOidcMetadataChoice(src, authority, effectiveMd);
}
catch { }

// Task 38 enrichment middleware
app.UseMiddleware<RequestEnrichmentMiddleware>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
else
{
    app.UseDeveloperExceptionPage();
}

// Respect proxy headers from the Gateway so Request.Scheme/Host reflect client
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
    ForwardLimit = null,
};

// In Development behind Docker bridge, clear the known proxies/networks so the headers are processed without warnings
if (app.Environment.IsDevelopment())
{
    fwd.KnownNetworks.Clear();
    fwd.KnownProxies.Clear();
}
app.UseForwardedHeaders(fwd);

// Honor X-Forwarded-Prefix so the app behaves as if hosted under that base path (e.g., "/dashboard")
app.Use(
    async (context, next) =>
    {
        var prefix = context.Request.Headers["X-Forwarded-Prefix"].ToString();
        // Avoid altering PathBase for API calls so Minimal API routes like "/api/*" match correctly.
        // UI endpoints still benefit from PathBase for correct base href/link generation under a proxy prefix.
        var isApiRequest = context.Request.Path.StartsWithSegments("/api", out _);
        if (!string.IsNullOrWhiteSpace(prefix) && !isApiRequest)
        {
            // Behave like UsePathBase only when the incoming path actually starts with the prefix.
            if (context.Request.Path.StartsWithSegments(prefix, out var rest))
            {
                context.Request.PathBase = prefix;
                context.Request.Path = rest;
            }
            else
            {
                // For non-API UI requests, setting PathBase helps Blazor generate correct base URIs behind the gateway.
                context.Request.PathBase = prefix;
            }
        }
        else if (
            !isApiRequest && context.Request.Path.StartsWithSegments("/dashboard", out var rest2)
        )
        {
            // Fallback inference: in case the proxy did not propagate X-Forwarded-Prefix, infer from the incoming path.
            // This keeps NavigationManager.BaseUri correct (".../dashboard/") and allows deep links to /dashboard/* to resolve.
            context.Request.PathBase = "/dashboard";
            context.Request.Path = rest2;
        }
        await next();
    }
);

// EARLY OIDC CONFIG/JWKS FORCE-LOAD (before static files & authentication)
// Purpose: eliminate timing race where first authorization redirect occurs before JWKS is loaded.
app.Use(
    async (context, next) =>
    {
        // Only attempt for unauthenticated interactive requests (avoid overhead for static assets and already signed-in users)
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            try
            {
                var sp = context.RequestServices;
                var monitor =
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>();
                var opts = monitor.Get("oidc");
                // Ensure ConfigurationManager exists
                if (
                    opts.ConfigurationManager == null
                    && !string.IsNullOrWhiteSpace(opts.MetadataAddress)
                )
                {
                    opts.ConfigurationManager =
                        new ConfigurationManager<OpenIdConnectConfiguration>(
                            opts.MetadataAddress,
                            new OpenIdConnectConfigurationRetriever(),
                            new HttpDocumentRetriever { RequireHttps = opts.RequireHttpsMetadata }
                        );
                }
                // Fetch configuration if not loaded or no keys
                if (
                    opts.ConfigurationManager != null
                    && (opts.Configuration == null || opts.Configuration.SigningKeys.Count == 0)
                )
                {
                    var cfg = await opts.ConfigurationManager.GetConfigurationAsync(
                        context.RequestAborted
                    );
                    opts.Configuration = cfg;
                }
                // Direct JWKS fetch if still no keys
                if (
                    opts.Configuration != null
                    && opts.Configuration.SigningKeys.Count == 0
                    && !string.IsNullOrWhiteSpace(opts.Configuration.JwksUri)
                )
                {
                    using var http = opts.Backchannel ?? new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(2);
                    // Rebase JWKS to gateway host if MetadataAddress is set (container-friendly)
                    var jwksUrl = opts.Configuration.JwksUri;
                    try
                    {
                        var md = new Uri(opts.MetadataAddress!);
                        var gatewayBase = new Uri(new Uri(md, "../").ToString().TrimEnd('/') + "/");
                        jwksUrl = new Uri(gatewayBase, ".well-known/jwks").AbsoluteUri;
                    }
                    catch { }
                    var jwksJson = await http.GetStringAsync(jwksUrl, context.RequestAborted);
                    var jwks = new JsonWebKeySet(jwksJson);
                    var dedupedKeys = DeduplicateSigningKeys(jwks.GetSigningKeys());
                    opts.Configuration.SigningKeys.Clear();
                    foreach (var key in dedupedKeys)
                    {
                        opts.Configuration.SigningKeys.Add(key);
                    }
                    opts.TokenValidationParameters.IssuerSigningKeys = dedupedKeys;
                }
                else if (opts.Configuration?.SigningKeys.Count > 0)
                {
                    var dedupedKeys = DeduplicateSigningKeys(opts.Configuration.SigningKeys);
                    if (dedupedKeys.Count != opts.Configuration.SigningKeys.Count)
                    {
                        opts.Configuration.SigningKeys.Clear();
                        foreach (var key in dedupedKeys)
                        {
                            opts.Configuration.SigningKeys.Add(key);
                        }
                    }
                    opts.TokenValidationParameters.IssuerSigningKeys = dedupedKeys;
                }
                // Console diagnostics (bypass logging filters)
                Console.WriteLine(
                    $"[EarlyOidcInit] Path={context.Request.Path} HasConfig={(opts.Configuration != null)} KeyCount={opts.Configuration?.SigningKeys.Count ?? 0} ValidateIssuerSigningKey={opts.TokenValidationParameters.ValidateIssuerSigningKey}"
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EarlyOidcInit] Exception: {ex.GetType().Name} {ex.Message}");
            }
        }
        await next();
    }
);

// Serve static files before auth so framework assets aren't gated
app.UseStaticFiles();

// Ensure WebSockets keep-alive pings are sent regularly for the Blazor circuit
app.UseWebSockets(
    new Microsoft.AspNetCore.Builder.WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(15)
    }
);

// Pre-auth key hydration middleware: if OIDC config lacks signing keys just before a challenge, fetch JWKS now.
app.Use(
    async (context, next) =>
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            try
            {
                var monitor =
                    context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>();
                var opts = monitor.Get("oidc");
                if (
                    opts.Configuration != null
                    && opts.Configuration.SigningKeys.Count == 0
                    && !string.IsNullOrWhiteSpace(opts.Configuration.JwksUri)
                )
                {
                    using var http = opts.Backchannel ?? new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(3);
                    var jwksUrl = opts.Configuration.JwksUri;
                    try
                    {
                        var md = new Uri(opts.MetadataAddress!);
                        var gatewayBase = new Uri(new Uri(md, "../").ToString().TrimEnd('/') + "/");
                        jwksUrl = new Uri(gatewayBase, ".well-known/jwks").AbsoluteUri;
                    }
                    catch { }
                    var jwksJson = await http.GetStringAsync(jwksUrl, context.RequestAborted);
                    var jwks = new JsonWebKeySet(jwksJson);
                    var dedupedKeys = DeduplicateSigningKeys(jwks.GetSigningKeys());
                    opts.Configuration.SigningKeys.Clear();
                    foreach (var key in dedupedKeys)
                    {
                        opts.Configuration.SigningKeys.Add(key);
                    }
                    opts.TokenValidationParameters.IssuerSigningKeys = dedupedKeys;
                }
                else if (opts.Configuration?.SigningKeys.Count > 0)
                {
                    var dedupedKeys = DeduplicateSigningKeys(opts.Configuration.SigningKeys);
                    if (dedupedKeys.Count != opts.Configuration.SigningKeys.Count)
                    {
                        opts.Configuration.SigningKeys.Clear();
                        foreach (var key in dedupedKeys)
                        {
                            opts.Configuration.SigningKeys.Add(key);
                        }
                    }
                    opts.TokenValidationParameters.IssuerSigningKeys = dedupedKeys;
                }
            }
            catch { }
        }
        await next();
    }
);

// Diagnostic middleware to capture OIDC callback context early (before auth handler executes)
app.Use(
    async (context, next) =>
    {
        if (context.Request.Path.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var logger = context
                    .RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC-Callback");
                var query = context.Request.Query.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToString()
                );
                var hasCode = query.ContainsKey("code");
                var hasIdToken = query.ContainsKey("id_token");
                var stateLen = query.TryGetValue("state", out var stateVal) ? stateVal.Length : 0;
                logger.LogInformation(
                    "/signin-oidc invoked. QueryKeys={Keys} HasCode={HasCode} HasIdToken={HasIdToken} StateLength={StateLength}",
                    string.Join(',', query.Keys),
                    hasCode,
                    hasIdToken,
                    stateLen
                );
                // Dump headers relevant to auth flow / proxying
                var headerSnapshot = context
                    .Request.Headers.Where(h =>
                        h.Key.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase)
                        || h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                        || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    )
                    .ToDictionary(h => h.Key, h => h.Value.ToString());
                logger.LogInformation(
                    "/signin-oidc headers snapshot: {Headers}",
                    System.Text.Json.JsonSerializer.Serialize(headerSnapshot)
                );

                // Correlation + nonce cookie presence diagnostics
                var corrCookies = new List<string>();
                foreach (var c in context.Request.Cookies.Keys)
                {
                    if (
                        c.Contains(
                            ".AspNetCore.Correlation.oidc",
                            StringComparison.OrdinalIgnoreCase
                        )
                        || c.Contains(
                            ".AspNetCore.OpenIdConnect.Nonce",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        corrCookies.Add(c);
                    }
                }
                logger.LogInformation(
                    "/signin-oidc correlation/nonce cookies: {Cookies}",
                    string.Join(',', corrCookies)
                );
            }
            catch
            { /* best effort */
            }
        }

        if (context.Request.Path.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                try
                {
                    var logger = context
                        .RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("OIDC-Callback");
                    logger.LogError(
                        ex,
                        "Unhandled exception in pipeline during /signin-oidc processing"
                    );
                    var diagnostics = new System.Text.StringBuilder();
                    diagnostics.AppendLine($"Timestamp: {DateTime.UtcNow:o}");
                    diagnostics.AppendLine("RequestPath=/signin-oidc");
                    diagnostics.AppendLine("Query=" + context.Request.QueryString);
                    diagnostics.AppendLine(
                        "Cookies=" + string.Join(';', context.Request.Cookies.Select(k => k.Key))
                    );
                    diagnostics.AppendLine("Exception=" + ex.ToString());
                    var file = Path.Combine(AppContext.BaseDirectory, "signin-oidc-error.log");
                    File.AppendAllText(file, diagnostics.ToString());
                }
                catch
                { /* ignore */
                }
                throw; // preserve original behavior (developer exception page)
            }
        }
        else
        {
            await next();
        }
    }
);

app.UseAuthentication();

// Proactive login challenge for admin UI routes (must run before UseAuthorization)
// Blazor Server with AuthorizeRouteView may render a NotAuthorized placeholder and gate framework assets,
// which doesn't always trigger a browser-visible redirect. For interactive HTML GETs under /admin we
// explicitly issue a challenge so the user is taken to the Identity login page and returns to the
// originally requested URL under the canonical /dashboard prefix.
app.Use(
    async (context, next) =>
    {
        try
        {
            var path = context.Request.Path;
            // Only consider interactive GET requests addressed to the Admin UI
            var isGet = HttpMethods.IsGet(context.Request.Method);
            // Challenge for all GETs under /admin (HTML and navigations). Rationale:
            // Playwright/real browsers may vary Accept headers and we observed cases where a strict
            // text/html check skipped the challenge, leaving a blank NotAuthorized page. We exclude
            // framework endpoints and APIs to avoid redirect loops and preserve API semantics.
            var isAdminUi =
                path.StartsWithSegments("/admin", out _)
                // exclude Admin/API endpoints which should return 401/403 instead of redirecting
                && !path.StartsWithSegments("/admin/api", out _)
                // exclude Blazor framework and hub endpoints to avoid challenge loops
                && !path.StartsWithSegments("/_framework", out _)
                && !path.StartsWithSegments("/_blazor", out _);

            if (isGet && isAdminUi)
            {
                var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;
                if (!isAuthenticated)
                {
                    try
                    {
                        var logger = context
                            .RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OIDC-RedirectDiag");
                        logger.LogInformation(
                            "Proactive admin challenge: issuing OIDC challenge for {Path}",
                            path
                        );
                    }
                    catch { }
                    // Explicitly challenge the OIDC handler to guarantee a browser redirect to the Identity login UI.
                    await context.ChallengeAsync("oidc");
                    return; // short-circuit; OIDC middleware will redirect to login
                }
            }
        }
        catch
        {
            // best effort; never block the pipeline due to diagnostics
        }

        await next();
    }
); // End of Middleware Proactive Admin UI Challenge

app.UseAuthorization();

// Diagnostics: log auth state & claims for metrics page to investigate repeated challenges/timeouts
app.Use(
    async (context, next) =>
    {
        if (
            context.Request.Path.StartsWithSegments(
                "/admin/metrics",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            try
            {
                var logger = context
                    .RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MetricsAuthDiag");
                var user = context.User;
                var isAuth = user?.Identity?.IsAuthenticated ?? false;
                var claims =
                    user?.Claims?.Select(c => c.Type + "=" + c.Value)
                        .Take(50) // avoid log bloat
                        .ToArray() ?? Array.Empty<string>();
                logger.LogInformation(
                    "/admin/metrics auth probe IsAuthenticated={Auth} Claims={Claims}",
                    isAuth,
                    string.Join(";", claims)
                );
            }
            catch
            { /* best effort */
            }
        }
        await next();
    }
);

app.UseAntiforgery();

// Static assets must be allowed anonymously; the app has a global fallback authorization policy
// and we don't want to gate framework files like blazor.server.js or CSS under auth.
app.MapStaticAssets().AllowAnonymous();

// --------------------
// API endpoints (map BEFORE Razor components so Blazor's catch-all doesn't shadow /api/*)
// --------------------
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

// Dev-only: expose dynamic logging override shim for Dashboard logs too
if (app.Environment.IsDevelopment())
{
    app.MapPost(
            "/dev/logging/overrides",
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

void MapMetricsApi(string prefix)
{
    var group = app.MapGroup(prefix).RequireAuthorization("AdminOnly");

    group.MapGet(
        "/catalog",
        (SigNozMetricsCatalog catalog, HttpContext http, IAuditLogger audit) =>
        {
            var catalogResponse = catalog.CreateCatalog();
            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "MetricsCatalogRead",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new { Count = catalogResponse.Charts.Count },
                    new[] { "Count" }
                );
            }
            catch { }
            return Results.Json(catalogResponse);
        }
    );

    group.MapGet(
        "/range",
        (
            string chartId,
            int? rangeMinutes,
            int? stepSeconds,
            string? tenant,
            string? service,
            SigNozMetricsCatalog catalog,
            HttpContext http,
            IAuditLogger audit
        ) =>
        {
            if (!catalog.TryResolve(chartId, out var redirect))
            {
                try
                {
                    var failure = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "MetricsRangeQuery",
                        Outcome = "Failure",
                        ReasonCode = "UnknownChart",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        failure,
                        new { ChartId = chartId ?? string.Empty },
                        new[] { "ChartId" }
                    );
                }
                catch { }

                return Results.Problem(
                    title: "Unknown chartId",
                    detail: $"The chart '{chartId}' is not registered in the SigNoz catalog.",
                    statusCode: 400
                );
            }

            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "MetricsRangeQuery",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new
                    {
                        ChartId = redirect.ChartId,
                        RangeMinutes = rangeMinutes ?? 0,
                        StepSeconds = stepSeconds ?? 0,
                        Tenant = tenant ?? string.Empty,
                        Service = service ?? string.Empty
                    },
                    new[] { "ChartId", "RangeMinutes", "StepSeconds", "Tenant", "Service" }
                );
            }
            catch { }

            var response = new
            {
                chartId = redirect.ChartId,
                source = "SigNoz",
                sigNozUrl = redirect.SigNozUrl,
                title = redirect.Title,
                description = redirect.Description
            };
            return Results.Json(response);
        }
    );

    group.MapGet(
        "/instant",
        (
            string chartId,
            string? at,
            string? tenant,
            string? service,
            SigNozMetricsCatalog catalog,
            HttpContext http,
            IAuditLogger audit
        ) =>
        {
            if (!catalog.TryResolve(chartId, out var redirect))
            {
                try
                {
                    var failure = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "MetricsInstantQuery",
                        Outcome = "Failure",
                        ReasonCode = "UnknownChart",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueueRedacted(
                        failure,
                        new { ChartId = chartId ?? string.Empty },
                        new[] { "ChartId" }
                    );
                }
                catch { }

                return Results.Problem(
                    title: "Unknown chartId",
                    detail: $"The chart '{chartId}' is not registered in the SigNoz catalog.",
                    statusCode: 400
                );
            }

            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "MetricsInstantQuery",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new
                    {
                        ChartId = redirect.ChartId,
                        At = at ?? string.Empty,
                        Tenant = tenant ?? string.Empty,
                        Service = service ?? string.Empty
                    },
                    new[] { "ChartId", "At", "Tenant", "Service" }
                );
            }
            catch { }

            var response = new
            {
                chartId = redirect.ChartId,
                source = "SigNoz",
                sigNozUrl = redirect.SigNozUrl,
                title = redirect.Title,
                description = redirect.Description
            };
            return Results.Json(response);
        }
    );
}

MapMetricsApi("/dashboard/api/metrics");
MapMetricsApi("/api/metrics");

// Admin API: Observability Governance Configuration
app.MapGet(
        "/api/admin/observability/governance",
        async (IWebHostEnvironment env, IAuditLogger audit, HttpContext http) =>
        {
            try
            {
                // Read governance.defaults.json from SigNoz folder
                var governanceFilePath = Path.Combine(
                    env.ContentRootPath,
                    "..",
                    "SigNoz",
                    "governance.defaults.json"
                );

                if (!File.Exists(governanceFilePath))
                {
                    return Results.Problem(
                        title: "Configuration file not found",
                        detail: "The governance.defaults.json file was not found. Ensure the file exists at SigNoz/governance.defaults.json.",
                        statusCode: StatusCodes.Status404NotFound
                    );
                }

                var jsonContent = await File.ReadAllTextAsync(governanceFilePath);
                var config =
                    System.Text.Json.JsonSerializer.Deserialize<TansuCloud.Dashboard.Models.ObservabilityGovernanceConfig>(
                        jsonContent,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    );

                if (config == null)
                {
                    return Results.Problem(
                        title: "Invalid configuration",
                        detail: "Failed to parse governance.defaults.json",
                        statusCode: StatusCodes.Status500InternalServerError
                    );
                }

                // Audit read
                try
                {
                    var ev = new TansuCloud.Observability.Auditing.AuditEvent
                    {
                        Category = "Admin",
                        Action = "ObservabilityGovernanceRead",
                        Outcome = "Success",
                        Subject = http.User?.Identity?.Name ?? "anonymous",
                        CorrelationId = http.TraceIdentifier
                    };
                    audit.TryEnqueue(ev);
                }
                catch { }

                return Results.Json(config);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Error reading configuration",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        }
    )
    .RequireAuthorization("AdminOnly")
    .WithDisplayName("Admin: Get observability governance config");

app.MapPost(
        "/api/admin/observability/governance",
        (HttpContext http) =>
        {
            return Results.Problem(
                title: "Not implemented",
                detail: "Observability governance updates must be applied via the governance.defaults.json file and the 'SigNoz: governance (apply)' VS Code task. See Guide-For-Admins-and-Tenants.md section 6.5 for details.",
                statusCode: StatusCodes.Status501NotImplemented
            );
        }
    )
    .RequireAuthorization("AdminOnly")
    .WithDisplayName("Admin: Update observability governance (not implemented)");

// Admin-only log reporting status/toggle API
app.MapGet(
        "/api/admin/log-reporting/status",
        (
            Microsoft.Extensions.Options.IOptionsMonitor<TansuCloud.Dashboard.Observability.Logging.LogReportingOptions> monitor,
            Microsoft.Extensions.Options.IOptionsMonitor<LoggingReportOptions> capture,
            ILogReportingRuntimeSwitch runtime,
            ILogBuffer buffer,
            HttpContext http,
            IAuditLogger audit
        ) =>
        {
            var opts = monitor.CurrentValue;
            var captureOpts = capture.CurrentValue;
            var effective = (opts.Enabled && runtime.Enabled);
            // Audit read of status (admin)
            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "LogReportingStatusRead",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev, new { Effective = effective }, new[] { "Effective" });
            }
            catch { }
            return Results.Json(
                new
                {
                    configured = opts.Enabled,
                    runtime = runtime.Enabled,
                    effective,
                    reportIntervalMinutes = opts.ReportIntervalMinutes,
                    mainServerUrl = opts.MainServerUrl,
                    severityThreshold = opts.SeverityThreshold,
                    queryWindowMinutes = opts.QueryWindowMinutes,
                    maxItems = opts.MaxItems,
                    httpTimeoutSeconds = opts.HttpTimeoutSeconds,
                    warningSamplingPercent = opts.WarningSamplingPercent,
                    allowedWarningCategories = opts.AllowedWarningCategories,
                    pseudonymizeTenants = opts.PseudonymizeTenants,
                    tenantHashSecretConfigured = !string.IsNullOrWhiteSpace(opts.TenantHashSecret),
                    reportKinds = new[]
                    {
                        "critical",
                        "error",
                        "warning (allowlisted / sampled)",
                        "perf_slo_breach",
                        "telemetry_internal"
                    },
                    capture = new
                    {
                        captureOpts.Enabled,
                        MinimumLevel = captureOpts.MinimumLevel.ToString(),
                        captureOpts.MaxBufferEntries,
                        captureOpts.MaxBatchSize,
                        captureOpts.ReportInterval
                    },
                    buffer = new { buffer.Capacity, buffer.Count }
                }
            );
        }
    )
    .RequireAuthorization("AdminOnly");

app.MapPost(
        "/api/admin/log-reporting/toggle",
        (
            [FromBody] TansuCloud.Dashboard.ToggleRequest body,
            ILogReportingRuntimeSwitch runtime,
            HttpContext http,
            IAuditLogger audit
        ) =>
        {
            runtime.Enabled = body.Enabled;
            // Audit toggle
            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "LogReportingToggle",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev, new { body.Enabled }, new[] { "Enabled" });
            }
            catch { }
            return Results.NoContent();
        }
    )
    .RequireAuthorization("AdminOnly");

app.MapPost(
        "/api/admin/log-reporting/test",
        async (
            ILogReporter reporter,
            Microsoft.Extensions.Options.IOptionsMonitor<TansuCloud.Dashboard.Observability.Logging.LogReportingOptions> monitor,
            HttpContext http,
            IAuditLogger audit,
            CancellationToken cancellationToken
        ) =>
        {
            var opts = monitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(opts.MainServerUrl))
            {
                return Results.Problem(
                    title: "Reporting endpoint not configured",
                    detail: "Set LogReporting:MainServerUrl (or LOGREPORTING__MAINSERVERURL) before sending a test report.",
                    statusCode: 400
                );
            }

            var environment =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            var timestamp = DateTimeOffset.UtcNow;
            var templateHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("tansu.dashboard.telemetry.heartbeat"))
            );
            var testItem = new LogItem(
                Kind: "diagnostic_heartbeat",
                Timestamp: timestamp.ToString("o"),
                Level: "Information",
                Message: "Dashboard telemetry heartbeat",
                TemplateHash: templateHash,
                Exception: null,
                Service: "tansu.dashboard",
                Environment: environment,
                TenantHash: null,
                CorrelationId: http.TraceIdentifier,
                TraceId: Activity.Current?.TraceId.ToString(),
                SpanId: Activity.Current?.SpanId.ToString(),
                Category: "Tansu.Telemetry",
                EventId: TansuCloud.Observability.LogEvents.TelemetryBatchSend.Id,
                Count: 1,
                Properties: new
                {
                    Source = "AdminTestReport",
                    SentBy = http.User?.Identity?.Name ?? "anonymous",
                    SentAtUtc = timestamp
                }
            );

            var request = new LogReportRequest(
                Host: Environment.MachineName,
                Environment: environment,
                Service: "tansu.dashboard",
                SeverityThreshold: opts.SeverityThreshold,
                WindowMinutes: 0,
                MaxItems: 1,
                Items: new[] { testItem }
            );

            await reporter.ReportAsync(request, cancellationToken).ConfigureAwait(false);

            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "LogReportingTestSend",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new { Target = opts.MainServerUrl ?? string.Empty },
                    new[] { "Target" }
                );
            }
            catch { }

            return Results.Ok(
                new
                {
                    sent = true,
                    target = opts.MainServerUrl,
                    kind = "diagnostic_heartbeat"
                }
            );
        }
    )
    .RequireAuthorization("AdminOnly");

// Admin API: recent logs snapshot (paged basic)
app.MapGet(
        "/api/admin/logs/recent",
        (
            ILogBuffer buffer,
            int? take,
            string? level,
            string? categoryContains,
            int? skip,
            HttpContext http,
            IAuditLogger audit
        ) =>
        {
            var items = buffer.Snapshot().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(level))
            {
                items = items.Where(i =>
                    string.Equals(i.Level, level, StringComparison.OrdinalIgnoreCase)
                );
            }
            if (!string.IsNullOrWhiteSpace(categoryContains))
            {
                items = items.Where(i =>
                    (i.Category ?? string.Empty).Contains(
                        categoryContains,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            }
            var t = Math.Max(1, take ?? 200);
            var s = Math.Max(0, skip ?? 0);
            var list = items.Skip(s).Take(t).ToList();
            // Audit read (admin)
            try
            {
                var ev = new TansuCloud.Observability.Auditing.AuditEvent
                {
                    Category = "Admin",
                    Action = "LogsRecentRead",
                    Outcome = "Success",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(
                    ev,
                    new
                    {
                        Take = t,
                        Skip = s,
                        Level = level ?? string.Empty,
                        CategoryContains = categoryContains ?? string.Empty
                    },
                    new[] { "Take", "Skip", "Level", "CategoryContains" }
                );
            }
            catch { }
            return Results.Json(list);
        }
    )
    .RequireAuthorization("AdminOnly");

// --------------------
// UI endpoints (Razor components)
// --------------------
// Map Razor components with global authorization but explicitly allow Blazor infrastructure endpoints
// to avoid OIDC challenges on framework/negotiate requests when not yet authenticated (per .NET 8/9 guidance).
app.MapRazorComponents<App>()
    .RequireAuthorization()
    .AddInteractiveServerRenderMode()
    .Add(e =>
    {
        if (e is RouteEndpointBuilder reb && reb.RoutePattern.RawText is string raw)
        {
            // Allow anonymous for Blazor framework assets served via endpoint routing in .NET 8/9
            // Includes blazor.web.js, blazor.server.js and other resources under /_framework/
            if (raw.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase))
            {
                e.Metadata.Add(new AllowAnonymousAttribute());
            }

            // Allow anonymous for ALL Blazor hub endpoints to prevent challenge loops during reconnects
            // Covers negotiate, WebSocket, and long-poll under "/_blazor"
            if (raw.Contains("/_blazor", StringComparison.OrdinalIgnoreCase))
            {
                e.Metadata.Add(new AllowAnonymousAttribute());
            }
        }

        // Optional stability: close circuit when authentication expires (helps recover cleanly)
        var dispatcherOptions = e
            .Metadata.OfType<HttpConnectionDispatcherOptions>()
            .FirstOrDefault();
        if (dispatcherOptions is not null)
        {
            dispatcherOptions.CloseOnAuthenticationExpiration = true;
        }
    });

// Development-only OIDC diagnostics endpoint exposing current client config & signing keys (not for production)
if (app.Environment.IsDevelopment())
{
    app.MapGet(
            "/diag/oidc",
            (IServiceProvider sp) =>
            {
                var monitor =
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>();
                var opts = monitor.Get("oidc");
                var cfg = opts.Configuration;
                var keys = new List<object>();
                if (cfg?.SigningKeys is not null)
                {
                    foreach (var k in cfg.SigningKeys)
                    {
                        keys.Add(new { k.KeyId, Type = k.GetType().Name });
                    }
                }
                return Results.Json(
                    new
                    {
                        opts.Authority,
                        opts.MetadataAddress,
                        RequireHttps = opts.RequireHttpsMetadata,
                        ConfigLoaded = cfg is not null,
                        Issuer = cfg?.Issuer,
                        KeyCount = cfg?.SigningKeys.Count ?? 0,
                        Keys = keys,
                        ValidIssuers = opts.TokenValidationParameters.ValidIssuers,
                        TvpKidList = (
                            opts.TokenValidationParameters.IssuerSigningKeys
                            ?? Enumerable.Empty<SecurityKey>()
                        )
                            .OfType<JsonWebKey>()
                            .Select(k => k.Kid ?? k.KeyId)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToArray()
                    }
                );
            }
        )
        .AllowAnonymous();
}

app.Run();

public sealed record LogOverrideRequest(string Category, LogLevel Level, int TtlSeconds);

// Support types (kept at end to avoid interfering with top-level statements above)
namespace TansuCloud.Dashboard
{
    // Deterministic static configuration manager to avoid races once discovery & JWKS are loaded at startup.
    sealed class StaticConfigurationManager<T> : IConfigurationManager<T>
        where T : class
    {
        private readonly T _config;

        public StaticConfigurationManager(T config) => _config = config; // End of Constructor StaticConfigurationManager

        public Task<T> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_config); // End of Method GetConfigurationAsync

        public void RequestRefresh() { } // End of Method RequestRefresh
    } // End of Class StaticConfigurationManager
} // End of Namespace TansuCloud.Dashboard
