// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Database.EF;
using TansuCloud.Database.Services;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using TansuCloud.Database.Caching;
using TansuCloud.Observability.Auditing;
using TansuCloud.Observability.Caching;

namespace TansuCloud.Database.Controllers;

/// <summary>
/// Manages collections of documents with CRUD operations, caching, and conditional requests.
/// </summary>
[ApiController]
[Route("api/collections")]
[Produces("application/json")]
public sealed class CollectionsController(
    ITenantDbContextFactory factory,
    ILogger<CollectionsController> logger,
    TansuCloud.Database.Outbox.IOutboxProducer outbox,
    Microsoft.Extensions.Caching.Hybrid.HybridCache? cache = null,
    ITenantCacheVersion? versions = null,
    IAuditLogger? audit = null
) : ControllerBase
{
    private readonly ITenantDbContextFactory _factory = factory;
    private readonly ILogger<CollectionsController> _logger = logger;
    private readonly TansuCloud.Database.Outbox.IOutboxProducer _outbox = outbox;
    private readonly Microsoft.Extensions.Caching.Hybrid.HybridCache? _cache = cache;
    private readonly ITenantCacheVersion? _versions = versions;
    private readonly IAuditLogger? _audit = audit;

    private string Tenant() => Request.Headers["X-Tansu-Tenant"].ToString();
    private string Key(params string[] parts)
    {
        var tenant = Tenant();
        var ver = _versions?.Get(tenant) ?? 0;
        return $"t:{tenant}:v{ver}:db:collections:{string.Join(':', parts)}";
    }

    /// <summary>
    /// Request DTO for creating a new collection.
    /// </summary>
    /// <param name="name">The name of the collection (required).</param>
    public sealed record CreateCollectionDto(string name);

    /// <summary>
    /// Response DTO representing a collection.
    /// </summary>
    /// <param name="id">Unique identifier of the collection.</param>
    /// <param name="name">Name of the collection.</param>
    /// <param name="createdAt">Timestamp when the collection was created.</param>
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

    /// <summary>
    /// Lists all collections with pagination support.
    /// </summary>
    /// <param name="page">Page number (default: 1).</param>
    /// <param name="pageSize">Number of items per page (default: 50, max: 500).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated list of collections with weak ETag support.</returns>
    /// <response code="200">Returns the list of collections.</response>
    /// <response code="304">Not Modified - ETag matches (If-None-Match).</response>
    /// <response code="400">Invalid pagination parameters.</response>
    /// <response code="401">Unauthorized - missing or invalid JWT token.</response>
    [HttpGet]
    [Authorize(Policy = "db.read")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
            var cached = await _cache.GetOrCreateWithMetricsAsync(
                cacheKey,
                async token =>
                {
                    var items = await q.Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(c => new CollectionDto(c.Id, c.Name, c.CreatedAt))
                        .ToListAsync(token);
                    return new { total, page, pageSize, items } as object;
                },
                service: "database",
                operation: "collections.list",
                cancellationToken: ct
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

    /// <summary>
    /// Streams all collections using IAsyncEnumerable for improved memory efficiency.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream of collections as newline-delimited JSON.</returns>
    /// <response code="200">Returns the stream of collections.</response>
    /// <response code="401">Unauthorized - missing or invalid JWT token.</response>
    /// <remarks>
    /// This endpoint streams collections one-by-one as newline-delimited JSON (NDJSON).
    /// Use this for large result sets to reduce memory usage on the server.
    /// Response format: each line is a separate JSON object.
    /// </remarks>
    [HttpGet("stream")]
    [Authorize(Policy = "db.read")]
    [ProducesResponseType(typeof(IAsyncEnumerable<CollectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [Produces("application/x-ndjson")]
    public async IAsyncEnumerable<CollectionDto> ListStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var q = db.Collections.AsNoTracking().OrderByDescending(c => c.CreatedAt);

        await foreach (var e in q.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return new CollectionDto(e.Id, e.Name, e.CreatedAt);
        }
    } // End of Method ListStream

    /// <summary>
    /// Creates a new collection.
    /// </summary>
    /// <param name="input">Collection data including name (required).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created collection with its generated ID.</returns>
    /// <response code="201">Collection created successfully.</response>
    /// <response code="400">Invalid input (e.g., missing or empty name).</response>
    /// <response code="401">Unauthorized - missing or invalid JWT token.</response>
    /// <response code="403">Forbidden - insufficient permissions (requires db.write scope).</response>
    [HttpPost]
    [Authorize(Policy = "db.write")]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CollectionDto>> Create(
        [FromBody] CreateCollectionDto input,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(input.name))
        {
            // Audit validation failure (allowlist: name)
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "CollectionCreate",
                    Outcome = "Failure",
                    ReasonCode = "ValidationError",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { input.name },
                new[] { nameof(input.name) }
            );
            return Problem(title: "name is required", statusCode: StatusCodes.Status400BadRequest);
        }
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
        try
        {
            _versions?.Increment(Tenant());
            HybridCacheMetrics.RecordEviction("database", "collections.create", "tenant_version_increment");
        }
        catch { }
        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "CollectionCreate",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { entity.Id, entity.Name },
            new[] { nameof(entity.Id), nameof(entity.Name) }
        );
        var dto = new CollectionDto(entity.Id, entity.Name, entity.CreatedAt);
        Response.Headers.ETag = ETagHelper.ComputeWeakETag(
            entity.Id.ToString(),
            entity.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, dto);
    } // End of Method Create

    /// <summary>
    /// Retrieves a single collection by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested collection if found.</returns>
    /// <response code="200">Returns the collection.</response>
    /// <response code="304">Not Modified - ETag matches (If-None-Match).</response>
    /// <response code="404">Collection not found.</response>
    /// <response code="401">Unauthorized - missing or invalid JWT token.</response>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "db.read")]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CollectionDto>> Get([FromRoute] Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var cacheKey = Key("item", id.ToString());
        if (_cache is not null)
        {
            var cached = await _cache.GetOrCreateWithMetricsAsync(
                cacheKey,
                async token =>
                {
                    var e0 = await db.Collections
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == id, token);
                    return e0 is null ? null : new CollectionDto(e0.Id, e0.Name, e0.CreatedAt);
                },
                service: "database",
                operation: "collections.get",
                cancellationToken: ct
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

    /// <summary>
    /// Request DTO for updating a collection.
    /// </summary>
    /// <param name="name">New name for the collection (required).</param>
    public sealed record UpdateCollectionDto(string name);

    /// <summary>
    /// Updates an existing collection (full replacement).
    /// </summary>
    /// <param name="id">ID of the collection to update.</param>
    /// <param name="input">New collection data (name).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated collection.</returns>
    /// <response code="200">Collection updated successfully.</response>
    /// <response code="400">Invalid input (e.g., missing or empty name).</response>
    /// <response code="404">Collection not found.</response>
    /// <response code="412">Precondition Failed - If-Match header does not match current ETag.</response>
    /// <response code="401">Unauthorized - missing or invalid JWT token.</response>
    /// <response code="403">Forbidden - insufficient permissions (requires db.write scope).</response>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "db.write")]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CollectionDto>> Update(
        [FromRoute] Guid id,
        [FromBody] UpdateCollectionDto input,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(input.name))
        {
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "CollectionUpdate",
                    Outcome = "Failure",
                    ReasonCode = "ValidationError",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { id, input.name },
                new[] { nameof(id), nameof(input.name) }
            );
            return Problem(title: "name is required", statusCode: StatusCodes.Status400BadRequest);
        }
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
            // Audit precondition failure
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "CollectionUpdate",
                    Outcome = "Failure",
                    ReasonCode = "PreconditionFailed",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { id },
                new[] { nameof(id) }
            );
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
        try
        {
            _versions?.Increment(Tenant());
            HybridCacheMetrics.RecordEviction("database", "collections.update", "tenant_version_increment");
        }
        catch { }
        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "CollectionUpdate",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { e.Id, e.Name },
            new[] { nameof(e.Id), nameof(e.Name) }
        );
        Response.Headers.ETag = currentEtag;
        return Ok(new CollectionDto(e.Id, e.Name, e.CreatedAt));
    } // End of Method Update

    /// <summary>
    /// Deletes a collection by ID.
    /// </summary>
    /// <param name="id">ID of the collection to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>No content on successful deletion.</returns>
    /// <response code="204">Collection deleted successfully.</response>
    /// <response code="404">Collection not found.</response>
    /// <response code="412">Precondition Failed - If-Match header does not match current ETag.</response>
    /// <response code="401">Unauthorized - missing or invalid JWT token.</response>
    /// <response code="403">Forbidden - insufficient permissions (requires db.write scope).</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "db.write")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
            // Audit precondition failure
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "CollectionDelete",
                    Outcome = "Failure",
                    ReasonCode = "PreconditionFailed",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { id },
                new[] { nameof(id) }
            );
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
            HybridCacheMetrics.RecordEviction("database", "collections.delete", "tenant_version_increment");
        }
        catch { }
        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "CollectionDelete",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { e.Id },
            new[] { nameof(e.Id) }
        );
        return NoContent();
    } // End of Method Delete
} // End of Class CollectionsController
