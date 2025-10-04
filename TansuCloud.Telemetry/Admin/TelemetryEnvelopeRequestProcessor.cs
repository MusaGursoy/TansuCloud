// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.ObjectModel;
using System.Globalization;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Data;

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Processes <see cref="TelemetryEnvelopeListRequest"/> instances into
/// <see cref="TelemetryEnvelopeQuery"/> values with consistent validation.
/// </summary>
internal static class TelemetryEnvelopeRequestProcessor
{
    private static readonly IReadOnlyDictionary<string, string[]> EmptyErrors =
        new ReadOnlyDictionary<string, string[]>(
            new Dictionary<string, string[]>(0, StringComparer.OrdinalIgnoreCase)
        );

    /// <summary>
    /// Validates the incoming request and creates a normalized query representation.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="options">The admin options controlling paging limits.</param>
    /// <param name="query">When successful, contains the normalized query.</param>
    /// <param name="validationErrors">Any validation errors discovered during processing.</param>
    /// <returns><see langword="true"/> when the request is valid; otherwise <see langword="false"/>.</returns>
    public static bool TryCreateQuery(
        TelemetryEnvelopeListRequest request,
        TelemetryAdminOptions options,
        out TelemetryEnvelopeQuery query,
        out IReadOnlyDictionary<string, string[]> validationErrors
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        var normalizedPage = request.Page.GetValueOrDefault(1);
        if (normalizedPage <= 0)
        {
            normalizedPage = 1;
        }

        var requestedPageSize = request.PageSize.GetValueOrDefault(options.DefaultPageSize);
        if (requestedPageSize <= 0)
        {
            requestedPageSize = options.DefaultPageSize;
        }

        if (requestedPageSize > options.MaxPageSize)
        {
            errors["pageSize"] = new[]
            {
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PageSize cannot exceed {0}.",
                    options.MaxPageSize
                )
            };
        }

        if (request.FromUtc.HasValue && request.ToUtc.HasValue && request.FromUtc > request.ToUtc)
        {
            errors["fromUtc"] = new[] { "FromUtc must be earlier than or equal to ToUtc." };
        }

        if (errors.Count > 0)
        {
            query = default!;
            validationErrors = new ReadOnlyDictionary<string, string[]>(errors);
            return false;
        }

        static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        query = new TelemetryEnvelopeQuery
        {
            Page = Math.Max(normalizedPage, 1),
            PageSize = Math.Clamp(requestedPageSize, 1, options.MaxPageSize),
            Service = Normalize(request.Service),
            Host = Normalize(request.Host),
            Environment = Normalize(request.Environment),
            SeverityThreshold = Normalize(request.SeverityThreshold),
            FromUtc = request.FromUtc?.UtcDateTime,
            ToUtc = request.ToUtc?.UtcDateTime,
            Acknowledged = request.Acknowledged,
            Deleted = request.Deleted,
            IncludeAcknowledged = request.Acknowledged.HasValue ? true : request.IncludeAcknowledged,
            IncludeDeleted = request.Deleted.HasValue ? true : request.IncludeDeleted,
            Search = Normalize(request.Search)
        };

        validationErrors = EmptyErrors;
        return true;
    } // End of Method TryCreateQuery
} // End of Class TelemetryEnvelopeRequestProcessor
