// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
