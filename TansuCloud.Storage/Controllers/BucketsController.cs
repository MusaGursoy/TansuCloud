// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/buckets")]
public sealed class BucketsController(IObjectStorage storage, ILogger<BucketsController> logger)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "storage.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await storage.ListBucketsAsync(ct);
        return Ok(list);
    }

    [HttpPut("{bucket}")]
    [Authorize(Policy = "storage.write")]
    public async Task<IActionResult> Create(string bucket, CancellationToken ct)
    {
        await storage.CreateBucketAsync(bucket, ct);
        logger.LogInformation("Created bucket {Bucket}", bucket);
        return Created($"/api/buckets/{bucket}", new { bucket });
    }

    [HttpDelete("{bucket}")]
    [Authorize(Policy = "storage.write")]
    public async Task<IActionResult> Delete(string bucket, CancellationToken ct)
    {
        var ok = await storage.DeleteBucketAsync(bucket, ct);
        if (!ok)
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: "Bucket not empty or not found"
            );
        logger.LogInformation("Deleted bucket {Bucket}", bucket);
        return NoContent();
    }
} // End of Class BucketsController
