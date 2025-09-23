// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Observability.Auditing;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/buckets")]
public sealed class BucketsController(
    IObjectStorage storage,
    ILogger<BucketsController> logger,
    IAuditLogger audit
) : ControllerBase
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
        // Audit (Storage:BucketCreate)
        audit.TryEnqueueRedacted(
            new AuditEvent { Action = "BucketCreate", Category = "Storage", Outcome = "Success" },
            new { Bucket = bucket },
            new[] { "Bucket" }
        );
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
        // Audit (Storage:BucketDelete)
        audit.TryEnqueueRedacted(
            new AuditEvent { Action = "BucketDelete", Category = "Storage", Outcome = ok ? "Success" : "Failure", ReasonCode = ok ? null : "BucketNotEmptyOrNotFound" },
            new { Bucket = bucket },
            new[] { "Bucket" }
        );
        return NoContent();
    }
} // End of Class BucketsController
