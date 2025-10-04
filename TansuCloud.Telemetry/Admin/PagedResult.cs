// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Response payload returned by the telemetry envelopes listing endpoint.
/// </summary>
/// <param name="TotalCount">The total number of envelopes matching the filter.</param>
/// <param name="Envelopes">The envelopes contained in the current page.</param>
public sealed record TelemetryEnvelopeListResponse(
    long TotalCount,
    IReadOnlyList<TelemetryEnvelopeSummary> Envelopes
); // End of Record TelemetryEnvelopeListResponse
