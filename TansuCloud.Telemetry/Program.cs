// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TansuCloud.Observability;
using TansuCloud.Observability.Shared.Configuration;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Ingestion;
using TansuCloud.Telemetry.Ingestion.Models;
using TansuCloud.Telemetry.Metrics;
using TansuCloud.Telemetry.Security;
using TansuCloud.Telemetry.Data.Entities;
using TansuCloud.Telemetry.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<TelemetryIngestionOptions>()
	.Bind(builder.Configuration.GetSection("TelemetryIngestion"))
	.ValidateDataAnnotations()
	.Validate(
		static options => !string.IsNullOrWhiteSpace(options.ApiKey),
		"TelemetryIngestion:ApiKey must be provided."
	);

builder.Services.AddOptions<TelemetryDatabaseOptions>()
	.Bind(builder.Configuration.GetSection("TelemetryDatabase"))
	.ValidateDataAnnotations();

builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();

builder.Services.AddSingleton<TelemetryMetrics>();
builder.Services.AddSingleton<ITelemetryIngestionQueue, TelemetryIngestionQueue>();
builder.Services.AddSingleton<TelemetryRepository>();
builder.Services.AddHostedService<TelemetryIngestionWorker>();

builder.Services.AddDbContext<TelemetryDbContext>((serviceProvider, optionsBuilder) =>
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

	optionsBuilder.UseSqlite(connectionBuilder.ToString(), sqliteOptions =>
	{
		sqliteOptions.MigrationsAssembly(typeof(Program).Assembly.FullName);
	});

	if (env.IsDevelopment())
	{
		optionsBuilder.EnableDetailedErrors();
		optionsBuilder.EnableSensitiveDataLogging();
	}
});

builder.Services.AddHealthChecks()
	.AddDbContextCheck<TelemetryDbContext>("database", tags: new[] { "ready" })
	.AddCheck<TelemetryQueueHealthCheck>("ingestion_queue", tags: new[] { "ready" });

builder.Services.AddOpenTelemetry()
	.ConfigureResource(resourceBuilder => resourceBuilder.AddService(
			serviceName: "tansu.cloud.telemetry",
			serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
			serviceInstanceId: Environment.MachineName
		)
		.AddAttributes(new KeyValuePair<string, object>[]
		{
			new("deployment.environment", (object)builder.Environment.EnvironmentName)
		}))
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
		metricBuilder.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
	});

builder.Services.AddTansuObservabilityCore();

builder.Logging.AddOpenTelemetry(o =>
{
	o.IncludeFormattedMessage = true;
	o.ParseStateValues = true;
	o.AddTansuOtlpExporter(builder.Configuration, builder.Environment);
});

builder.Services
	.AddAuthentication(options =>
	{
		options.DefaultAuthenticateScheme = TelemetryAuthenticationDefaults.SchemeName;
		options.DefaultChallengeScheme = TelemetryAuthenticationDefaults.SchemeName;
	})
	.AddScheme<AuthenticationSchemeOptions, TelemetryApiKeyAuthenticationHandler>(
		TelemetryAuthenticationDefaults.SchemeName,
		_ => { }
	);

builder.Services.AddAuthorization(options =>
{
	options.AddPolicy(
		TelemetryAuthorizationPolicies.Ingestion,
		policy =>
		{
			policy.AddAuthenticationSchemes(TelemetryAuthenticationDefaults.SchemeName);
			policy.RequireAuthenticatedUser();
		}
	);
});

var app = builder.Build();

app.UseExceptionHandler(_ => { });

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

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
			await context.Response.WriteAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
		}
	}
);

app.MapGet("/", () => Results.Redirect("/health/ready"));

var apiGroup = app.MapGroup("/api/logs").RequireAuthorization(TelemetryAuthorizationPolicies.Ingestion);

apiGroup.MapPost("/report", async (
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

		var validationErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

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
			validationErrors["windowMinutes"] = new[] { "WindowMinutes must be greater than zero." };
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
		if (request.Items is not null)
		{
			for (var index = 0; index < request.Items.Count; index++)
			{
				var item = request.Items[index];
				if (!DateTimeOffset.TryParse(
						item.Timestamp,
						CultureInfo.InvariantCulture,
						DateTimeStyles.RoundtripKind,
						out var timestamp
					))
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
			Id = Guid.NewGuid(),
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
		}

		var options = ingestionOptions.Value;
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(options.EnqueueTimeout);

		if (!await queue.TryEnqueueAsync(new TelemetryWorkItem(envelope), timeoutCts.Token).ConfigureAwait(false))
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

		logger.LogInformation(
			"Accepted telemetry payload from host {Host} service {Service} containing {ItemCount} items",
			envelope.Host,
			envelope.Service,
			envelope.ItemCount
		);

		var response = new LogReportResponse(true, null);
		return Results.Json(response, statusCode: StatusCodes.Status202Accepted);
	})
	.WithName("ReportTelemetry")
	.WithSummary("Accepts a telemetry log report payload for ingestion.")
	.Produces<LogReportResponse>(StatusCodes.Status202Accepted)
	.ProducesValidationProblem(StatusCodes.Status400BadRequest)
	.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

using (var scope = app.Services.CreateScope())
{
	var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
	var dbOptions = scope.ServiceProvider.GetRequiredService<IOptions<TelemetryDatabaseOptions>>().Value;
	var dbContext = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
	var databasePath = TelemetryDatabasePathResolver.Resolve(env, dbOptions);
	Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
	await dbContext.Database.MigrateAsync().ConfigureAwait(false);
}

await app.RunAsync().ConfigureAwait(false);
