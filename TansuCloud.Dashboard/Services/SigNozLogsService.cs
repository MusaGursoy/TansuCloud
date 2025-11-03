// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TansuCloud.Dashboard.Models;
using TansuCloud.Dashboard.Observability.SigNoz;
using SigNozLogEntry = TansuCloud.Dashboard.Observability.SigNoz.LogEntry;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Implementation of ISigNozLogsService for querying logs from SigNoz.
/// Uses SigNoz v3 query_range API for structured log search and filtering.
/// </summary>
public class SigNozLogsService : ISigNozLogsService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<SigNozQueryOptions> _options;
    private readonly ILogger<SigNozLogsService> _logger;
    private readonly ISigNozAuthenticationService _authService;

    public SigNozLogsService(
        HttpClient httpClient,
        IOptionsMonitor<SigNozQueryOptions> options,
        ILogger<SigNozLogsService> logger,
        ISigNozAuthenticationService authService
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

        var baseUrl = _options.CurrentValue.ApiBaseUrl?.TrimEnd('/') ?? "http://signoz:8080";
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    } // End of Constructor SigNozLogsService

    /// <inheritdoc/>
    public async Task<LogSearchResult> SearchLogsAsync(
        LogSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation(
                "Searching logs: service={Service}, severity={Severity}, search={Search}, time={Start}-{End}",
                request.ServiceName,
                request.SeverityText,
                request.SearchText,
                request.StartTimeNano,
                request.EndTimeNano
            );

            var queryPayload = BuildLogSearchQuery(request);
            var jsonPayload = JsonSerializer.Serialize(queryPayload);

            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                "/api/v3/query_range",
                jsonPayload,
                cancellationToken
            );
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Log search failed: {StatusCode}", response.StatusCode);
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = ParseLogSearchResponse(responseContent, request.Limit);

            _logger.LogInformation(
                "Log search returned {Count} logs (total: {Total})",
                result.Logs.Count,
                result.Total
            );
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching logs");
            return new LogSearchResult
            {
                Logs = new(),
                Total = 0,
                HasMore = false
            };
        }
    } // End of Method SearchLogsAsync

    /// <inheritdoc/>
    public async Task<Models.LogEntry?> GetLogByIdAsync(
        string logId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation("Getting log by ID: {LogId}", logId);

            // SigNoz logs API doesn't have a direct "get by ID" endpoint
            // We would need to parse the logId (timestamp + unique identifier) and query by time range
            // For now, return null - this would require additional implementation
            _logger.LogWarning(
                "GetLogByIdAsync not fully implemented - requires parsing log ID and time-range query"
            );
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log by ID: {LogId}", logId);
            return null;
        }
    } // End of Method GetLogByIdAsync

    /// <inheritdoc/>
    public async Task<List<LogField>> GetLogFieldsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation("Getting available log fields");

            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                "/api/v1/logs/fields",
                null,
                cancellationToken
            );
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Get log fields failed: {StatusCode}", response.StatusCode);
                return new List<LogField>();
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var fields = ParseLogFields(responseContent);

            _logger.LogInformation("Retrieved {Count} log fields", fields.Count);
            return fields;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log fields");
            return new List<LogField>();
        }
    } // End of Method GetLogFieldsAsync

    /// <inheritdoc/>
    public async Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting available services from logs");

            // Query for distinct service names from logs
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var oneHourAgo = now - (3600L * 1_000_000_000);

            var request = new LogSearchRequest
            {
                StartTimeNano = oneHourAgo,
                EndTimeNano = now,
                Limit = 1000 // Get enough logs to capture all services
            };

            var result = await SearchLogsAsync(request, cancellationToken);
            var services = result
                .Logs.Select(l => l.ServiceName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            _logger.LogInformation("Found {Count} distinct services", services.Count);
            return services;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting services from logs");
            return new List<string>();
        }
    } // End of Method GetServicesAsync

    private object BuildLogSearchQuery(LogSearchRequest request)
    {
        var filters = new List<object>();

        // Time range filter (required)
        filters.Add(
            new
            {
                items = new[]
                {
                    new
                    {
                        key = new
                        {
                            key = "timestamp",
                            dataType = "int64",
                            type = "tag",
                            isColumn = true
                        },
                        op = ">=",
                        value = request.StartTimeNano.ToString()
                    },
                    new
                    {
                        key = new
                        {
                            key = "timestamp",
                            dataType = "int64",
                            type = "tag",
                            isColumn = true
                        },
                        op = "<=",
                        value = request.EndTimeNano.ToString()
                    }
                },
                op = "AND"
            }
        );

        // Service name filter
        if (!string.IsNullOrWhiteSpace(request.ServiceName))
        {
            filters.Add(
                new
                {
                    items = new[]
                    {
                        new
                        {
                            key = new
                            {
                                key = "service_name",
                                dataType = "string",
                                type = "resource"
                            },
                            op = "=",
                            value = request.ServiceName
                        }
                    },
                    op = "AND"
                }
            );
        }

        // Severity filter
        if (!string.IsNullOrWhiteSpace(request.SeverityText))
        {
            filters.Add(
                new
                {
                    items = new[]
                    {
                        new
                        {
                            key = new
                            {
                                key = "severity_text",
                                dataType = "string",
                                type = "tag",
                                isColumn = true
                            },
                            op = "=",
                            value = request.SeverityText
                        }
                    },
                    op = "AND"
                }
            );
        }

        // Text search filter
        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            filters.Add(
                new
                {
                    items = new[]
                    {
                        new
                        {
                            key = new
                            {
                                key = "body",
                                dataType = "string",
                                type = "tag",
                                isColumn = true
                            },
                            op = "contains",
                            value = request.SearchText
                        }
                    },
                    op = "AND"
                }
            );
        }

        // TraceId filter for correlation
        if (!string.IsNullOrWhiteSpace(request.TraceId))
        {
            filters.Add(
                new
                {
                    items = new[]
                    {
                        new
                        {
                            key = new
                            {
                                key = "trace_id",
                                dataType = "string",
                                type = "tag",
                                isColumn = true
                            },
                            op = "=",
                            value = request.TraceId
                        }
                    },
                    op = "AND"
                }
            );
        }

        // SpanId filter for correlation
        if (!string.IsNullOrWhiteSpace(request.SpanId))
        {
            filters.Add(
                new
                {
                    items = new[]
                    {
                        new
                        {
                            key = new
                            {
                                key = "span_id",
                                dataType = "string",
                                type = "tag",
                                isColumn = true
                            },
                            op = "=",
                            value = request.SpanId
                        }
                    },
                    op = "AND"
                }
            );
        }

        return new
        {
            start = request.StartTimeNano,
            end = request.EndTimeNano,
            step = 60,
            compositeQuery = new
            {
                builderQueries = new Dictionary<string, object>
                {
                    ["A"] = new
                    {
                        dataSource = "logs",
                        queryName = "A",
                        aggregateOperator = "noop",
                        aggregateAttribute = new { },
                        filters = new { items = filters, op = "AND" },
                        expression = "A",
                        disabled = false,
                        limit = request.Limit,
                        offset = request.Offset,
                        orderBy = new[]
                        {
                            new { columnName = "timestamp", order = request.OrderBy }
                        }
                    }
                },
                queryType = "builder",
                panelType = "list"
            }
        };
    } // End of Method BuildLogSearchQuery

    private LogSearchResult ParseLogSearchResponse(string responseContent, int limit)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            if (
                !root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("result", out var result)
                || result.GetArrayLength() == 0
            )
            {
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            var firstResult = result[0];
            if (!firstResult.TryGetProperty("list", out var list))
            {
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            var logs = new List<Models.LogEntry>();
            foreach (var item in list.EnumerateArray())
            {
                var logEntry = ParseLogEntry(item);
                if (logEntry != null)
                {
                    logs.Add(logEntry);
                }
            }

            // Get total count if available
            var total = logs.Count;
            if (firstResult.TryGetProperty("total", out var totalElement))
            {
                total = totalElement.GetInt32();
            }

            return new LogSearchResult
            {
                Logs = logs,
                Total = total,
                HasMore = logs.Count >= limit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing log search response");
            return new LogSearchResult
            {
                Logs = new(),
                Total = 0,
                HasMore = false
            };
        }
    } // End of Method ParseLogSearchResponse

    private Models.LogEntry? ParseLogEntry(JsonElement item)
    {
        try
        {
            var data = item.TryGetProperty("data", out var d) ? d : item;

            // Extract timestamp
            var timestampNano = 0L;
            if (data.TryGetProperty("timestamp", out var tsElement))
            {
                if (tsElement.ValueKind == JsonValueKind.String)
                {
                    long.TryParse(tsElement.GetString(), out timestampNano);
                }
                else if (tsElement.ValueKind == JsonValueKind.Number)
                {
                    timestampNano = tsElement.GetInt64();
                }
            }

            // Extract body
            var body = "";
            if (data.TryGetProperty("body", out var bodyElement))
            {
                body = bodyElement.GetString() ?? "";
            }

            // Extract severity
            var severityText = "INFO";
            var severityNumber = 9; // INFO level
            if (data.TryGetProperty("severity_text", out var sevTextElement))
            {
                severityText = sevTextElement.GetString() ?? "INFO";
            }
            if (data.TryGetProperty("severity_number", out var sevNumElement))
            {
                severityNumber = sevNumElement.GetInt32();
            }

            // Extract service name from resources
            var serviceName = "";
            if (data.TryGetProperty("resources_string", out var resourcesElement))
            {
                foreach (var resource in resourcesElement.EnumerateObject())
                {
                    if (resource.Name == "service.name" || resource.Name == "service_name")
                    {
                        serviceName = resource.Value.GetString() ?? "";
                        break;
                    }
                }
            }

            // Extract trace/span IDs
            string? traceId = null;
            string? spanId = null;
            if (data.TryGetProperty("trace_id", out var traceIdElement))
            {
                traceId = traceIdElement.GetString();
            }
            if (data.TryGetProperty("span_id", out var spanIdElement))
            {
                spanId = spanIdElement.GetString();
            }

            // Extract attributes
            var attributes = new Dictionary<string, JsonElement>();
            if (data.TryGetProperty("attributes_string", out var attrsStringElement))
            {
                foreach (var attr in attrsStringElement.EnumerateObject())
                {
                    attributes[attr.Name] = attr.Value;
                }
            }
            if (data.TryGetProperty("attributes_number", out var attrsNumberElement))
            {
                foreach (var attr in attrsNumberElement.EnumerateObject())
                {
                    attributes[attr.Name] = attr.Value;
                }
            }
            if (data.TryGetProperty("attributes_bool", out var attrsBoolElement))
            {
                foreach (var attr in attrsBoolElement.EnumerateObject())
                {
                    attributes[attr.Name] = attr.Value;
                }
            }

            // Extract resource attributes
            var resourceAttributes = new Dictionary<string, string>();
            if (data.TryGetProperty("resources_string", out var resourcesStringElement))
            {
                foreach (var resource in resourcesStringElement.EnumerateObject())
                {
                    resourceAttributes[resource.Name] = resource.Value.GetString() ?? "";
                }
            }

            // Generate log ID
            var id = $"{timestampNano}_{serviceName}_{severityText}";

            return new Models.LogEntry
            {
                Id = id,
                TimestampNano = timestampNano,
                Body = body,
                SeverityText = severityText,
                SeverityNumber = severityNumber,
                ServiceName = serviceName,
                TraceId = traceId,
                SpanId = spanId,
                ResourceAttributes = resourceAttributes,
                Attributes = attributes
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing log entry");
            return null;
        }
    } // End of Method ParseLogEntry

    private List<LogField> ParseLogFields(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data))
            {
                return new List<LogField>();
            }

            var fields = new List<LogField>();
            foreach (var field in data.EnumerateArray())
            {
                if (
                    field.TryGetProperty("name", out var nameElement)
                    && field.TryGetProperty("type", out var typeElement)
                )
                {
                    var isIndexed = false;
                    if (field.TryGetProperty("indexed", out var indexedElement))
                    {
                        isIndexed = indexedElement.GetBoolean();
                    }

                    fields.Add(
                        new LogField
                        {
                            Name = nameElement.GetString() ?? "",
                            Type = typeElement.GetString() ?? "string",
                            IsIndexed = isIndexed
                        }
                    );
                }
            }

            return fields;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing log fields");
            return new List<LogField>();
        }
    } // End of Method ParseLogFields

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method,
        string path,
        string? jsonPayload,
        CancellationToken cancellationToken
    )
    {
        var request = new HttpRequestMessage(method, path);

        // Add JWT token from authentication service
        var token = await _authService.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (jsonPayload != null)
        {
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        return request;
    } // End of Method CreateAuthenticatedRequestAsync
} // End of Class SigNozLogsService
