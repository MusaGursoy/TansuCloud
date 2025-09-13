// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.IdentityModel.Tokens.Jwt; // For token inspection logging
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols; // For ConfigurationManager
using Microsoft.IdentityModel.Protocols.OpenIdConnect; // For OpenIdConnectConfigurationRetriever
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Dashboard.Components;
using TansuCloud.Dashboard.Hosting;
using TansuCloud.Dashboard.Observability;

var builder = WebApplication.CreateBuilder(args);

// Resolve the public base URL (what browsers should see). Prefer explicit PublicBaseUrl, then GatewayBaseUrl, then localhost.
var publicBaseUrl =
    builder.Configuration["PublicBaseUrl"]
    ?? builder.Configuration["GatewayBaseUrl"]
    // Fallback defaults to IPv4 loopback to avoid localhost/::1 vs 127.0.0.1 divergence seen in tests and Identity issuer canonicalization.
    ?? "http://127.0.0.1:8080";
if (!publicBaseUrl.EndsWith('/'))
{
    publicBaseUrl += "/";
}
var publicBaseUri = new Uri(publicBaseUrl);

// Development canonicalization: previously we rewrote localhost -> 127.0.0.1.
// This caused issuer/authority divergence when other services emitted tokens using the original host.
// To preserve parity and avoid subtle mismatches, we now opt-out by default. Enable only if explicitly requested.
var canonicalizeLoopback = Environment.GetEnvironmentVariable("DASHBOARD_CANONICALIZE_LOOPBACK");
if (
    builder.Environment.IsDevelopment()
    && string.Equals(canonicalizeLoopback, "1", StringComparison.OrdinalIgnoreCase)
    && publicBaseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
)
{
    var b = new UriBuilder(publicBaseUri) { Host = "127.0.0.1" };
    publicBaseUri = b.Uri; // End of canonicalization adjustment
    publicBaseUrl = publicBaseUri.ToString();
}

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

// Elevate our custom OIDC diagnostic categories
builder.Logging.AddFilter("OIDC-Diagnostics", LogLevel.Information);
builder.Logging.AddFilter("OIDC-Callback", LogLevel.Information);
builder.Services.AddHealthChecks();

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

// Observability: Prometheus options + HTTP client and query service
builder.Services.Configure<PrometheusOptions>(builder.Configuration.GetSection("Prometheus"));
builder.Services.AddHttpClient("prometheus");
builder.Services.AddSingleton<IPrometheusQueryService, PrometheusQueryService>();

// OIDC sign-in (simplified deterministic preload)
builder
    .Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Cookies";
        options.DefaultChallengeScheme = "oidc";
    })
    .AddCookie(
        "Cookies",
        o =>
        {
            o.Cookie.Path = "/";
            // In Development over HTTP, SameSite=None without Secure is rejected by modern browsers.
            // Use Lax so cookies flow on top-level GET redirects (OIDC callback) while remaining safe for subrequests.
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.None;
        }
    )
    .AddOpenIdConnect(
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
            options.Authority =
                builder.Configuration["Oidc:Authority"]
                ?? new Uri(publicBaseUri, "identity").ToString().TrimEnd('/');
            // Explicitly set discovery document URL to avoid any authority/path-base ambiguity behind the gateway
            options.MetadataAddress =
                builder.Configuration["Oidc:MetadataAddress"]
                ?? ($"{options.Authority!.TrimEnd('/')}/.well-known/openid-configuration");
            options.ClientId = builder.Configuration["Oidc:ClientId"] ?? "tansu-dashboard";
            options.ClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? "dev-secret";
            options.ResponseType = "code";
            // Use query response mode to ensure callback is a top-level GET navigation, which works with Lax cookies in dev HTTP
            options.ResponseMode = "query";
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
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

            // Optional DEV-only bypass of signature validation for isolation: DASHBOARD_BYPASS_IDTOKEN_SIGNATURE=1
            var bypassSig = Environment.GetEnvironmentVariable(
                "DASHBOARD_BYPASS_IDTOKEN_SIGNATURE"
            );
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
            try
            {
                var metadataAddress = options.MetadataAddress;
                if (string.IsNullOrWhiteSpace(metadataAddress))
                    throw new InvalidOperationException("MetadataAddress missing");
                Console.WriteLine(
                    $"[OidcPreload] Beginning preload Authority={options.Authority} MetadataAddress={metadataAddress}"
                );
                var docRetriever = new HttpDocumentRetriever
                {
                    RequireHttps = options.RequireHttpsMetadata
                };
                var cfgManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    metadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    docRetriever
                );
                // Retrieve configuration deterministically (synchronous wait acceptable at startup)
                var cfg = cfgManager
                    .GetConfigurationAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                // Force public browser-visible endpoints (host 127.0.0.1, under /identity) regardless of backchannel MetadataAddress
                try
                {
                    // Separate front-channel (browser) vs backchannel (server) endpoints.
                    // Front-channel should use public host (127.0.0.1) so browser never sees 'gateway'.
                    // Backchannel must use in-cluster host derived from MetadataAddress (gateway:8080/identity).
                    var publicRoot = new Uri(publicBaseUrl);
                    cfg.AuthorizationEndpoint = new Uri(
                        publicRoot,
                        "connect/authorize"
                    ).AbsoluteUri;

                    // Derive gateway base from MetadataAddress: http://gateway:8080/identity/.well-known/openid-configuration
                    // -> base http://gateway:8080/identity/
                    try
                    {
                        var md = new Uri(options.MetadataAddress!);
                        // one level up from .well-known -> identity/
                        var gatewayBase = new Uri(md, "../");
                        // Normalize to ensure trailing slash
                        var gatewayBaseStr = gatewayBase.ToString();
                        if (!gatewayBaseStr.EndsWith('/'))
                        {
                            gatewayBaseStr += "/";
                        }
                        var gatewayBaseUri = new Uri(gatewayBaseStr);
                        // Backchannel endpoints: token/userinfo/introspection
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
                        // Rebase JWKS to gateway host so the container can fetch keys
                        cfg.JwksUri = new Uri(gatewayBaseUri, ".well-known/jwks").AbsoluteUri;
                    }
                    catch { }

                    // Optional: end-session for browser sign-out
                    if (!string.IsNullOrWhiteSpace(cfg.EndSessionEndpoint))
                    {
                        cfg.EndSessionEndpoint = new Uri(
                            publicRoot,
                            "connect/endsession"
                        ).AbsoluteUri;
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
                        cfg.SigningKeys.Add(k);
                }
                options.Configuration = cfg;
                // Decide whether to freeze configuration: default (no) to allow key rotation (common under OpenIddict dev keys).
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
                            $"[OidcPreload] Loaded Issuer={cfg.Issuer} KeyCount={cfg.SigningKeys.Count} (static)"
                        );
                    }
                    else
                    {
                        options.ConfigurationManager = cfgManager; // dynamic for refresh on rotation
                        Console.WriteLine(
                            $"[OidcPreload] Loaded Issuer={cfg.Issuer} KeyCount={cfg.SigningKeys.Count} (dynamic)"
                        );
                    }
                }
                else
                {
                    options.ConfigurationManager = cfgManager; // allow later refresh to pull keys when they appear
                    Console.WriteLine(
                        $"[OidcPreload] Loaded Issuer={cfg.Issuer} KeyCount=0 (dynamic manager retained)"
                    );
                }
                // Construct a permissive ValidIssuers set to absorb minor host/trailing slash differences in dev.
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
                        // Ensure issuer validation is on (unless explicitly disabled earlier) now that we have a set.
                        if (!options.TokenValidationParameters.ValidateIssuer)
                        {
                            options.TokenValidationParameters.ValidateIssuer = true;
                        }
                        Console.WriteLine(
                            $"[OidcPreload] ValidIssuers={string.Join(',', issuers)}"
                        );
                    }
                }
                catch { }
                // Fallback resolver: always attempt to use whatever keys are present in TVP or configuration at validation time.
                options.TokenValidationParameters.IssuerSigningKeyResolver = (
                    token,
                    securityToken,
                    kid,
                    validationParameters
                ) =>
                {
                    // Prefer explicitly assigned IssuerSigningKeys, then configuration keys.
                    var explicitKeys = validationParameters.IssuerSigningKeys;
                    if (explicitKeys != null && explicitKeys.Any())
                        return explicitKeys;
                    var cfgCurrent = options.Configuration; // may have been updated dynamically
                    if (cfgCurrent?.SigningKeys != null && cfgCurrent.SigningKeys.Count > 0)
                    {
                        return cfgCurrent.SigningKeys;
                    }
                    return Enumerable.Empty<SecurityKey>();
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OidcPreload] FAILED {ex.GetType().Name}: {ex.Message}");
            }

            // Minimal events: adjust redirect URI behind prefix and log token validation summary
            var oidcEvents = new OpenIdConnectEvents();
            oidcEvents.OnMessageReceived = ctx =>
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
                    logger.LogInformation(
                        "OIDC message received. Path={Path} KeyCount={KeyCount} HasConfig={HasConfig}",
                        ctx.Request.Path,
                        keyCount,
                        optsDiag.Configuration != null
                    );
                }
                catch { }
                return Task.CompletedTask;
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
                        logger.LogInformation(
                            "OIDC token response received. IdTokenHeader Kid={Kid} Alg={Alg}",
                            kid,
                            alg
                        );
                    }
                }
                catch { }
                return Task.CompletedTask;
            }; // End of OnTokenResponseReceived
            oidcEvents.OnRedirectToIdentityProvider = ctx =>
            {
                // Ensure browser-visible RedirectUri honors the dashboard prefix.
                // Prefer X-Forwarded-Prefix from the gateway; fallback to PathBase (behaves like UsePathBase)
                // and only then infer from the incoming request path.
                var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
                if (string.IsNullOrEmpty(prefix))
                {
                    var pb = ctx.Request.PathBase.HasValue
                        ? ctx.Request.PathBase.Value!
                        : string.Empty;
                    if (
                        !string.IsNullOrEmpty(pb)
                        && pb.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        prefix = "/dashboard";
                    }
                    else if (
                        ctx.Request.Path.HasValue
                        && ctx.Request.Path.StartsWithSegments("/dashboard")
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
                                propsSet3.RedirectUri = new Uri(
                                    publicBaseUri,
                                    pathAndQuery
                                ).ToString();
                            }
                        }
                    }
                }
                catch { }
                return Task.CompletedTask;
            }; // End of OnRedirectToIdentityProvider
            oidcEvents.OnRedirectToIdentityProviderForSignOut = ctx =>
            {
                var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
                if (string.IsNullOrEmpty(prefix))
                {
                    var pb = ctx.Request.PathBase.HasValue
                        ? ctx.Request.PathBase.Value!
                        : string.Empty;
                    if (
                        !string.IsNullOrEmpty(pb)
                        && pb.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        prefix = "/dashboard";
                    }
                    else if (
                        ctx.Request.Path.HasValue
                        && ctx.Request.Path.StartsWithSegments("/dashboard")
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
                    ctx.ProtocolMessage.PostLogoutRedirectUri = new Uri(
                        publicBaseUri,
                        full
                    ).ToString();
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

                    // Normalize the post-authentication RedirectUri to include the dashboard prefix when hosted behind the gateway.
                    // In some flows, the saved RedirectUri is a root-anchored path like "/admin/metrics" without the "/dashboard" prefix,
                    // which leads to a 404 at the gateway. Force-prefix it when needed and build an absolute public URL.
                    // Prefer our stashed canonical desired URL, if present, and do not override it.
                    if (
                        ctx.Properties?.Items is not null
                        && ctx.Properties.Items.TryGetValue(
                            "tansu.returnUri",
                            out var stashedDesired
                        )
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
                    var effPrefix = ctx
                        .HttpContext.Request.Headers["X-Forwarded-Prefix"]
                        .ToString();
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
                            var pathBase = req.PathBase.HasValue
                                ? req.PathBase.Value!
                                : string.Empty;
                            var path = req.Path.HasValue ? req.Path.Value! : string.Empty;
                            var query = req.QueryString.HasValue
                                ? req.QueryString.Value
                                : string.Empty;
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
                    var effPrefix = ctx
                        .HttpContext.Request.Headers["X-Forwarded-Prefix"]
                        .ToString();
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
                            var pathBase = req.PathBase.HasValue
                                ? req.PathBase.Value!
                                : string.Empty;
                            var path = req.Path.HasValue ? req.Path.Value! : string.Empty;
                            var query = req.QueryString.HasValue
                                ? req.QueryString.Value
                                : string.Empty;
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
                }
                catch { }
                return Task.CompletedTask;
            }; // End of OnTicketReceived
            options.Events = oidcEvents; // End of OIDC events assignment

            // Removed dynamic resolver: keys are deterministically preloaded.
        }
    );

// PostConfigure to log final key count after all option modifications (verifies preload succeeded before handler construction)
builder.Services.PostConfigure<OpenIdConnectOptions>(
    "oidc",
    opts =>
    {
        try
        {
            var keyCount = opts.Configuration?.SigningKeys.Count ?? 0;
            var tvpKeyCount = opts.TokenValidationParameters.IssuerSigningKeys?.Count() ?? 0;
            Console.WriteLine(
                $"[OidcPostConfigure] ConfigKeyCount={keyCount} TVPKeyCount={tvpKeyCount} ValidateIssuerSigningKey={opts.TokenValidationParameters.ValidateIssuerSigningKey}"
            );
            Console.WriteLine(
                $"[OidcPostConfigure] Authority={opts.Authority} Metadata={opts.MetadataAddress} ValidIssuers={(opts.TokenValidationParameters.ValidIssuers == null ? "<null>" : string.Join('|', opts.TokenValidationParameters.ValidIssuers))}"
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OidcPostConfigure] Exception {ex.GetType().Name}: {ex.Message}");
        }
    }
);

// Removed warm-up hosted service & post-configure: deterministic preload above handles key availability.

builder.Services.AddAuthorization();

// HttpClient for server-side calls to backend via Gateway
builder.Services.AddTransient<TansuCloud.Dashboard.Security.BearerTokenHandler>();
builder
    .Services.AddHttpClient(
        "Gateway",
        client =>
        {
            var baseUrl = builder.Configuration["GatewayBaseUrl"] ?? "http://localhost:5299";
            client.BaseAddress = new Uri(baseUrl);
        }
    )
    // Attach the access token from the OIDC sign-in when present
    .AddHttpMessageHandler<TansuCloud.Dashboard.Security.BearerTokenHandler>();

// Provide default HttpClient from the named one so @inject HttpClient works
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Gateway")
);

var app = builder.Build();

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
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            // Behave like UsePathBase: if the path starts with the prefix, move it to PathBase and trim from Path.
            if (context.Request.Path.StartsWithSegments(prefix, out var rest))
            {
                context.Request.PathBase = prefix;
                context.Request.Path = rest;
            }
            else
            {
                // Otherwise, at least set PathBase so link generation/base URI are correct under a proxy path.
                context.Request.PathBase = prefix;
            }
        }
        else if (context.Request.Path.StartsWithSegments("/dashboard", out var rest2))
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
                    foreach (var k in jwks.GetSigningKeys())
                    {
                        opts.Configuration.SigningKeys.Add(k);
                    }
                }
                // Inject into TokenValidationParameters if empty
                if (
                    opts.Configuration?.SigningKeys.Count > 0
                    && (
                        opts.TokenValidationParameters.IssuerSigningKeys == null
                        || !opts.TokenValidationParameters.IssuerSigningKeys.Any()
                    )
                )
                {
                    opts.TokenValidationParameters.IssuerSigningKeys =
                        opts.Configuration.SigningKeys.ToList();
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
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

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
                    foreach (var k in jwks.GetSigningKeys())
                        opts.Configuration.SigningKeys.Add(k);
                    if (
                        opts.Configuration.SigningKeys.Count > 0
                        && (
                            opts.TokenValidationParameters.IssuerSigningKeys == null
                            || !opts.TokenValidationParameters.IssuerSigningKeys.Any()
                        )
                    )
                    {
                        opts.TokenValidationParameters.IssuerSigningKeys =
                            opts.Configuration.SigningKeys.ToList();
                    }
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
app.UseAuthorization();

app.UseAntiforgery();

// Static assets must be allowed anonymously; the app has a global fallback authorization policy
// and we don't want to gate framework files like blazor.server.js or CSS under auth.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().RequireAuthorization().AddInteractiveServerRenderMode();

// Health endpoints
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

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
