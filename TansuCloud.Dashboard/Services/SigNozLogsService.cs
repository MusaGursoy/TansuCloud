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
/// Uses SigNoz v5 query_range API for structured log search and filtering.
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
            _logger.LogError("===== SEARCH LOGS CALLED =====");
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

            // DEBUG: Log the actual query being sent
            _logger.LogInformation("Log query payload: {Payload}", jsonPayload);

            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                "/api/v5/query_range",
                jsonPayload,
                cancellationToken
            );
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            // DEBUG: Log response status
            _logger.LogInformation("Log search response: {StatusCode}", response.StatusCode);

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Log search failed: {StatusCode}, Response: {Response}",
                    response.StatusCode,
                    responseContent
                );
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            // DEBUG: Log response content
            _logger.LogInformation(
                "Log search response content (first 500 chars): {Content}",
                responseContent.Length > 500 ? responseContent.Substring(0, 500) : responseContent
            );

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
            _logger.LogInformation("Getting available services from logs (v5 API)");

            // Use v5 API with groupBy to get distinct service names
            var now = DateTimeOffset.UtcNow;
            var oneHourAgo = now.AddHours(-1);

            var queryPayload = new
            {
                schemaVersion = "v1",
                start = oneHourAgo.ToUnixTimeMilliseconds(),
                end = now.ToUnixTimeMilliseconds(),
                requestType = "scalar",
                compositeQuery = new
                {
                    queries = new[]
                    {
                        new
                        {
                            type = "builder_query",
                            spec = new
                            {
                                name = "A",
                                signal = "logs",
                                disabled = false,
                                aggregations = new[] { new { expression = "count()" } },
                                filter = new { expression = "" },
                                groupBy = new[]
                                {
                                    new { name = "service.name", fieldContext = "resource" }
                                },
                                limit = 1000,
                                having = new { expression = "" }
                            }
                        }
                    }
                },
                formatOptions = new { formatTableResultForUI = true, fillGaps = false },
                variables = new { }
            };

            var jsonPayload = JsonSerializer.Serialize(queryPayload);
            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                "/api/v5/query_range",
                jsonPayload,
                cancellationToken
            );

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "SigNoz get services failed: {StatusCode}, {ErrorBody}",
                    response.StatusCode,
                    errorBody
                );
                return new List<string>();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(
                cancellationToken: cancellationToken
            );

            return ParseServiceList(jsonResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting services from logs");
            return new List<string>();
        }
    } // End of Method GetServicesAsync

    private List<string> ParseServiceList(JsonElement response)
    {
        var services = new List<string>();

        try
        {
            // V5 API response structure (scalar with groupBy):
            // { "status": "success", "data": { "data": { "results": [ { "columns": [...], "data": [[...]] } ] } } }

            if (!response.TryGetProperty("data", out var outerData))
                return services;

            if (!outerData.TryGetProperty("data", out var innerData))
                return services;

            if (
                !innerData.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
            )
                return services;

            foreach (var result in results.EnumerateArray())
            {
                // Find the "service.name" column index
                if (!result.TryGetProperty("columns", out var columns))
                    continue;

                int serviceNameIndex = -1;
                int columnIndex = 0;
                foreach (var column in columns.EnumerateArray())
                {
                    if (
                        column.TryGetProperty("name", out var colName)
                        && colName.GetString() == "service.name"
                    )
                    {
                        serviceNameIndex = columnIndex;
                        break;
                    }
                    columnIndex++;
                }

                if (serviceNameIndex < 0)
                    continue;

                // Extract service names from data arrays
                if (!result.TryGetProperty("data", out var dataRows))
                    continue;

                foreach (var dataRow in dataRows.EnumerateArray())
                {
                    // Each row is an array: ["service-name", count]
                    if (dataRow.GetArrayLength() > serviceNameIndex)
                    {
                        var serviceNameElement = dataRow[serviceNameIndex];
                        if (serviceNameElement.ValueKind == JsonValueKind.String)
                        {
                            var serviceName = serviceNameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(serviceName))
                                services.Add(serviceName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing service list response (v5)");
        }

        return services.Distinct().OrderBy(s => s).ToList();
    } // End of Method ParseServiceList

    private object BuildLogSearchQuery(LogSearchRequest request)
    {
        // Build filter expression for v5 API
        var filterParts = new List<string>();

        // Service name filter
        if (!string.IsNullOrWhiteSpace(request.ServiceName))
        {
            filterParts.Add($"serviceName = '{request.ServiceName}'");
        }

        // Severity filter
        if (!string.IsNullOrWhiteSpace(request.SeverityText))
        {
            filterParts.Add($"severity_text = '{request.SeverityText}'");
        }

        // Text search filter (body contains)
        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            // Escape single quotes in search text
            var escapedSearch = request.SearchText.Replace("'", "''");
            filterParts.Add($"body like '%{escapedSearch}%'");
        }

        // TraceId filter for correlation
        if (!string.IsNullOrWhiteSpace(request.TraceId))
        {
            filterParts.Add($"trace_id = '{request.TraceId}'");
        }

        // SpanId filter for correlation
        if (!string.IsNullOrWhiteSpace(request.SpanId))
        {
            filterParts.Add($"span_id = '{request.SpanId}'");
        }

        var filterExpression = filterParts.Count > 0 ? string.Join(" AND ", filterParts) : "";

        // Convert nanoseconds to milliseconds for v5 API
        var startMs = request.StartTimeNano / 1_000_000;
        var endMs = request.EndTimeNano / 1_000_000;

        // DEBUG: Log time range for troubleshooting
        _logger.LogInformation(
            "Building log query: StartNano={StartNano}, EndNano={EndNano}, StartMs={StartMs}, EndMs={EndMs}, Filter='{Filter}'",
            request.StartTimeNano,
            request.EndTimeNano,
            startMs,
            endMs,
            filterExpression
        );

        // v5 API format based on SigNoz error response testing:
        // For requestType="raw" (list view), orderBy is NOT supported in v5 API
        // SigNoz error: "unknown field \"orderBy\" in query spec"
        return new
        {
            schemaVersion = "v1",
            start = startMs,
            end = endMs,
            requestType = "raw",
            compositeQuery = new
            {
                queries = new object[]
                {
                    new
                    {
                        type = "builder_query",
                        spec = new
                        {
                            name = "A",
                            signal = "logs",
                            disabled = false,
                            filter = new { expression = filterExpression },
                            groupBy = new object[] { },
                            limit = request.Limit,
                            offset = request.Offset
                        }
                    }
                }
            },
            formatOptions = new { formatTableResultForUI = false, fillGaps = false },
            variables = new { }
        };
    } // End of Method BuildLogSearchQuery

    private LogSearchResult ParseLogSearchResponse(string responseContent, int limit)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            // DEBUG: Log response structure (using Warning level to ensure visibility)
            _logger.LogWarning("=== LOG SEARCH RESPONSE STRUCTURE ===");
            _logger.LogWarning(
                "Response JSON (first 1000 chars): {Content}",
                responseContent.Length > 1000 ? responseContent.Substring(0, 1000) : responseContent
            );

            var hasData = root.TryGetProperty("data", out var data);
            _logger.LogWarning("Response has 'data': {HasData}", hasData);

            if (hasData)
            {
                var hasResult = data.TryGetProperty("result", out var resultProp);
                _logger.LogWarning(
                    "Data has 'result': {HasResult}, length: {Length}",
                    hasResult,
                    hasResult ? resultProp.GetArrayLength() : 0
                );

                if (hasResult && resultProp.GetArrayLength() > 0)
                {
                    var first = resultProp[0];
                    _logger.LogWarning(
                        "First result has keys: {Keys}",
                        string.Join(", ", first.EnumerateObject().Select(p => p.Name))
                    );
                }
            }

            if (
                !root.TryGetProperty("data", out data)
                || !data.TryGetProperty("result", out var result)
                || result.GetArrayLength() == 0
            )
            {
                _logger.LogWarning("Response missing data/result or empty result array");
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            var firstResult = result[0];
            
            // v5 API returns logs in 'table' structure for raw queries
            if (!firstResult.TryGetProperty("table", out var tableData))
            {
                _logger.LogWarning("Response missing 'table' property. Available keys: {Keys}",
                    string.Join(", ", firstResult.EnumerateObject().Select(p => p.Name)));
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            _logger.LogWarning("Table structure - ValueKind: {Kind}, Properties: {Props}",
                tableData.ValueKind,
                string.Join(", ", tableData.EnumerateObject().Select(p => $"{p.Name}={p.Value.ValueKind}")));

            // Table should have 'columns' and 'rows'
            if (!tableData.TryGetProperty("columns", out var columns) ||
                !tableData.TryGetProperty("rows", out var rows))
            {
                _logger.LogWarning("Table missing columns or rows");
                return new LogSearchResult
                {
                    Logs = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            _logger.LogWarning("Found {ColumnCount} columns and {RowCount} rows",
                columns.GetArrayLength(), rows.GetArrayLength());

            // Build column name to index mapping
            var columnMap = new Dictionary<string, int>();
            var columnIndex = 0;
            foreach (var col in columns.EnumerateArray())
            {
                if (col.TryGetProperty("name", out var nameElement))
                {
                    columnMap[nameElement.GetString() ?? ""] = columnIndex;
                }
                columnIndex++;
            }

            _logger.LogWarning("Column mapping: {Columns}",
                string.Join(", ", columnMap.Select(kvp => $"{kvp.Key}={kvp.Value}")));

            var logs = new List<Models.LogEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                try
                {
                    var logEntry = ParseTableRow(row, columnMap);
                    if (logEntry != null)
                    {
                        logs.Add(logEntry);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing table row");
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
        };
    } // End of Method ParseLogSearchResponse

    private Models.LogEntry? ParseTableRow(JsonElement row, Dictionary<string, int> columnMap)
    {
        try
        {
            // Extract values from row array using column mapping
            var GetValue = (string columnName) =>
            {
                if (columnMap.TryGetValue(columnName, out var index) && index < row.GetArrayLength())
                {
                    return row[index];
                }
                return default(JsonElement);
            };

            // Extract timestamp
            var timestampNano = 0L;
            var tsElement = GetValue("timestamp");
            if (tsElement.ValueKind == JsonValueKind.String)
            {
                long.TryParse(tsElement.GetString(), out timestampNano);
            }
            else if (tsElement.ValueKind == JsonValueKind.Number)
            {
                timestampNano = tsElement.GetInt64();
            }

            // Extract body
            var bodyElement = GetValue("body");
            var body = bodyElement.ValueKind == JsonValueKind.String ? bodyElement.GetString() ?? "" : "";

            // Extract severity
            var sevTextElement = GetValue("severity_text");
            var severityText = sevTextElement.ValueKind == JsonValueKind.String ? sevTextElement.GetString() ?? "INFO" : "INFO";
            
            var sevNumElement = GetValue("severity_number");
            var severityNumber = sevNumElement.ValueKind == JsonValueKind.Number ? sevNumElement.GetInt32() : 9;

            // Extract service name
            var serviceName = "";
            var resourcesElement = GetValue("resources_string");
            if (resourcesElement.ValueKind == JsonValueKind.Object)
            {
                if (resourcesElement.TryGetProperty("service.name", out var serviceNameElement))
                {
                    serviceName = serviceNameElement.GetString() ?? "";
                }
            }

            // Extract trace/span IDs
            var traceIdElement = GetValue("trace_id");
            var traceId = traceIdElement.ValueKind == JsonValueKind.String ? traceIdElement.GetString() : null;
            
            var spanIdElement = GetValue("span_id");
            var spanId = spanIdElement.ValueKind == JsonValueKind.String ? spanIdElement.GetString() : null;

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
                ResourceAttributes = new Dictionary<string, string>(),
                Attributes = new Dictionary<string, JsonElement>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing table row");
            return null;
        }
    } // End of Method ParseTableRow

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
