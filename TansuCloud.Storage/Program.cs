// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry baseline
var storageName = "tansu.storage";
var storageVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(storageName, serviceVersion: storageVersion, serviceInstanceId: Environment.MachineName)
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
// Add services to the container.

builder.Services.AddControllers();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Authentication/Authorization (OpenIddict validation)
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddAuthorization(options =>
{
    // Require auth by default
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    // Scope-based policies
    options.AddPolicy(
        "storage.read",
    policy => policy.RequireAssertion(ctx => ctx.User.HasScope("storage.read") && ctx.User.HasClaim("aud", "tansu.storage"))
    );
    options.AddPolicy(
        "storage.write",
    policy => policy.RequireAssertion(ctx => ctx.User.HasScope("storage.write") && ctx.User.HasClaim("aud", "tansu.storage"))
    );
});

builder
    .Services.AddOpenIddict()
    .AddValidation(options =>
    {
        options.SetIssuer(new Uri("https://localhost:7299/identity/"));
        options.AddAudiences("tansu.storage");
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

app.MapControllers();

// Health endpoints
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();
