// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Database.EF;
using TansuCloud.Database.Services;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using TansuCloud.Database.Caching;

namespace TansuCloud.Database.Controllers;

[ApiController]
[Route("api/collections")]
public sealed class CollectionsController(
    ITenantDbContextFactory factory,
    ILogger<CollectionsController> logger,
    TansuCloud.Database.Outbox.IOutboxProducer outbox,
    Microsoft.Extensions.Caching.Hybrid.HybridCache? cache = null,
    ITenantCacheVersion? versions = null
) : ControllerBase
{
    private readonly ITenantDbContextFactory _factory = factory;
    private readonly ILogger<CollectionsController> _logger = logger;
    private readonly TansuCloud.Database.Outbox.IOutboxProducer _outbox = outbox;
    private readonly Microsoft.Extensions.Caching.Hybrid.HybridCache? _cache = cache;
    private readonly ITenantCacheVersion? _versions = versions;

    private string Tenant() => Request.Headers["X-Tansu-Tenant"].ToString();
    private string Key(params string[] parts)
    {
        var tenant = Tenant();
        var ver = _versions?.Get(tenant) ?? 0;
        return $"t:{tenant}:v{ver}:db:collections:{string.Join(':', parts)}";
    }

    public sealed record CreateCollectionDto(string name);

    public sealed record CollectionDto(Guid id, string name, DateTimeOffset createdAt);

    // Conditional helpers (weak ETag compare + header parsing)
    private static bool WeakETagEquals(string? a, string? b)
    {
        static string Norm(string? s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.StartsWith("W/", StringComparison.Ordinal))
                s = s.Substring(2).Trim();
            if (s.Length > 1 && s[0] == '"' && s[^1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s;
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
    } // End of Method WeakETagEquals

    private static bool AnyIfNoneMatchMatches(
        Microsoft.Extensions.Primitives.StringValues values,
        string current
    )
    {
        foreach (var raw in values)
        {
            if (raw is null)
                continue;
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim();
                if (t == "*")
                    return true;
                if (WeakETagEquals(t, current))
                    return true;
            }
        }
        return false;
    } // End of Method AnyIfNoneMatchMatches

    private static bool AnyIfMatchMatches(
        Microsoft.Extensions.Primitives.StringValues values,
        string current
    )
    {
        foreach (var raw in values)
        {
            if (raw is null)
                continue;
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim();
                if (t == "*")
                    return true;
                if (WeakETagEquals(t, current))
                    return true;
            }
        }
        return false;
    } // End of Method AnyIfMatchMatches

    [HttpGet]
    [Authorize(Policy = "db.read")]
    public async Task<ActionResult<object>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default
    )
    {
        if (page <= 0 || pageSize <= 0 || pageSize > 500)
            return Problem(
                title: "Invalid pagination",
                statusCode: StatusCodes.Status400BadRequest
            );

    await using var db = await _factory.CreateAsync(HttpContext, ct);
        var q = db.Collections.AsNoTracking().OrderByDescending(c => c.CreatedAt);
        var total = await q.CountAsync(ct);
        var etag = ETagHelper.ComputeWeakETag(
            total.ToString(),
            page.ToString(),
            pageSize.ToString()
        );
        // If-None-Match short-circuit
        var inm = Request.Headers.IfNoneMatch;
        if (
            !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(inm)
            && AnyIfNoneMatchMatches(inm, etag)
        )
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Try cache for list page
    var cacheKey = Key("list", page.ToString(), pageSize.ToString(), etag);
        if (_cache is not null)
        {
            var cached = await _cache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    var items = await q.Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(c => new CollectionDto(c.Id, c.Name, c.CreatedAt))
                        .ToListAsync(ct);
                    return new { total, page, pageSize, items } as object;
                }
            );
            Response.Headers.ETag = etag;
            return Ok(cached);
        }

        var items = await q.Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CollectionDto(c.Id, c.Name, c.CreatedAt))
            .ToListAsync(ct);
        Response.Headers.ETag = etag;
        return Ok(new { total, page, pageSize, items });
    } // End of Method List

    [HttpPost]
    [Authorize(Policy = "db.write")]
    public async Task<ActionResult<CollectionDto>> Create(
        [FromBody] CreateCollectionDto input,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(input.name))
            return Problem(title: "name is required", statusCode: StatusCodes.Status400BadRequest);
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var entity = new Collection
        {
            Id = Guid.NewGuid(),
            Name = input.name,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Collections.Add(entity);
        // outbox event (transactional with this SaveChanges)
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new { tenant, collectionId = entity.Id, op = "collection.created" };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "collection.created", payloadDoc);
        }
        catch { }
        await db.SaveChangesAsync(ct);
    try { _versions?.Increment(Tenant()); } catch { }
        var dto = new CollectionDto(entity.Id, entity.Name, entity.CreatedAt);
        Response.Headers.ETag = ETagHelper.ComputeWeakETag(
            entity.Id.ToString(),
            entity.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, dto);
    } // End of Method Create

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "db.read")]
    public async Task<ActionResult<CollectionDto>> Get([FromRoute] Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
    var cacheKey = Key("item", id.ToString());
        if (_cache is not null)
        {
            var cached = await _cache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    var e0 = await db.Collections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    return e0 is null ? null : new CollectionDto(e0.Id, e0.Name, e0.CreatedAt);
                }
            );
            if (cached is null) return NotFound();
            var etag0 = ETagHelper.ComputeWeakETag(cached.id.ToString(), cached.createdAt.ToUnixTimeMilliseconds().ToString());
            var inm0 = Request.Headers.IfNoneMatch;
            if (!Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(inm0) && AnyIfNoneMatchMatches(inm0, etag0))
            {
                Response.Headers.ETag = etag0;
                return StatusCode(StatusCodes.Status304NotModified);
            }
            Response.Headers.ETag = etag0;
            return Ok(cached);
        }

        var e = await db.Collections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();
        var etag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var inm = Request.Headers.IfNoneMatch;
        if (
            !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(inm)
            && AnyIfNoneMatchMatches(inm, etag)
        )
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }
        Response.Headers.ETag = etag;
        return Ok(new CollectionDto(e.Id, e.Name, e.CreatedAt));
    } // End of Method Get

    public sealed record UpdateCollectionDto(string name);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "db.write")]
    public async Task<ActionResult<CollectionDto>> Update(
        [FromRoute] Guid id,
        [FromBody] UpdateCollectionDto input,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(input.name))
            return Problem(title: "name is required", statusCode: StatusCodes.Status400BadRequest);
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var e = await db.Collections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();
        // If-Match precondition (when provided)
        var currentEtag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var ifm = Request.Headers.IfMatch;
        if (
            !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(ifm)
            && !AnyIfMatchMatches(ifm, currentEtag)
        )
        {
            Response.Headers.ETag = currentEtag;
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }
    e.Name = input.name;
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new { tenant, collectionId = e.Id, op = "collection.updated" };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "collection.updated", payloadDoc);
        }
        catch { }
        await db.SaveChangesAsync(ct);
    // Invalidate cache for tenant via tag
    try { _versions?.Increment(Tenant()); } catch { }
        Response.Headers.ETag = currentEtag;
        return Ok(new CollectionDto(e.Id, e.Name, e.CreatedAt));
    } // End of Method Update

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "db.write")]
    public async Task<ActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var e = await db.Collections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();
        // If-Match precondition (when provided)
        var currentEtag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var ifm = Request.Headers.IfMatch;
        if (
            !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(ifm)
            && !AnyIfMatchMatches(ifm, currentEtag)
        )
        {
            Response.Headers.ETag = currentEtag;
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }
    db.Collections.Remove(e);
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new { tenant, collectionId = e.Id, op = "collection.deleted" };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "collection.deleted", payloadDoc);
        }
        catch { }
        await db.SaveChangesAsync(ct);
        try
        {
            _versions?.Increment(Tenant());
        }
        catch { }
        return NoContent();
    } // End of Method Delete
} // End of Class CollectionsController
