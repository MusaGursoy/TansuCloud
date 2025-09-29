// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.EntityFrameworkCore;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace TansuCloud.Observability;

/// <summary>
/// OpenTelemetry helper extensions for ASP.NET Core tracing configuration.
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Adds standardized ASP.NET Core instrumentation that enriches spans with route and tenant metadata.
    /// </summary>
    public static TracerProviderBuilder AddTansuAspNetCoreInstrumentation(
        this TracerProviderBuilder builder
    )
    {
        return builder.AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                if (activity is null)
                {
                    return;
                }

                TrySetRouteTemplate(activity, request.HttpContext);
                TrySetRouteBase(activity, request);
                TrySetTenant(activity, request);
            };
            options.EnrichWithHttpResponse = (activity, response) =>
            {
                if (activity is null)
                {
                    return;
                }

                activity.SetTag("http.status_code", response.StatusCode);
            };
        });
    } // End of Method AddTansuAspNetCoreInstrumentation

    /// <summary>
    /// Adds standardized database and cache instrumentation (Entity Framework Core, Npgsql, Redis).
    /// </summary>
    public static TracerProviderBuilder AddTansuDataInstrumentation(
        this TracerProviderBuilder builder,
        bool includeRedis = true
    )
    {
        builder.AddEntityFrameworkCoreInstrumentation(options =>
        {
            options.SetDbStatementForText = true;
            options.SetDbStatementForStoredProcedure = true;
        });

        var npgsqlActivitySourceName = ResolveNpgsqlActivitySourceName();
        builder.AddSource(npgsqlActivitySourceName);

        if (includeRedis)
        {
            builder.AddRedisInstrumentation(options =>
            {
                options.FlushInterval = TimeSpan.FromSeconds(1);
                options.SetVerboseDatabaseStatements = false;
            });
        }

        return builder;
    } // End of Method AddTansuDataInstrumentation

    /// <summary>
    /// Adds an OTLP exporter with standardized retry and batching configuration.
    /// </summary>
    public static TracerProviderBuilder AddTansuOtlpExporter(
        this TracerProviderBuilder builder,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        return builder.AddOtlpExporter(options =>
        {
            ConfigureOtlpExporter(options, configuration, environment);
        });
    } // End of Method AddTansuOtlpExporter

    /// <summary>
    /// Adds an OTLP exporter with standardized retry and batching configuration.
    /// </summary>
    public static MeterProviderBuilder AddTansuOtlpExporter(
        this MeterProviderBuilder builder,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        return builder.AddOtlpExporter(options =>
        {
            ConfigureOtlpExporter(options, configuration, environment);
        });
    } // End of Method AddTansuOtlpExporter

    /// <summary>
    /// Adds an OTLP exporter with standardized retry and batching configuration.
    /// </summary>
    public static OpenTelemetryLoggerOptions AddTansuOtlpExporter(
        this OpenTelemetryLoggerOptions options,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        return options.AddOtlpExporter(exporterOptions =>
        {
            ConfigureOtlpExporter(exporterOptions, configuration, environment);
        });
    } // End of Method AddTansuOtlpExporter

    private static void ConfigureOtlpExporter(
        OtlpExporterOptions exporterOptions,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        var section = configuration.GetSection("OpenTelemetry:Otlp");
        var endpointRaw = section["Endpoint"];
        if (string.IsNullOrWhiteSpace(endpointRaw))
        {
            endpointRaw = ResolveDefaultOtlpEndpoint();
        }

        if (
            !string.IsNullOrWhiteSpace(endpointRaw)
            && Uri.TryCreate(endpointRaw, UriKind.Absolute, out var endpointUri)
        )
        {
            exporterOptions.Endpoint = endpointUri;
        }

        exporterOptions.ExportProcessorType = ExportProcessorType.Batch;

        var protocolRaw = section["Protocol"];
        if (
            !string.IsNullOrWhiteSpace(protocolRaw)
            && Enum.TryParse<OtlpExportProtocol>(protocolRaw, true, out var protocol)
        )
        {
            exporterOptions.Protocol = protocol;
        }

        var headersRaw = section["Headers"];
        if (!string.IsNullOrWhiteSpace(headersRaw))
        {
            exporterOptions.Headers = headersRaw;
        }

        var timeout = section.GetValue<int?>("TimeoutMilliseconds");
        exporterOptions.TimeoutMilliseconds = timeout ?? ResolveTimeout(environment);

        ConfigureRetry(exporterOptions, section.GetSection("Retry"));

        ApplyBatchDefaults(exporterOptions, section.GetSection("Batch"));
    } // End of Method ConfigureOtlpExporter

    private static string ResolveDefaultOtlpEndpoint()
    {
        var inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

        return inContainer ? "http://signoz-otel-collector:4317" : "http://127.0.0.1:4317";
    } // End of Method ResolveDefaultOtlpEndpoint

    private static int ResolveTimeout(IHostEnvironment environment)
    {
        // Allow a shorter timeout in Development for faster feedback when collectors are offline.
        return environment.IsDevelopment() ? 10000 : 30000;
    } // End of Method ResolveTimeout

    private static void ConfigureRetry(
        OtlpExporterOptions exporterOptions,
        IConfiguration retrySection
    )
    {
        if (exporterOptions.Protocol != OtlpExportProtocol.Grpc)
        {
            return;
        }

        var section = retrySection as IConfigurationSection;
        var maxAttempts = section?.GetValue<int?>("MaxAttempts") ?? 5;
        var initialBackoffMilliseconds =
            section?.GetValue<int?>("InitialBackoffMilliseconds") ?? 1000;
        var maxBackoffMilliseconds = section?.GetValue<int?>("MaxBackoffMilliseconds") ?? 16000;
        var backoffMultiplier = section?.GetValue<double?>("BackoffMultiplier") ?? 2.0;

        var retryPolicy = new RetryPolicy
        {
            MaxAttempts = maxAttempts,
            InitialBackoff = TimeSpan.FromMilliseconds(initialBackoffMilliseconds),
            MaxBackoff = TimeSpan.FromMilliseconds(maxBackoffMilliseconds),
            BackoffMultiplier = backoffMultiplier,
        };

        retryPolicy.RetryableStatusCodes.Add(Grpc.Core.StatusCode.Unavailable);
        retryPolicy.RetryableStatusCodes.Add(Grpc.Core.StatusCode.ResourceExhausted);
        retryPolicy.RetryableStatusCodes.Add(Grpc.Core.StatusCode.DeadlineExceeded);

        var methodConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = retryPolicy
        };

        dynamic dynOptions = exporterOptions;

        try
        {
            var channelOptions = dynOptions.GrpcChannelOptions ?? new GrpcChannelOptions();
            channelOptions.ServiceConfig = new ServiceConfig { MethodConfigs = { methodConfig } };
            dynOptions.GrpcChannelOptions = channelOptions;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Property not available on current exporter (e.g., HTTP/proto); skip custom retry configuration.
        }
    } // End of Method ConfigureRetry

    private static void ApplyBatchDefaults(
        OtlpExporterOptions exporterOptions,
        IConfiguration batchSection
    )
    {
        dynamic? batchOptions = exporterOptions.BatchExportProcessorOptions;
        if (batchOptions is null)
        {
            return;
        }

        var maxQueue = batchSection.GetValue<int?>("MaxQueueSize") ?? 4096;
        var scheduledDelay = batchSection.GetValue<int?>("ScheduledDelayMilliseconds") ?? 5000;
        var maxExportBatch = batchSection.GetValue<int?>("MaxExportBatchSize") ?? 512;
        var exporterTimeout =
            batchSection.GetValue<int?>("ExporterTimeoutMilliseconds")
            ?? exporterOptions.TimeoutMilliseconds;

        batchOptions.MaxQueueSize = maxQueue;
        batchOptions.ScheduledDelayMilliseconds = scheduledDelay;
        batchOptions.MaxExportBatchSize = maxExportBatch;
        batchOptions.ExportTimeoutMilliseconds = exporterTimeout;
    } // End of Method ApplyBatchDefaults

    private static void TrySetRouteTemplate(Activity activity, HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint endpoint)
        {
            var routeTemplate = endpoint.RoutePattern?.RawText;
            if (!string.IsNullOrWhiteSpace(routeTemplate))
            {
                activity.SetTag("http.route", routeTemplate);
            }
        }
    } // End of Method TrySetRouteTemplate

    private static void TrySetRouteBase(Activity activity, HttpRequest request)
    {
        var path = request.Path.HasValue ? request.Path.Value : string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var segments = path.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        activity.SetTag("tansu.route_base", segments[0]);
    } // End of Method TrySetRouteBase

    private static void TrySetTenant(Activity activity, HttpRequest request)
    {
        var tenantRaw = request.Headers["X-Tansu-Tenant"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantRaw))
        {
            return;
        }

        var safe = tenantRaw.Trim();
        if (safe.Length > 64)
        {
            safe = safe.Substring(0, 64);
        }

        activity.SetTag("tansu.tenant", safe.ToLowerInvariant());
    } // End of Method TrySetTenant

    private static string ResolveNpgsqlActivitySourceName()
    {
        const string fallback = "Npgsql";
        try
        {
            var connectionType = Type.GetType("Npgsql.NpgsqlConnection, Npgsql");
            var assembly = connectionType?.Assembly;
            var helper = assembly?.GetType("Npgsql.NpgsqlActivitySourceHelper");
            var field = helper?.GetField(
                "ActivitySourceName",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            );
            if (field?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch
        {
            // Reflection fallback; ignore and use default
        }

        return fallback;
    } // End of Method ResolveNpgsqlActivitySourceName
} // End of Class OpenTelemetryExtensions
