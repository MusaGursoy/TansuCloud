// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TansuCloud.Identity.Pages.Admin.Impersonation;

[Authorize(Roles = "Admin")]
public sealed class IndexModel(IHttpClientFactory httpClientFactory) : PageModel
{
    [BindProperty]
    public string UserId { get; set; } = string.Empty;

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(UserId))
        {
            var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(sub))
            {
                UserId = sub;
            }
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("local");
        if (string.IsNullOrWhiteSpace(UserId))
        {
            var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(sub))
            {
                UserId = sub;
            }
        }
        var resp = await http.PostAsJsonAsync(
            "/admin/impersonation/start",
            new { userId = UserId },
            ct
        );
        TempData["Message"] = await resp.Content.ReadAsStringAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostEndAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("local");
        var resp = await http.PostAsync(
            "/admin/impersonation/end?userId=" + Uri.EscapeDataString(UserId),
            null,
            ct
        );
        TempData["Message"] = await resp.Content.ReadAsStringAsync(ct);
        return Page();
    }
} // End of Class IndexModel
