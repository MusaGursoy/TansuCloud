// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/usage")]
public sealed class UsageController(IQuotaService quotas, Microsoft.Extensions.Options.IOptions<StorageOptions> opts) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "storage.read")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var (total, count) = await quotas.GetUsageAsync(ct);
        var limits = opts.Value.Quotas ?? new QuotaLimits();
        return Ok(new
        {
            totalBytes = total,
            objectCount = count,
            maxTotalBytes = limits.MaxTotalBytes,
            maxObjectCount = limits.MaxObjectCount,
            maxObjectSizeBytes = limits.MaxObjectSizeBytes
        });
    }
} // End of Class UsageController
