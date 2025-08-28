// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Infrastructure.External;

namespace TansuCloud.Identity.Pages.Admin.Providers;

[Authorize(Roles = "Admin")]
public sealed class IndexModel(AppDbContext db) : PageModel
{
    public List<ExternalProviderSetting> Items { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Items = await db.ExternalProviderSettings.AsNoTracking().OrderBy(x => x.TenantId).ThenBy(x => x.Provider).ToListAsync(ct);
    }
} // End of Class IndexModel
