// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Storage.Hosting;
using TansuCloud.Storage.Services;

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

// Optional HybridCache backed by Redis when Cache:Redis is provided
var cacheRedis = builder.Configuration["Cache:Redis"];
if (!string.IsNullOrWhiteSpace(cacheRedis))
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = cacheRedis);
    builder.Services.AddHybridCache();
}

// Add services to the container.

builder.Services.AddControllers();

// Response compression (Task 14): enable Brotli for compressible types using configurable options
builder.Services.AddResponseCompression(options =>
{
    // Bind Storage:Compression options for response compression
    var comp = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()?.Compression ?? new CompressionOptions();
    options.EnableForHttps = comp.EnableForHttps;
    options.Providers.Clear();
    options.Providers.Add<BrotliCompressionProvider>();
    // Allowlist of common text-like types; images/archives not included
    options.MimeTypes = (comp.MimeTypes is { Length: > 0 })
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
    var comp = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()?.Compression ?? new CompressionOptions();
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
var transformsSection = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()?.Transforms ?? new TransformOptions();
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

// Authentication/Authorization (OpenIddict validation)
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddAuthorization(options =>
{
    // Require auth by default
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    // Scope-based policies
    options.AddPolicy(
        "storage.read",
        policy =>
            policy.RequireAssertion(ctx =>
                ctx.User.HasScope("storage.read") || ctx.User.HasScope("admin.full")
            )
    );
    options.AddPolicy(
        "storage.write",
        policy =>
            policy.RequireAssertion(ctx =>
                ctx.User.HasScope("storage.write") || ctx.User.HasScope("admin.full")
            )
    );
});

builder
    .Services.AddOpenIddict()
    .AddValidation(options =>
    {
        var issuer = builder.Configuration["Oidc:Issuer"] ?? "http://127.0.0.1:8080/identity/";
        if (!issuer.EndsWith('/'))
            issuer += "/";
        options.SetIssuer(new Uri(issuer));
        options.AddAudiences("tansu.storage");
        options.UseSystemNetHttp();
        options.UseAspNetCore();
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

// Response compression should happen after auth but before controllers to ensure Content-Encoding set
var compEnabled = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()?.Compression?.Enabled ?? true;
if (compEnabled)
{
    app.UseResponseCompression();
}

// Development-only diagnostics: log auth state for bucket endpoints to debug 401/403 causes
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
                    var scopes = string.Join(
                        ' ',
                        context.User is { } u1 ? u1.GetScopes().ToArray() : Array.Empty<string>()
                    );
                    var audiences = string.Join(
                        ' ',
                        context.User is { } u2 ? u2.GetAudiences().ToArray() : Array.Empty<string>()
                    );
                    app.Logger.LogInformation(
                        "AuthDiag[/api/buckets]: hasAuthHeader={HasAuthHeader} isAuthenticated={IsAuth} scopes='{Scopes}' aud='{Audiences}'",
                        hasAuthHeader,
                        isAuth,
                        scopes,
                        audiences
                    );
                }
                catch
                { /* best-effort diagnostics */
                }
            }
            await next();
        }
    );
}

// Per-request metrics and structured logs
app.UseMiddleware<RequestMetricsMiddleware>();

app.MapControllers();

// Health endpoints
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.Run();
