// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Observability;
using TansuCloud.Observability.Shared.Configuration;
using TansuCloud.Telemetry.Admin;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Contracts;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Data.Entities;
using TansuCloud.Telemetry.HealthChecks;
using TansuCloud.Telemetry.Ingestion;
using TansuCloud.Telemetry.Ingestion.Models;
using TansuCloud.Telemetry.Metrics;
using TansuCloud.Telemetry.Security;

var builder = WebApplication.CreateBuilder(args);

var runningInContainer = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
    "true",
    StringComparison.OrdinalIgnoreCase
);

if (!runningInContainer)
{
    const string devLoopbackUrl = "http://127.0.0.1:5279";
    const string devLocalhostUrl = "http://localhost:5279";
    var urlsRaw = builder.Configuration["ASPNETCORE_URLS"];

    if (string.IsNullOrWhiteSpace(urlsRaw))
    {
        builder.WebHost.UseUrls(devLoopbackUrl, devLocalhostUrl);
    }
    else
    {
        var urls = urlsRaw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var shouldAugment = false;

        if (
            urls.Any(url => string.Equals(url, devLocalhostUrl, StringComparison.OrdinalIgnoreCase))
            && !urls.Any(url =>
                string.Equals(url, devLoopbackUrl, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            urls.Add(devLoopbackUrl);
            shouldAugment = true;
        }

        if (shouldAugment)
        {
            builder.WebHost.UseUrls(urls.ToArray());
        }
    }
}

builder
    .Services.AddOptions<TelemetryIngestionOptions>()
    .Bind(builder.Configuration.GetSection("Telemetry:Ingestion"))
    .ValidateDataAnnotations()
    .Validate(
        static options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "Telemetry:Ingestion:ApiKey must be provided."
    )
    .ValidateOnStart();

builder
    .Services.AddOptions<TelemetryAdminOptions>()
    .Bind(builder.Configuration.GetSection("Telemetry:Admin"))
    .ValidateDataAnnotations()
    .Validate(
        static options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "Telemetry:Admin:ApiKey must be provided."
    )
    .ValidateOnStart();

builder
    .Services.AddOptions<TelemetryDatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Telemetry:Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", TelemetryAuthorizationPolicies.Admin);
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
    options.Conventions.AddPageRouteModelConvention(
        "/Admin/Envelopes/Index",
        model =>
        {
            model.Selectors.Clear();

            static void AddSelector(PageRouteModel routeModel, string template)
            {
                routeModel.Selectors.Add(
                    new SelectorModel
                    {
                        AttributeRouteModel = new AttributeRouteModel { Template = template }
                    }
                );
            }

            AddSelector(model, "admin/envelopes");
            AddSelector(model, "admin");
        }
    );
});

builder.Services.AddSingleton<TelemetryMetrics>();
builder.Services.AddSingleton<ITelemetryIngestionQueue, TelemetryIngestionQueue>();
builder.Services.AddScoped<TelemetryRepository>();
builder.Services.AddHostedService<TelemetryIngestionWorker>();

builder.Services.AddDbContext<TelemetryDbContext>(
    (serviceProvider, optionsBuilder) =>
    {
        var env = serviceProvider.GetRequiredService<IHostEnvironment>();
        var dbOptions = serviceProvider
            .GetRequiredService<IOptions<TelemetryDatabaseOptions>>()
            .Value;
        var databasePath = TelemetryDatabasePathResolver.Resolve(env, dbOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var connectionBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = dbOptions.EnforceForeignKeys
        };

        optionsBuilder.UseSqlite(
            connectionBuilder.ToString(),
            sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(Program).Assembly.FullName);
            }
        );
        if (env.IsDevelopment())
        {
            optionsBuilder.EnableDetailedErrors();
            optionsBuilder.EnableSensitiveDataLogging();
        }
    }
);

builder
    .Services.AddHealthChecks()
    .AddDbContextCheck<TelemetryDbContext>("database", tags: new[] { "ready" })
    .AddCheck<TelemetryQueueHealthCheck>("ingestion_queue", tags: new[] { "ready" });

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resourceBuilder =>
        resourceBuilder
            .AddService(
                serviceName: "tansu.cloud.telemetry",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                serviceInstanceId: Environment.MachineName
            )
            .AddAttributes(
                new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", (object)builder.Environment.EnvironmentName)
                }
            )
    )
    .WithTracing(tracingBuilder =>
    {
        tracingBuilder.AddTansuAspNetCoreInstrumentation();
        tracingBuilder.AddHttpClientInstrumentation();
        tracingBuilder.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
    })
    .WithMetrics(metricBuilder =>
    {
        metricBuilder.AddRuntimeInstrumentation();
        metricBuilder.AddAspNetCoreInstrumentation();
        metricBuilder.AddHttpClientInstrumentation();
        metricBuilder.AddMeter("TansuCloud.Telemetry");
    });

var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = TelemetryAuthenticationDefaults.IngestionSchemeName;
});

authenticationBuilder.AddScheme<
    AuthenticationSchemeOptions,
    TelemetryApiKeyAuthenticationHandler<TelemetryIngestionOptions>
>(TelemetryAuthenticationDefaults.IngestionSchemeName, _ => { });

authenticationBuilder.AddScheme<
    AuthenticationSchemeOptions,
    TelemetryApiKeyAuthenticationHandler<TelemetryAdminOptions>
>(TelemetryAuthenticationDefaults.AdminSchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        TelemetryAuthorizationPolicies.Ingestion,
        policy =>
        {
            policy.AddAuthenticationSchemes(TelemetryAuthenticationDefaults.IngestionSchemeName);
            policy.RequireAuthenticatedUser();
        }
    );

    options.AddPolicy(
        TelemetryAuthorizationPolicies.Admin,
        policy =>
        {
            policy.AddAuthenticationSchemes(TelemetryAuthenticationDefaults.AdminSchemeName);
            policy.RequireAuthenticatedUser();
        }
    );
});

var app = builder.Build();

app.UseExceptionHandler(_ => { });

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseStatusCodePages(context =>
{
    var httpContext = context.HttpContext;
    if (
        httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized
        && !httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && httpContext.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
    )
    {
        var currentUrl =
            $"{httpContext.Request.PathBase}{httpContext.Request.Path}{httpContext.Request.QueryString}";
        var redirectUrl = TelemetryAdminAuthenticationDefaults.LoginPath;

        if (!string.IsNullOrWhiteSpace(currentUrl) && currentUrl != "/")
        {
            redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "returnUrl", currentUrl);
        }

        redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "missingKey", "1");
        httpContext.Response.Redirect(redirectUrl);
    }

    return Task.CompletedTask;
});

// Ensure Razor pages (admin UI) are mapped before health endpoints so generic patterns
// like `/health/*` do not pre-empt `/admin` routes when middleware ordering changes.
app.MapRazorPages();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = healthCheck => healthCheck.Tags.Contains("ready"),
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                details = report.Entries.Select(kvp => new
                {
                    name = kvp.Key,
                    status = kvp.Value.Status.ToString(),
                    description = kvp.Value.Description,
                    data = kvp.Value.Data
                })
            };
            await context
                .Response.WriteAsync(JsonSerializer.Serialize(payload))
                .ConfigureAwait(false);
        }
    }
);

app.MapGet("/", () => Results.Redirect("/admin/envelopes"));

var apiGroup = app.MapGroup("/api/logs")
    .RequireAuthorization(TelemetryAuthorizationPolicies.Ingestion);

var adminGroup = app.MapGroup("/api/admin")
    .RequireAuthorization(TelemetryAuthorizationPolicies.Admin);

adminGroup
    .MapGet(
        "/envelopes",
        async (
            [AsParameters] TelemetryEnvelopeListRequest request,
            TelemetryRepository repository,
            IOptionsSnapshot<TelemetryAdminOptions> adminOptions,
            CancellationToken cancellationToken
        ) =>
        {
            var options = adminOptions.Value;

            if (
                !TelemetryEnvelopeRequestProcessor.TryCreateQuery(
                    request,
                    options,
                    out var query,
                    out var validationErrors
                )
            )
            {
                return Results.ValidationProblem(validationErrors);
            }

            var result = await repository
                .QueryEnvelopesAsync(query, cancellationToken)
                .ConfigureAwait(false);
            var envelopes = result.Items.Select(TelemetryAdminMapper.ToSummary).ToArray();

            var response = new TelemetryEnvelopeListResponse(result.TotalCount, envelopes);
            return Results.Ok(response);
        }
    )
    .WithName("TelemetryAdmin_ListEnvelopes")
    .WithSummary("Lists telemetry envelopes using optional filters and pagination.");

adminGroup
    .MapGet(
        "/envelopes/{id:guid}",
        async (Guid id, TelemetryRepository repository, CancellationToken cancellationToken) =>
        {
            var envelope = await repository
                .GetEnvelopeAsync(id, includeDeleted: true, cancellationToken)
                .ConfigureAwait(false);

            if (envelope is null)
            {
                return Results.NotFound();
            }

            var detail = TelemetryAdminMapper.ToDetail(envelope);
            return Results.Ok(detail);
        }
    )
    .WithName("TelemetryAdmin_GetEnvelope")
    .WithSummary("Gets a telemetry envelope including items by identifier.");

adminGroup
    .MapGet(
        "/envelopes/export/json",
        async (
            [AsParameters] TelemetryEnvelopeListRequest request,
            TelemetryRepository repository,
            IOptionsSnapshot<TelemetryAdminOptions> adminOptions,
            CancellationToken cancellationToken
        ) =>
        {
            var options = adminOptions.Value;

            if (
                !TelemetryEnvelopeRequestProcessor.TryCreateQuery(
                    request,
                    options,
                    out var query,
                    out var validationErrors
                )
            )
            {
                return Results.ValidationProblem(validationErrors);
            }

            var exportLimit = Math.Max(1, options.MaxExportItems);
            var envelopes = await repository
                .ExportEnvelopesAsync(query, exportLimit, includeItems: true, cancellationToken)
                .ConfigureAwait(false);

            var details = envelopes.Select(TelemetryAdminMapper.ToDetail).ToArray();
            var payload = TelemetryEnvelopeExportFormatter.CreateJson(details);
            var fileName = $"telemetry-envelopes-{DateTime.UtcNow:yyyyMMddHHmmss}-utc.json";
            return Results.File(payload, "application/json", fileName);
        }
    )
    .WithName("TelemetryAdmin_ExportEnvelopesJson")
    .WithSummary(
        "Exports telemetry envelopes (with items) as a JSON payload respecting export limits."
    );

adminGroup
    .MapGet(
        "/envelopes/export/csv",
        async (
            [AsParameters] TelemetryEnvelopeListRequest request,
            TelemetryRepository repository,
            IOptionsSnapshot<TelemetryAdminOptions> adminOptions,
            CancellationToken cancellationToken
        ) =>
        {
            var options = adminOptions.Value;

            if (
                !TelemetryEnvelopeRequestProcessor.TryCreateQuery(
                    request,
                    options,
                    out var query,
                    out var validationErrors
                )
            )
            {
                return Results.ValidationProblem(validationErrors);
            }

            var exportLimit = Math.Max(1, options.MaxExportItems);
            var envelopes = await repository
                .ExportEnvelopesAsync(query, exportLimit, includeItems: true, cancellationToken)
                .ConfigureAwait(false);

            var details = envelopes.Select(TelemetryAdminMapper.ToDetail).ToArray();
            var payload = TelemetryEnvelopeExportFormatter.CreateCsv(details);
            var fileName = $"telemetry-envelopes-{DateTime.UtcNow:yyyyMMddHHmmss}-utc.csv";
            return Results.File(payload, "text/csv", fileName);
        }
    )
    .WithName("TelemetryAdmin_ExportEnvelopesCsv")
    .WithSummary(
        "Exports telemetry envelopes (with item timestamps) as a CSV payload respecting export limits."
    );

adminGroup
    .MapPost(
        "/envelopes/{id:guid}/acknowledge",
        async (Guid id, TelemetryRepository repository, CancellationToken cancellationToken) =>
        {
            var acknowledged = await repository
                .TryAcknowledgeAsync(id, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            return acknowledged ? Results.NoContent() : Results.NotFound();
        }
    )
    .WithName("TelemetryAdmin_AcknowledgeEnvelope")
    .WithSummary("Marks a telemetry envelope as acknowledged.");

adminGroup
    .MapPost(
        "/envelopes/{id:guid}/delete",
        async (Guid id, TelemetryRepository repository, CancellationToken cancellationToken) =>
        {
            var deleted = await repository
                .TrySoftDeleteAsync(id, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            return deleted ? Results.NoContent() : Results.NotFound();
        }
    )
    .WithName("TelemetryAdmin_DeleteEnvelope")
    .WithSummary("Soft deletes (archives) a telemetry envelope.");

apiGroup
    .MapPost(
        "/report",
        async (
            LogReportRequest request,
            ITelemetryIngestionQueue queue,
            IOptionsSnapshot<TelemetryIngestionOptions> ingestionOptions,
            ILoggerFactory loggerFactory,
            HttpContext httpContext,
            CancellationToken cancellationToken
        ) =>
        {
            var logger = loggerFactory.CreateLogger("TelemetryIngestionEndpoint");
            if (request is null)
            {
                return Results.BadRequest();
            }

            var validationErrors = new Dictionary<string, string[]>(
                StringComparer.OrdinalIgnoreCase
            );

            if (string.IsNullOrWhiteSpace(request.Host))
            {
                validationErrors["host"] = new[] { "Host is required." };
            }

            if (string.IsNullOrWhiteSpace(request.Environment))
            {
                validationErrors["environment"] = new[] { "Environment is required." };
            }

            if (string.IsNullOrWhiteSpace(request.Service))
            {
                validationErrors["service"] = new[] { "Service is required." };
            }

            if (string.IsNullOrWhiteSpace(request.SeverityThreshold))
            {
                validationErrors["severityThreshold"] = new[] { "Severity threshold is required." };
            }

            if (request.WindowMinutes <= 0)
            {
                validationErrors["windowMinutes"] = new[]
                {
                    "WindowMinutes must be greater than zero."
                };
            }

            if (request.MaxItems <= 0)
            {
                validationErrors["maxItems"] = new[] { "MaxItems must be greater than zero." };
            }

            if (request.Items is null || request.Items.Count == 0)
            {
                validationErrors["items"] = new[] { "At least one log item is required." };
            }

            var convertedItems = new List<TelemetryItemEntity>();
            var maxItems = Math.Max(1, request.MaxItems);
            if (request.Items is not null)
            {
                for (var index = 0; index < request.Items.Count; index++)
                {
                    var item = request.Items[index];
                    if (
                        !DateTimeOffset.TryParse(
                            item.Timestamp,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var timestamp
                        )
                    )
                    {
                        validationErrors[$"items[{index}].timestamp"] = new[]
                        {
                            "Timestamp must be an ISO 8601 value."
                        };
                        continue;
                    }

                    if (item.Count <= 0)
                    {
                        validationErrors[$"items[{index}].count"] = new[]
                        {
                            "Count must be greater than zero."
                        };
                        continue;
                    }

                    if (convertedItems.Count >= maxItems)
                    {
                        break;
                    }

                    var propertiesJson = item.Properties switch
                    {
                        null => null,
                        JsonElement element => element.GetRawText(),
                        _ => JsonSerializer.Serialize(item.Properties)
                    };

                    var entity = new TelemetryItemEntity
                    {
                        Kind = item.Kind,
                        TimestampUtc = timestamp.UtcDateTime,
                        Level = item.Level,
                        Message = item.Message,
                        TemplateHash = item.TemplateHash,
                        Exception = item.Exception,
                        Service = item.Service,
                        Environment = item.Environment,
                        TenantHash = item.TenantHash,
                        CorrelationId = item.CorrelationId,
                        TraceId = item.TraceId,
                        SpanId = item.SpanId,
                        Category = item.Category,
                        EventId = item.EventId,
                        Count = item.Count,
                        PropertiesJson = propertiesJson
                    };

                    convertedItems.Add(entity);
                }
            }

            if (validationErrors.Count > 0)
            {
                return Results.ValidationProblem(validationErrors);
            }

            var envelope = new TelemetryEnvelopeEntity
            {
                Id = Guid.CreateVersion7(),
                ReceivedAtUtc = DateTime.UtcNow,
                Host = request.Host.Trim(),
                Environment = request.Environment.Trim(),
                Service = request.Service.Trim(),
                SeverityThreshold = request.SeverityThreshold.Trim(),
                WindowMinutes = request.WindowMinutes,
                MaxItems = request.MaxItems,
                ItemCount = convertedItems.Count,
                Items = convertedItems
            };

            foreach (var item in convertedItems)
            {
                item.EnvelopeId = envelope.Id;
                item.Envelope = envelope;
            }

            var options = ingestionOptions.Value;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutCts.CancelAfter(options.EnqueueTimeout);

            try
            {
                var enqueued = await queue
                    .TryEnqueueAsync(new TelemetryWorkItem(envelope), timeoutCts.Token)
                    .ConfigureAwait(false);

                if (!enqueued)
                {
                    logger.LogWarning(
                        "Telemetry ingestion queue rejected payload from host {Host} service {Service}",
                        envelope.Host,
                        envelope.Service
                    );

                    return Results.Problem(
                        detail: "Telemetry service is experiencing high load. Please retry after a short delay.",
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Queue capacity exceeded"
                    );
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Telemetry ingestion queue timed out waiting for capacity for host {Host} service {Service}",
                    envelope.Host,
                    envelope.Service
                );

                return Results.Problem(
                    detail: "Telemetry service is busy. Please retry after a short delay.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Queue timeout"
                );
            }

            logger.LogInformation(
                "Accepted telemetry payload from host {Host} service {Service} containing {ItemCount} items",
                envelope.Host,
                envelope.Service,
                envelope.ItemCount
            );

            var response = new LogReportResponse(true, null);
            return Results.Json(response, statusCode: StatusCodes.Status202Accepted);
        }
    )
    .WithName("ReportTelemetry")
    .WithSummary("Accepts a telemetry log report payload for ingestion.")
    .Produces<LogReportResponse>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

using (var scope = app.Services.CreateScope())
{
    var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
    var dbOptions = scope
        .ServiceProvider.GetRequiredService<IOptions<TelemetryDatabaseOptions>>()
        .Value;
    var dbContext = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
    var databasePath = TelemetryDatabasePathResolver.Resolve(env, dbOptions);
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await dbContext.Database.MigrateAsync().ConfigureAwait(false);
}

await app.RunAsync().ConfigureAwait(false);
