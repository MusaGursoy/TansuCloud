// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using TansuCloud.Dashboard.Components;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry baseline for Dashboard
var dashName = "tansu.dashboard";
var dashVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(dashName, serviceVersion: dashVersion, serviceInstanceId: Environment.MachineName)
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
// Enable detailed IdentityModel logs in Development to diagnose OIDC metadata retrieval
if (builder.Environment.IsDevelopment())
{
    Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
}

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();

// OIDC sign-in (dev wiring)
builder
    .Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Cookies";
        options.DefaultChallengeScheme = "oidc";
    })
    .AddCookie("Cookies")
    .AddOpenIdConnect(
        "oidc",
        options =>
        {
            options.Authority =
                builder.Configuration["Oidc:Authority"] ?? "https://localhost:7299/identity";
            // Explicitly set discovery document URL to avoid any authority/path-base ambiguity behind the gateway
            options.MetadataAddress =
                builder.Configuration["Oidc:MetadataAddress"]
                ?? (options.Authority!.TrimEnd('/') + "/.well-known/openid-configuration");
            options.ClientId = builder.Configuration["Oidc:ClientId"] ?? "tansu-dashboard";
            options.ClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? "dev-secret";
            options.ResponseType = "code";
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

            // In Development, allow non-HTTPS authority metadata to simplify local runs behind Gateway
            if (builder.Environment.IsDevelopment())
            {
                options.RequireHttpsMetadata = false;
                // Allow insecure dev certificates when fetching metadata/tokens via HTTPS
                var acceptAny = builder.Configuration.GetValue("Oidc:AcceptAnyServerCert", true);
                if (acceptAny)
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
            }

            // Ensure redirect URIs include the gateway path prefix when present
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = ctx =>
                {
                    var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
                    var scheme = ctx.Request.Scheme;
                    var host = ctx.Request.Host.ToString();
                    var callback = ctx.Options.CallbackPath.HasValue
                        ? ctx.Options.CallbackPath.Value
                        : "/signin-oidc";
                    var full = string.IsNullOrEmpty(prefix) ? callback : $"{prefix}{callback}";
                    ctx.ProtocolMessage.RedirectUri = $"{scheme}://{host}{full}";
                    return Task.CompletedTask;
                },
                OnRedirectToIdentityProviderForSignOut = ctx =>
                {
                    var prefix = ctx.Request.Headers["X-Forwarded-Prefix"].ToString();
                    var scheme = ctx.Request.Scheme;
                    var host = ctx.Request.Host.ToString();
                    var callback = ctx.Options.SignedOutCallbackPath.HasValue
                        ? ctx.Options.SignedOutCallbackPath.Value
                        : "/signout-callback-oidc";
                    var full = string.IsNullOrEmpty(prefix) ? callback : $"{prefix}{callback}";
                    ctx.ProtocolMessage.PostLogoutRedirectUri = $"{scheme}://{host}{full}";
                    return Task.CompletedTask;
                }
            };

            // Dev-only: Preload discovery and JWKS to a static configuration to avoid transient metadata fetch issues
            if (builder.Environment.IsDevelopment())
            {
                var preload = builder.Configuration.GetValue("Oidc:PreloadDiscovery", true);
                if (preload)
                {
                    try
                    {
                        var handler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback =
                                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        };
                        using var http = new HttpClient(handler)
                        {
                            Timeout = TimeSpan.FromSeconds(10)
                        };
                        http.DefaultRequestVersion = System.Net.HttpVersion.Version11;
                        http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

                        var metaUrl = options.MetadataAddress!;
                        var metaJson = http.GetStringAsync(metaUrl).GetAwaiter().GetResult();
                        var oidcConfig =
                            Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration.Create(
                                metaJson
                            );

                        if (!string.IsNullOrWhiteSpace(oidcConfig.JwksUri))
                        {
                            var jwksJson = http.GetStringAsync(oidcConfig.JwksUri)
                                .GetAwaiter()
                                .GetResult();
                            var jwks = new Microsoft.IdentityModel.Tokens.JsonWebKeySet(jwksJson);
                            foreach (var key in jwks.GetSigningKeys())
                            {
                                oidcConfig.SigningKeys.Add(key);
                            }
                        }

                        options.Configuration = oidcConfig;
                    }
                    catch
                    {
                        // Best-effort preload; if it fails, the middleware will retry normally
                    }
                }

                // Ensure endpoints are under the "/identity" base even if discovery advertises root paths
                var authority =
                    options.Authority?.TrimEnd('/') ?? "https://localhost:7299/identity";
                if (!authority.EndsWith("/identity"))
                {
                    authority += "/identity";
                }
                var issuer = authority.EndsWith("/") ? authority : authority + "/";
                var cfg =
                    options.Configuration
                    ?? new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();
                cfg.Issuer = issuer;
                cfg.AuthorizationEndpoint = issuer + "connect/authorize";
                cfg.TokenEndpoint = issuer + "connect/token";
                cfg.JwksUri = issuer + ".well-known/jwks";
                options.Configuration = cfg;
            }
        }
    );

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

// Respect proxy headers from the Gateway so Request.Scheme/Host reflect client
app.UseForwardedHeaders(
    new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = null,
        AllowedHosts = { "*" }
    }
);

// Honor X-Forwarded-Prefix so the app behaves as if hosted under that base path (e.g., "/dashboard")
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
);

// Serve static files before auth so framework assets aren't gated
app.UseStaticFiles();

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

app.Run();
