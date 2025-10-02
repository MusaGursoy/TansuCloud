// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Contracts;

namespace TansuCloud.Dashboard.Observability.Logging
{
    public sealed class NoopLogReporter : ILogReporter
    {
        public Task ReportAsync(
            LogReportRequest request,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask; // End of Method ReportAsync
    } // End of Class NoopLogReporter

    public sealed class HttpLogReporter : ILogReporter
    {
        private readonly HttpClient _http;
        private readonly LogReportingOptions _options;

        public HttpLogReporter(HttpClient http, LogReportingOptions options)
        {
            _http = http;
            _options = options;
            if (_options.HttpTimeoutSeconds > 0)
            {
                _http.Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds);
            }
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    _options.ApiKey
                );
            }
        } // End of Constructor HttpLogReporter

        public async Task ReportAsync(
            LogReportRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(_options.MainServerUrl))
                return;

            var url = _options.MainServerUrl!.Trim();
            var json = JsonSerializer.Serialize(
                request,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }
            );
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Log report failed with status {(int)resp.StatusCode} {resp.ReasonPhrase}"
                );
            }
        } // End of Method ReportAsync
    } // End of Class HttpLogReporter

    /// <summary>
    /// Reporter that queries the latest <see cref="LogReportingOptions"/> for each request.
    /// </summary>
    public sealed class ConfigurableLogReporter : ILogReporter
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IOptionsMonitor<LogReportingOptions> _options;

        public ConfigurableLogReporter(
            IHttpClientFactory httpFactory,
            IOptionsMonitor<LogReportingOptions> options
        )
        {
            _httpFactory = httpFactory;
            _options = options;
        } // End of Constructor ConfigurableLogReporter

        public async Task ReportAsync(
            LogReportRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var current = _options.CurrentValue;
            if (!current.Enabled || string.IsNullOrWhiteSpace(current.MainServerUrl))
            {
                return;
            }

            var client = _httpFactory.CreateClient("log-reporter");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (current.HttpTimeoutSeconds > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(current.HttpTimeoutSeconds));
            }

            var json = JsonSerializer.Serialize(
                request,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }
            );
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                current.MainServerUrl!.Trim()
            )
            {
                Content = content
            };
            httpRequest.Headers.Accept.Clear();
            httpRequest.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(current.ApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    current.ApiKey
                );
            }

            using var response = await client
                .SendAsync(httpRequest, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response
                    .Content.ReadAsStringAsync(cts.Token)
                    .ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Log report failed with status {(int)response.StatusCode} {response.ReasonPhrase}: {body}"
                );
            }
        } // End of Method ReportAsync
    } // End of Class ConfigurableLogReporter
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
