// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TansuCloud.Telemetry.Admin;
using TansuCloud.Telemetry.Data;

namespace TansuCloud.Telemetry.Pages.Admin.Envelopes;

/// <summary>
/// Displays a single telemetry envelope with full detail.
/// </summary>
public sealed class DetailModel : PageModel
{
    private readonly TelemetryRepository _repository;

    public DetailModel(TelemetryRepository repository)
    {
        _repository = repository;
    } // End of Constructor DetailModel

    public TelemetryEnvelopeDetail? Envelope { get; private set; } // End of Property Envelope

    public IReadOnlyList<TelemetryItemView> Items =>
        Envelope?.Items ?? Array.Empty<TelemetryItemView>(); // End of Property Items

    public bool IsAcknowledged => Envelope?.IsAcknowledged ?? false; // End of Property IsAcknowledged

    public bool IsArchived => Envelope?.IsDeleted ?? false; // End of Property IsArchived

    public int ItemCount => Items.Count; // End of Property ItemCount

    public DateTime? FirstEventUtc => Items.Count == 0 ? null : Items.Min(i => i.TimestampUtc); // End of Property FirstEventUtc

    public DateTime? LastEventUtc => Items.Count == 0 ? null : Items.Max(i => i.TimestampUtc); // End of Property LastEventUtc

    public string FormatTimestamp(DateTime utcTimestamp)
    {
        return utcTimestamp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    } // End of Method FormatTimestamp

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _repository
            .GetEnvelopeAsync(id, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            SetStatusMessage("error", "Envelope could not be found. It may have been removed.");
            return RedirectToPage("./Index");
        }

        Envelope = TelemetryAdminMapper.ToDetail(entity);
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
            SetStatusMessage("success", "Envelope acknowledged.");
        }
        else
        {
            SetStatusMessage(
                "error",
                "Envelope could not be acknowledged. It may be archived or missing."
            );
        }

        return RedirectToPage(null, new { id });
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

        return RedirectToPage(null, new { id });
    } // End of Method OnPostDeleteAsync

    private void SetStatusMessage(string type, string message)
    {
        TempData["StatusMessageType"] = type;
        TempData["StatusMessage"] = message;
    } // End of Method SetStatusMessage
} // End of Class DetailModel
