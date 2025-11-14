// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.Loki;

/// <summary>
/// Service for querying Grafana Loki API to retrieve log data.
/// Implements ILokiLogsService using HttpClient for Loki HTTP API access.
/// </summary>
public sealed class LokiLogsService : ILokiLogsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LokiLogsService> _logger;
    private readonly LokiQueryOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LokiLogsService(
        HttpClient httpClient,
        IOptions<LokiQueryOptions> options,
        ILogger<LokiLogsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
    } // End of Constructor LokiLogsService

    public async Task<LokiLogSearchResult> SearchLogsAsync(
        LokiSearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Build LogQL query from filters
            var logQLQuery = BuildLogQLQuery(filters);
            _logger.LogInformation(
                "Executing Loki query: {Query} | Filters: Service={Service}, Level={Level}, StartNano={StartNano}, EndNano={EndNano}, Limit={Limit}",
                logQLQuery,
                filters.ServiceName ?? "null",
                filters.Level ?? "null",
                filters.StartNano,
                filters.EndNano,
                filters.Limit
            );

            // Build query parameters
            var queryParams = new Dictionary<string, string>
            {
                ["query"] = logQLQuery,
                ["limit"] = filters.Limit.ToString(CultureInfo.InvariantCulture),
                ["direction"] = filters.Direction
            };

            // Add time range if specified
            if (filters.StartNano.HasValue)
            {
                queryParams["start"] = filters.StartNano.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (filters.EndNano.HasValue)
            {
                queryParams["end"] = filters.EndNano.Value.ToString(CultureInfo.InvariantCulture);
            }

            // Build request URL
            var queryString = BuildQueryString(queryParams);
            var requestUri = $"/loki/api/v1/query_range?{queryString}";
            _logger.LogDebug("Loki request URI: {BaseAddress}{RequestUri}", _httpClient.BaseAddress, requestUri);

            // Execute HTTP request
            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            _logger.LogInformation("Loki response status: {StatusCode}", response.StatusCode);
            
            response.EnsureSuccessStatusCode();

            // Parse response
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Loki response content: {Content}", responseContent);
            
            var apiResponse = JsonSerializer.Deserialize<LokiQueryRangeResponse>(
                responseContent,
                JsonOptions);

            if (apiResponse?.Data?.Result == null)
            {
                _logger.LogWarning("Loki returned empty or malformed response. Status={Status}, Data={Data}", 
                    apiResponse?.Status ?? "null", 
                    apiResponse?.Data != null ? "not null" : "null");
                return new LokiLogSearchResult();
            }

            _logger.LogInformation("Loki returned {StreamCount} streams", apiResponse.Data.Result.Count);

            // Transform API response to domain model
            var logs = TransformStreamsToLogs(apiResponse.Data.Result);
            var stats = TransformStats(apiResponse.Data, logs.Count);

            _logger.LogInformation("Transformed to {LogCount} log entries", logs.Count);

            return new LokiLogSearchResult
            {
                Logs = logs,
                Stats = stats
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error querying Loki: {Message}", ex.Message);
            return new LokiLogSearchResult(); // Return empty result on error
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error from Loki response: {Message}", ex.Message);
            return new LokiLogSearchResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying Loki: {Message}", ex.Message);
            return new LokiLogSearchResult();
        }
    } // End of Method SearchLogsAsync

    public async Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Query for unique values of service_name label
            var requestUri = "/loki/api/v1/label/service_name/values";
            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<LokiLabelsResponse>(
                JsonOptions,
                cancellationToken);

            if (apiResponse?.Data == null)
            {
                _logger.LogWarning("Loki returned empty service_name values");
                return [];
            }

            return apiResponse.Data;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching services from Loki: {Message}", ex.Message);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching services: {Message}", ex.Message);
            return [];
        }
    } // End of Method GetServicesAsync

    public async Task<List<string>> GetLabelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var requestUri = "/loki/api/v1/labels";
            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<LokiLabelsResponse>(
                JsonOptions,
                cancellationToken);

            if (apiResponse?.Data == null)
            {
                _logger.LogWarning("Loki returned empty labels list");
                return [];
            }

            return apiResponse.Data;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching labels from Loki: {Message}", ex.Message);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching labels: {Message}", ex.Message);
            return [];
        }
    } // End of Method GetLabelsAsync

    public async Task<List<string>> GetLabelValuesAsync(
        string labelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requestUri = $"/loki/api/v1/label/{HttpUtility.UrlEncode(labelName)}/values";
            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<LokiLabelsResponse>(
                JsonOptions,
                cancellationToken);

            if (apiResponse?.Data == null)
            {
                _logger.LogWarning("Loki returned empty values for label {Label}", labelName);
                return [];
            }

            return apiResponse.Data;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching label values from Loki: {Message}", ex.Message);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching label values: {Message}", ex.Message);
            return [];
        }
    } // End of Method GetLabelValuesAsync

    #region Private Helpers

    /// <summary>
    /// Builds a LogQL query string from search filters.
    /// </summary>
    private static string BuildLogQLQuery(LokiSearchFilters filters)
    {
        // If custom LogQL query provided, use it directly
        if (!string.IsNullOrWhiteSpace(filters.LogQLQuery))
        {
            return filters.LogQLQuery;
        }

        // Build label matchers
        var labelMatchers = new List<string>();

        if (!string.IsNullOrWhiteSpace(filters.ServiceName))
        {
            labelMatchers.Add($"service_name=\"{EscapeLogQL(filters.ServiceName)}\"");
        }

        if (!string.IsNullOrWhiteSpace(filters.Level))
        {
            labelMatchers.Add($"level=\"{EscapeLogQL(filters.Level)}\"");
        }

        // Build stream selector
        // Note: Loki's /query_range endpoint requires at least one label matcher
        // Use wildcard service_name matcher if no filters provided
        var streamSelector = labelMatchers.Count > 0
            ? $"{{{string.Join(",", labelMatchers)}}}"
            : "{service_name=~\".+\"}"; // Match all services with non-empty service_name

        // Return LogQL query with JSON parsing for structured logs
        return $"{streamSelector} | json";
    } // End of Method BuildLogQLQuery

    /// <summary>
    /// Escapes special characters in LogQL query strings.
    /// </summary>
    private static string EscapeLogQL(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    } // End of Method EscapeLogQL

    /// <summary>
    /// Builds URL query string from parameters dictionary.
    /// </summary>
    private static string BuildQueryString(Dictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }
            sb.Append(HttpUtility.UrlEncode(key));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value));
        }
        return sb.ToString();
    } // End of Method BuildQueryString

    /// <summary>
    /// Transforms Loki streams (grouped by labels) into flat list of log entries.
    /// </summary>
    private List<LokiLogEntry> TransformStreamsToLogs(List<LokiStream> streams)
    {
        var logs = new List<LokiLogEntry>();

        foreach (var stream in streams)
        {
            if (stream.Stream == null || stream.Values == null)
            {
                continue;
            }

            foreach (var value in stream.Values)
            {
                if (value.Count < 2)
                {
                    _logger.LogWarning("Malformed log entry in stream: expected [timestamp, message]");
                    continue;
                }

                // Parse timestamp (first element is Unix nanoseconds as string)
                if (!long.TryParse(value[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestampNano))
                {
                    _logger.LogWarning("Invalid timestamp in log entry: {Timestamp}", value[0]);
                    continue;
                }

                // Message is second element
                var message = value[1];

                logs.Add(new LokiLogEntry
                {
                    TimestampNano = timestampNano,
                    Message = message,
                    Labels = new Dictionary<string, string>(stream.Stream)
                });
            }
        }

        return logs;
    } // End of Method TransformStreamsToLogs

    /// <summary>
    /// Transforms Loki API stats into domain model.
    /// </summary>
    private LokiSearchStats? TransformStats(LokiQueryData data, int totalEntries)
    {
        if (data.Stats?.Summary == null)
        {
            return null;
        }

        return new LokiSearchStats
        {
            TotalEntriesReturned = totalEntries,
            StreamsQueried = data.Result?.Count ?? 0
        };
    } // End of Method TransformStats

    #endregion
} // End of Class LokiLogsService
