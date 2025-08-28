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
var dbName = "tansu.db";
var dbVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(dbName, serviceVersion: dbVersion, serviceInstanceId: Environment.MachineName)
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
        "db.read",
        policy =>
            policy.RequireAssertion(ctx =>
                ctx.User.HasScope("db.read") && ctx.User.HasClaim("aud", "tansu.db")
            )
    );
    options.AddPolicy(
        "db.write",
        policy =>
            policy.RequireAssertion(ctx =>
                ctx.User.HasScope("db.write") && ctx.User.HasClaim("aud", "tansu.db")
            )
    );
});

builder
    .Services.AddOpenIddict()
    .AddValidation(options =>
    {
        // In dev, validate tokens issued via the gateway path
        options.SetIssuer(new Uri("https://localhost:7299/identity/"));
        options.AddAudiences("tansu.db");
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
