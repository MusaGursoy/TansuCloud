// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Text.Json;
using Microsoft.Extensions.Logging;
using TansuCloud.Dashboard.Models;
using TansuCloud.Dashboard.Observability.Loki;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Adapter that implements ISigNozLogsService using Loki as the backend.
/// Maps between Dashboard's log models and Loki's API responses.
/// Preserves existing Logs.razor UI without changes (Task 47 Phase 3).
/// </summary>
public sealed class LokiLogsAdapter : ISigNozLogsService
{
    private readonly ILokiLogsService _lokiService;
    private readonly ILogger<LokiLogsAdapter> _logger;

    public LokiLogsAdapter(
        ILokiLogsService lokiService,
        ILogger<LokiLogsAdapter> logger)
    {
        _lokiService = lokiService;
        _logger = logger;
    } // End of Constructor LokiLogsAdapter

    public async Task<LogSearchResult> SearchLogsAsync(
        LogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "LokiLogsAdapter: SearchLogsAsync called. Service={Service}, Severity={Severity}, StartNano={StartNano}, EndNano={EndNano}, Limit={Limit}, Offset={Offset}, SearchText={SearchText}",
                request.ServiceName ?? "null",
                request.SeverityText ?? "null",
                request.StartTimeNano,
                request.EndTimeNano,
                request.Limit,
                request.Offset,
                request.SearchText ?? "null"
            );

            // Map Dashboard request to Loki filters
            var lokiFilters = new LokiSearchFilters
            {
                ServiceName = request.ServiceName,
                Level = MapSeverityToLokiLevel(request.SeverityText),
                StartNano = request.StartTimeNano,
                EndNano = request.EndTimeNano,
                Limit = request.Limit,
                Direction = request.OrderBy == "asc" ? "forward" : "backward"
            };

            _logger.LogInformation(
                "LokiLogsAdapter: Mapped to Loki filters. Service={Service}, Level={Level}",
                lokiFilters.ServiceName ?? "null",
                lokiFilters.Level ?? "null"
            );

            // Execute Loki query
            var lokiResult = await _lokiService.SearchLogsAsync(lokiFilters, cancellationToken);

            _logger.LogInformation(
                "LokiLogsAdapter: Received {LogCount} logs from Loki service",
                lokiResult.Logs.Count
            );

            // Transform Loki logs to Dashboard logs
            var logs = lokiResult.Logs
                .Select(TransformLokiLogToLogEntry)
                .ToList();

            // Apply additional filters that Loki doesn't support directly
            if (!string.IsNullOrWhiteSpace(request.SearchText))
            {
                logs = logs.Where(log => log.Body.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase)).ToList();
                _logger.LogInformation("LokiLogsAdapter: After SearchText filter: {LogCount} logs", logs.Count);
            }

            if (!string.IsNullOrWhiteSpace(request.TraceId))
            {
                logs = logs.Where(log => log.TraceId == request.TraceId).ToList();
                _logger.LogInformation("LokiLogsAdapter: After TraceId filter: {LogCount} logs", logs.Count);
            }

            if (!string.IsNullOrWhiteSpace(request.SpanId))
            {
                logs = logs.Where(log => log.SpanId == request.SpanId).ToList();
                _logger.LogInformation("LokiLogsAdapter: After SpanId filter: {LogCount} logs", logs.Count);
            }

            // Apply offset for pagination
            var total = logs.Count;
            if (request.Offset > 0)
            {
                logs = logs.Skip(request.Offset).ToList();
            }

            // Limit results
            if (logs.Count > request.Limit)
            {
                logs = logs.Take(request.Limit).ToList();
            }

            _logger.LogInformation(
                "LokiLogsAdapter: Returning {LogCount} logs (total={Total}, hasMore={HasMore})",
                logs.Count,
                total,
                total > (request.Offset + logs.Count)
            );

            return new LogSearchResult
            {
                Logs = logs,
                Total = total,
                HasMore = total > (request.Offset + logs.Count)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching logs via Loki adapter");
            return new LogSearchResult { Logs = [], Total = 0, HasMore = false };
        }
    } // End of Method SearchLogsAsync

    public async Task<LogEntry?> GetLogByIdAsync(
        string logId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // LogId format: "{timestampNano}_{hash}"
            // Parse timestamp from ID
            var parts = logId.Split('_');
            if (parts.Length < 1 || !long.TryParse(parts[0], out var timestampNano))
            {
                _logger.LogWarning("Invalid log ID format: {LogId}", logId);
                return null;
            }

            // Query Loki for logs around this timestamp (±1 second window)
            var filters = new LokiSearchFilters
            {
                StartNano = timestampNano - 1_000_000_000, // 1 second before
                EndNano = timestampNano + 1_000_000_000,   // 1 second after
                Limit = 100 // Small limit, we'll find exact match
            };

            var result = await _lokiService.SearchLogsAsync(filters, cancellationToken);

            // Find exact match by timestamp
            var matchingLog = result.Logs.FirstOrDefault(log => log.TimestampNano == timestampNano);
            if (matchingLog == null)
            {
                _logger.LogWarning("Log not found by ID: {LogId}", logId);
                return null;
            }

            return TransformLokiLogToLogEntry(matchingLog);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log by ID via Loki adapter: {LogId}", logId);
            return null;
        }
    } // End of Method GetLogByIdAsync

    public async Task<List<LogField>> GetLogFieldsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Get available labels from Loki
            var labels = await _lokiService.GetLabelsAsync(cancellationToken);

            // Map Loki labels to Dashboard log fields
            var fields = labels.Select(label => new LogField
            {
                Name = label,
                Type = "string", // Loki labels are always strings
                IsIndexed = true // Loki labels are indexed
            }).ToList();

            // Add standard OTEL fields that might not be in labels yet
            var standardFields = new[]
            {
                new LogField { Name = "service_name", Type = "string", IsIndexed = true },
                new LogField { Name = "level", Type = "string", IsIndexed = true },
                new LogField { Name = "trace_id", Type = "string", IsIndexed = true },
                new LogField { Name = "span_id", Type = "string", IsIndexed = true },
                new LogField { Name = "body", Type = "string", IsIndexed = false }
            };

            // Merge and deduplicate
            var allFields = fields.Concat(standardFields)
                .GroupBy(f => f.Name)
                .Select(g => g.First())
                .OrderBy(f => f.Name)
                .ToList();

            return allFields;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log fields via Loki adapter");
            return [];
        }
    } // End of Method GetLogFieldsAsync

    public async Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _lokiService.GetServicesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting services via Loki adapter");
            return [];
        }
    } // End of Method GetServicesAsync

    #region Private Helpers

    /// <summary>
    /// Transforms a Loki log entry to Dashboard LogEntry model.
    /// </summary>
    private LogEntry TransformLokiLogToLogEntry(LokiLogEntry lokiLog)
    {
        // Generate unique ID from timestamp (Loki doesn't have unique IDs)
        var logId = $"{lokiLog.TimestampNano}_{lokiLog.Message.GetHashCode():X}";

        // Extract service name from labels
        var serviceName = lokiLog.Labels.GetValueOrDefault("service_name", "unknown");

        // Extract severity from labels (OTEL format)
        var severityText = lokiLog.Labels.GetValueOrDefault("level", "INFO").ToUpperInvariant();
        var severityNumber = MapSeverityTextToNumber(severityText);

        // Extract trace/span IDs from labels if present
        var traceId = lokiLog.Labels.GetValueOrDefault("trace_id");
        var spanId = lokiLog.Labels.GetValueOrDefault("span_id");

        // Separate resource attributes from log attributes
        var resourceAttributes = new Dictionary<string, string>();
        var logAttributes = new Dictionary<string, JsonElement>();

        foreach (var (key, value) in lokiLog.Labels)
        {
            // Resource attributes are typically prefixed or well-known keys
            if (key.StartsWith("resource.") || key == "service_name" || key == "service_version")
            {
                resourceAttributes[key] = value;
            }
            else if (key != "level" && key != "trace_id" && key != "span_id")
            {
                // Other labels become log attributes
                logAttributes[key] = JsonSerializer.SerializeToElement(value);
            }
        }

        return new LogEntry
        {
            Id = logId,
            TimestampNano = lokiLog.TimestampNano,
            Body = lokiLog.Message,
            SeverityText = severityText,
            SeverityNumber = severityNumber,
            ServiceName = serviceName,
            TraceId = traceId,
            SpanId = spanId,
            ResourceAttributes = resourceAttributes,
            Attributes = logAttributes
        };
    } // End of Method TransformLokiLogToLogEntry

    /// <summary>
    /// Maps Dashboard severity text to Loki level label value.
    /// Dashboard uses OTEL conventions (uppercase), Loki uses various conventions.
    /// </summary>
    private static string? MapSeverityToLokiLevel(string? severityText)
    {
        if (string.IsNullOrWhiteSpace(severityText))
        {
            return null;
        }

        // Normalize to match common Loki level values
        return severityText.ToUpperInvariant() switch
        {
            "TRACE" => "trace",
            "DEBUG" => "debug",
            "INFO" => "info",
            "WARN" or "WARNING" => "warn",
            "ERROR" => "error",
            "FATAL" or "CRITICAL" => "fatal",
            _ => severityText.ToLowerInvariant()
        };
    } // End of Method MapSeverityToLokiLevel

    /// <summary>
    /// Maps severity text to numeric level (OTEL standard).
    /// </summary>
    private static int MapSeverityTextToNumber(string severityText)
    {
        return severityText.ToUpperInvariant() switch
        {
            "TRACE" => 1,
            "DEBUG" => 5,
            "INFO" => 9,
            "WARN" or "WARNING" => 13,
            "ERROR" => 17,
            "FATAL" or "CRITICAL" => 21,
            _ => 9 // Default to INFO
        };
    } // End of Method MapSeverityTextToNumber

    #endregion
} // End of Class LokiLogsAdapter
