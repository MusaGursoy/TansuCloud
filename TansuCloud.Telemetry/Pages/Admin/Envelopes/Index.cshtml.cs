// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Admin;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Ingestion;

namespace TansuCloud.Telemetry.Pages.Admin.Envelopes;

/// <summary>
/// Displays telemetry envelopes with filtering, paging, and administrative actions.
/// </summary>
public sealed class IndexModel : PageModel
{
    private static readonly string[] SeverityOptions =
    {
        "Trace",
        "Debug",
        "Information",
        "Warning",
        "Error",
        "Critical"
    };

    private readonly TelemetryRepository _repository;
    private readonly IOptionsSnapshot<TelemetryAdminOptions> _adminOptions;
    private readonly ITelemetryIngestionQueue _queue;
    private readonly IOptionsSnapshot<TelemetryIngestionOptions> _ingestionOptions;

    public IndexModel(
        TelemetryRepository repository,
        IOptionsSnapshot<TelemetryAdminOptions> adminOptions,
        ITelemetryIngestionQueue queue,
        IOptionsSnapshot<TelemetryIngestionOptions> ingestionOptions
    )
    {
        _repository = repository;
        _adminOptions = adminOptions;
        _queue = queue;
        _ingestionOptions = ingestionOptions;
    } // End of Constructor IndexModel

    [BindProperty(SupportsGet = true)]
    public TelemetryEnvelopeListRequest Filter { get; set; } = new(); // End of Property Filter

    public IReadOnlyList<TelemetryEnvelopeSummary> Envelopes { get; private set; } =
        Array.Empty<TelemetryEnvelopeSummary>(); // End of Property Envelopes

    public long TotalCount { get; private set; } // End of Property TotalCount

    public int CurrentPage { get; private set; } = 1; // End of Property CurrentPage

    public int PageSize { get; private set; } // End of Property PageSize

    public int TotalPages { get; private set; } // End of Property TotalPages

    public DateTime LastRefreshedUtc { get; private set; } = DateTime.UtcNow; // End of Property LastRefreshedUtc

    public int AcknowledgedOnPage => Envelopes.Count(e => e.IsAcknowledged); // End of Property AcknowledgedOnPage

    public int ArchivedOnPage => Envelopes.Count(e => e.IsDeleted); // End of Property ArchivedOnPage

    public int ActiveOnPage => Envelopes.Count(e => !e.IsDeleted); // End of Property ActiveOnPage

    public int ItemCountOnPage => Envelopes.Sum(e => e.ItemCount); // End of Property ItemCountOnPage

    public IReadOnlyList<string> SeverityChoices => SeverityOptions; // End of Property SeverityChoices

    public bool HasResults => Envelopes.Count > 0; // End of Property HasResults

    public int QueueDepth { get; private set; } // End of Property QueueDepth

    public int QueueCapacity { get; private set; } // End of Property QueueCapacity

    public double QueueUsageFraction { get; private set; } // End of Property QueueUsageFraction

    public int QueueUsagePercentage =>
        (int)Math.Round(QueueUsageFraction * 100, MidpointRounding.AwayFromZero); // End of Property QueueUsagePercentage

    public int ExportLimit { get; private set; } // End of Property ExportLimit

    public string FormatTimestamp(DateTime utcTimestamp)
    {
        return utcTimestamp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    } // End of Method FormatTimestamp

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        LastRefreshedUtc = DateTime.UtcNow;

        QueueDepth = _queue.GetDepth();
        var capacity = Math.Max(1, _ingestionOptions.Value.QueueCapacity);
        QueueCapacity = capacity;
        QueueUsageFraction = Math.Clamp(QueueDepth / (double)capacity, 0d, 1d);

        var options = _adminOptions.Value;
        ExportLimit = Math.Max(1, options.MaxExportItems);

        if (
            !TelemetryEnvelopeRequestProcessor.TryCreateQuery(
                Filter,
                options,
                out var query,
                out var validationErrors
            )
        )
        {
            ApplyValidationErrors(validationErrors);
            NormalizeFilter(options, null);
            PageSize = options.DefaultPageSize;
            TotalPages = 0;
            Envelopes = Array.Empty<TelemetryEnvelopeSummary>();
            TotalCount = 0;
            return Page();
        }

    NormalizeFilter(options, query);

        var result = await _repository
            .QueryEnvelopesAsync(query, cancellationToken)
            .ConfigureAwait(false);

        var summaries = result.Items.Select(TelemetryAdminMapper.ToSummary).ToArray();

        if (query.Page > 1 && summaries.Length == 0 && result.TotalCount > 0)
        {
            Filter.Page = 1;
            return RedirectToPage(null, BuildRedirectRoute(pageOverride: 1));
        }

        Envelopes = summaries;
        TotalCount = result.TotalCount;
        PageSize = query.PageSize;
        CurrentPage = query.Page;
        TotalPages = CalculateTotalPages(result.TotalCount, query.PageSize);

        return Page();
    } // End of Method OnGetAsync

    public async Task<IActionResult> OnPostAcknowledgeAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var acknowledged = await _repository
            .TryAcknowledgeAsync(id, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (acknowledged)
        {
            SetStatusMessage("success", "Envelope acknowledgement recorded or already present.");
        }
        else
        {
            SetStatusMessage(
                "error",
                "Envelope could not be acknowledged. It may no longer exist or is archived."
            );
        }

        return RedirectToPage(null, BuildRedirectRoute());
    } // End of Method OnPostAcknowledgeAsync

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _repository
            .TrySoftDeleteAsync(id, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (deleted)
        {
            SetStatusMessage("success", "Envelope archived successfully.");
        }
        else
        {
            SetStatusMessage(
                "error",
                "Envelope could not be archived. It may already be archived or missing."
            );
        }

        return RedirectToPage(null, BuildRedirectRoute());
    } // End of Method OnPostDeleteAsync

    private void ApplyValidationErrors(IReadOnlyDictionary<string, string[]> validationErrors)
    {
        foreach (var error in validationErrors)
        {
            var propertyName = error.Key.ToLowerInvariant() switch
            {
                "page" => nameof(TelemetryEnvelopeListRequest.Page),
                "pagesize" => nameof(TelemetryEnvelopeListRequest.PageSize),
                "fromutc" => nameof(TelemetryEnvelopeListRequest.FromUtc),
                "toutc" => nameof(TelemetryEnvelopeListRequest.ToUtc),
                _ => error.Key
            };

            foreach (var message in error.Value)
            {
                ModelState.AddModelError($"Filter.{propertyName}", message);
            }
        }
    } // End of Method ApplyValidationErrors

    private void NormalizeFilter(TelemetryAdminOptions options, TelemetryEnvelopeQuery? query)
    {
        var currentPage = Filter.Page.GetValueOrDefault(1);
        Filter.Page = query?.Page ?? Math.Max(currentPage, 1);

        var desiredPageSize = query?.PageSize
            ?? Filter.PageSize
            ?? options.DefaultPageSize;
        if (desiredPageSize <= 0)
        {
            desiredPageSize = options.DefaultPageSize;
        }

        Filter.PageSize = Math.Clamp(desiredPageSize, 1, options.MaxPageSize);

        Filter.Service = NormalizeString(Filter.Service);
        Filter.Host = NormalizeString(Filter.Host);
        Filter.Environment = NormalizeString(Filter.Environment);
        Filter.SeverityThreshold = NormalizeString(Filter.SeverityThreshold);
        Filter.Search = NormalizeString(Filter.Search);

        if (Filter.FromUtc.HasValue)
        {
            Filter.FromUtc = Filter.FromUtc.Value.ToUniversalTime();
        }

        if (Filter.ToUtc.HasValue)
        {
            Filter.ToUtc = Filter.ToUtc.Value.ToUniversalTime();
        }

        if (Filter.Acknowledged.HasValue)
        {
            Filter.IncludeAcknowledged = true;
        }

        if (Filter.Deleted.HasValue)
        {
            Filter.IncludeDeleted = true;
        }
    } // End of Method NormalizeFilter

    private static string? NormalizeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim(); // End of Method NormalizeString

    private void SetStatusMessage(string type, string message)
    {
        TempData["StatusMessageType"] = type;
        TempData["StatusMessage"] = message;
    } // End of Method SetStatusMessage

    private static int CalculateTotalPages(long totalCount, int pageSize)
    {
        if (pageSize <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling(totalCount / (double)pageSize);
    } // End of Method CalculateTotalPages

    public IDictionary<string, string> BuildRouteParameters(int page)
    {
        var pageSize = Filter.PageSize.GetValueOrDefault(_adminOptions.Value.DefaultPageSize);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Page"] = page.ToString(CultureInfo.InvariantCulture),
            ["PageSize"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["IncludeAcknowledged"] = Filter.IncludeAcknowledged
                ? bool.TrueString.ToLowerInvariant()
                : bool.FalseString.ToLowerInvariant(),
            ["IncludeDeleted"] = Filter.IncludeDeleted
                ? bool.TrueString.ToLowerInvariant()
                : bool.FalseString.ToLowerInvariant()
        };

        AddIfPresent(parameters, "Service", Filter.Service);
        AddIfPresent(parameters, "Host", Filter.Host);
        AddIfPresent(parameters, "Environment", Filter.Environment);
        AddIfPresent(parameters, "SeverityThreshold", Filter.SeverityThreshold);
        AddIfPresent(parameters, "Search", Filter.Search);

        if (Filter.Acknowledged.HasValue)
        {
            parameters["Acknowledged"] = Filter.Acknowledged.Value
                ? bool.TrueString.ToLowerInvariant()
                : bool.FalseString.ToLowerInvariant();
        }

        if (Filter.Deleted.HasValue)
        {
            parameters["Deleted"] = Filter.Deleted.Value
                ? bool.TrueString.ToLowerInvariant()
                : bool.FalseString.ToLowerInvariant();
        }

        if (Filter.FromUtc.HasValue)
        {
            parameters["FromUtc"] = Filter.FromUtc.Value.ToString("o");
        }

        if (Filter.ToUtc.HasValue)
        {
            parameters["ToUtc"] = Filter.ToUtc.Value.ToString("o");
        }

        return parameters;
    } // End of Method BuildRouteParameters

    public IDictionary<string, string> BuildExportRouteValues()
    {
        var parameters = BuildRouteParameters(Filter.Page.GetValueOrDefault(1));
        parameters.Remove("Page");
        parameters.Remove("PageSize");
        return parameters;
    } // End of Method BuildExportRouteValues

    private RouteValueDictionary BuildRedirectRoute(int? pageOverride = null)
    {
        var targetPage = pageOverride ?? Filter.Page ?? 1;
        var values = new RouteValueDictionary
        {
            ["Page"] = targetPage,
            ["PageSize"] = Filter.PageSize ?? _adminOptions.Value.DefaultPageSize,
            ["Service"] = Filter.Service,
            ["Host"] = Filter.Host,
            ["Environment"] = Filter.Environment,
            ["SeverityThreshold"] = Filter.SeverityThreshold,
            ["IncludeAcknowledged"] = Filter.IncludeAcknowledged,
            ["IncludeDeleted"] = Filter.IncludeDeleted,
            ["Search"] = Filter.Search
        };

        if (Filter.Acknowledged.HasValue)
        {
            values["Acknowledged"] = Filter.Acknowledged.Value;
        }

        if (Filter.Deleted.HasValue)
        {
            values["Deleted"] = Filter.Deleted.Value;
        }

        if (Filter.FromUtc.HasValue)
        {
            values["FromUtc"] = Filter.FromUtc.Value.ToString("o");
        }

        if (Filter.ToUtc.HasValue)
        {
            values["ToUtc"] = Filter.ToUtc.Value.ToString("o");
        }

        return values;
    } // End of Method BuildRedirectRoute

    private static void AddIfPresent(
        IDictionary<string, string> destination,
        string key,
        string? value
    )
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            destination[key] = value.Trim();
        }
    } // End of Method AddIfPresent
} // End of Class IndexModel
