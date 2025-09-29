// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.SigNoz;

public sealed class SigNozOptions
{
    public const string SectionName = "SigNoz";

    public string? BaseUrl { get; set; } // End of Property BaseUrl
} // End of Class SigNozOptions

public sealed record SigNozChartTemplate(
    string Id,
    string Title,
    string Description,
    string RelativePath,
    string Category
); // End of Record SigNozChartTemplate

public sealed record SigNozChartDescriptor(
    string Id,
    string Title,
    string Description,
    string Category,
    string SigNozUrl
); // End of Record SigNozChartDescriptor

public sealed record SigNozCatalogResponse(
    string BaseUrl,
    IReadOnlyList<SigNozChartDescriptor> Charts
) // End of Record SigNozCatalogResponse
;

public sealed record SigNozRedirectResponse(
    string ChartId,
    string SigNozUrl,
    string Title,
    string Description
); // End of Record SigNozRedirectResponse

public sealed class SigNozMetricsCatalog
{
    private readonly IOptionsMonitor<SigNozOptions> _options;
    private readonly IReadOnlyDictionary<string, SigNozChartTemplate> _templates;

    public SigNozMetricsCatalog(IOptionsMonitor<SigNozOptions> options)
    {
        _options = options;
        _templates = new Dictionary<string, SigNozChartTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["overview.http.rps"] = new SigNozChartTemplate(
                "overview.http.rps",
                "Overall request throughput",
                "Cluster-wide HTTP requests per second across gateway and services.",
                "dashboard",
                "Overview"
            ),
            ["gateway.http.rps"] = new SigNozChartTemplate(
                "gateway.http.rps",
                "Gateway HTTP throughput",
                "Requests handled by the gateway including proxied API calls.",
                "services/tansu.gateway?tab=metrics",
                "Gateway"
            ),
            ["storage.http.rps"] = new SigNozChartTemplate(
                "storage.http.rps",
                "Storage API throughput",
                "Requests per second, error rate, and latency for the storage service.",
                "services/tansu.storage?tab=metrics",
                "Storage"
            ),
            ["database.http.rps"] = new SigNozChartTemplate(
                "database.http.rps",
                "Database API throughput",
                "Request volume, latency, and failures for the database service.",
                "services/tansu.database?tab=metrics",
                "Database"
            ),
            ["identity.signin.rate"] = new SigNozChartTemplate(
                "identity.signin.rate",
                "Identity sign-in activity",
                "Interactive sign-in, token issuance, and error trends for Identity.",
                "services/tansu.identity?tab=metrics",
                "Identity"
            )
        };
    } // End of Constructor SigNozMetricsCatalog

    public SigNozCatalogResponse CreateCatalog()
    {
        var baseUrl = NormalizeBaseUrl(_options.CurrentValue.BaseUrl);
        var charts = _templates
            .Values.Select(template => new SigNozChartDescriptor(
                template.Id,
                template.Title,
                template.Description,
                template.Category,
                CombineUrl(baseUrl, template.RelativePath)
            ))
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SigNozCatalogResponse(baseUrl, charts);
    } // End of Method CreateCatalog

    public bool TryResolve(string? chartId, out SigNozRedirectResponse redirect)
    {
        redirect = default!;
        if (string.IsNullOrWhiteSpace(chartId))
        {
            return false;
        }

        if (!_templates.TryGetValue(chartId, out var template))
        {
            return false;
        }

        var baseUrl = NormalizeBaseUrl(_options.CurrentValue.BaseUrl);
        var url = CombineUrl(baseUrl, template.RelativePath);
        redirect = new SigNozRedirectResponse(
            template.Id,
            url,
            template.Title,
            template.Description
        );
        return true;
    } // End of Method TryResolve

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        // Allow configuration via SigNoz:BaseUrl, SIG_NOZ_BASE_URL, or fall back to PublicBaseUrl when reasonable
        var effective = string.IsNullOrWhiteSpace(baseUrl)
            ? (
                Environment.GetEnvironmentVariable("SIG_NOZ_BASE_URL")
                ?? Environment.GetEnvironmentVariable("SigNoz__BaseUrl")
                ?? string.Empty
            )
            : baseUrl.Trim();
        if (string.IsNullOrWhiteSpace(effective))
        {
            // As a last resort (dev ergonomics): if PublicBaseUrl is set and SigNoz is commonly at 3301 in dev,
            // prefer that default; otherwise just return a reasonable http://127.0.0.1:3301/ dev URL.
            var publicBase =
                Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                ?? Environment.GetEnvironmentVariable("PublicBaseUrl");
            if (!string.IsNullOrWhiteSpace(publicBase))
            {
                try
                {
                    var uri = new Uri(publicBase.Trim());
                    effective = $"http://{uri.Host}:3301/";
                }
                catch
                {
                    effective = "http://127.0.0.1:3301/";
                }
            }
            else
            {
                effective = "http://127.0.0.1:3301/";
            }
        }
        if (!effective.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedHost = effective.Trim('/');
            effective = $"http://{normalizedHost}/";
        }
        if (!effective.EndsWith('/'))
        {
            effective += "/";
        }
        return effective;
    } // End of Method NormalizeBaseUrl

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return baseUrl;
        }

        var trimmedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var trimmedRelative = relativePath.TrimStart('/');
        return trimmedBase + trimmedRelative;
    } // End of Method CombineUrl
} // End of Class SigNozMetricsCatalog
