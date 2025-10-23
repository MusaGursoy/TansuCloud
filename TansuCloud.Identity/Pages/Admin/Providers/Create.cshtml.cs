// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Data.Entities;

namespace TansuCloud.Identity.Pages.Admin.Providers;

[Authorize(Roles = "Admin")]
public sealed class CreateModel(AppDbContext db) : PageModel
{
    [BindProperty]
    public ExternalProviderSetting Item { get; set; } = new();

    public void OnGet()
    {
        Item.Provider = "oidc";
        Item.Scopes = "openid profile email";
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Page();

        await db.ExternalProviderSettings.AddAsync(Item, ct);
        await db.SaveChangesAsync(ct);
        return RedirectToPage("Index");
    }
} // End of Class CreateModel
