// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Primitives;
using TansuCloud.Database.Caching;
using TansuCloud.Database.EF;
using TansuCloud.Database.Services;
using TansuCloud.Observability.Auditing;
using TansuCloud.Observability.Caching;

namespace TansuCloud.Database.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    ITenantDbContextFactory factory,
    ILogger<DocumentsController> logger,
    TansuCloud.Database.Outbox.IOutboxProducer outbox,
    Microsoft.Extensions.Caching.Hybrid.HybridCache? cache = null,
    ITenantCacheVersion? versions = null,
    IAuditLogger? audit = null
) : ControllerBase
{
    private readonly ITenantDbContextFactory _factory = factory;
    private readonly ILogger<DocumentsController> _logger = logger;
    private readonly TansuCloud.Database.Outbox.IOutboxProducer _outbox = outbox;
    private readonly Microsoft.Extensions.Caching.Hybrid.HybridCache? _cache = cache;
    private readonly ITenantCacheVersion? _versions = versions;
    private readonly IAuditLogger? _audit = audit;

    private string Tenant() => Request.Headers["X-Tansu-Tenant"].ToString();

    private string Key(params string[] parts)
    {
        var tenant = Tenant();
        var ver = _versions?.Get(tenant) ?? 0;
        return $"t:{tenant}:v{ver}:db:documents:{string.Join(':', parts)}";
    }

    // Small helpers for conditional requests
    private static bool WeakETagEquals(string? a, string? b)
    {
        static string Norm(string? s)
        {
            s = (s ?? string.Empty).Trim();
            // Remove optional weakness and surrounding quotes
            if (s.StartsWith("W/", StringComparison.Ordinal))
                s = s.Substring(2).Trim();
            if (s.Length > 1 && s[0] == '"' && s[^1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s;
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
    } // End of Method WeakETagEquals

    private static bool AnyIfNoneMatchMatches(StringValues values, string current)
    {
        foreach (var raw in values)
        {
            if (raw is null)
                continue;
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim();
                if (t == "*")
                    return true; // any current representation matches
                if (WeakETagEquals(t, current))
                    return true;
            }
        }
        return false;
    } // End of Method AnyIfNoneMatchMatches

    private static bool AnyIfMatchMatches(StringValues values, string current)
    {
        foreach (var raw in values)
        {
            if (raw is null)
                continue;
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim();
                if (t == "*")
                    return true; // resource exists, so wildcard matches
                if (WeakETagEquals(t, current))
                    return true;
            }
        }
        return false;
    } // End of Method AnyIfMatchMatches

    public sealed record DocumentDto(
        Guid id,
        Guid collectionId,
        JsonElement? content,
        DateTimeOffset createdAt
    );

    // Accept arbitrary JSON for content; we'll persist the raw JSON text into a jsonb column.
    public sealed record CreateDocumentDto(
        Guid collectionId,
        System.Text.Json.JsonElement? content,
        float[]? embedding
    );

    public sealed record UpdateDocumentDto(
        System.Text.Json.JsonElement? content,
        float[]? embedding
    );

    private static JsonElement? ToElement(JsonDocument? doc)
    {
        if (doc is null)
            return null;
        return doc.RootElement.Clone();
    }

    [HttpGet]
    [Authorize(Policy = "db.read")]
    public async Task<ActionResult<object>> List(
        [FromQuery] Guid? collectionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sortBy = "createdAt",
        [FromQuery] string? sortDir = "desc",
        [FromQuery] DateTimeOffset? createdAfter = null,
        [FromQuery] DateTimeOffset? createdBefore = null,
        CancellationToken ct = default
    )
    {
        if (page <= 0 || pageSize <= 0 || pageSize > 500)
            return Problem(
                title: "Invalid pagination",
                statusCode: StatusCodes.Status400BadRequest
            );
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var q = db.Documents.AsNoTracking().AsQueryable();
        if (collectionId is Guid cid && cid != Guid.Empty)
            q = q.Where(d => d.CollectionId == cid);
        if (createdAfter.HasValue)
            q = q.Where(d => d.CreatedAt >= createdAfter.Value);
        if (createdBefore.HasValue)
            q = q.Where(d => d.CreatedAt <= createdBefore.Value);

        // Sorting
        var sort = (sortBy ?? "createdAt").Trim().ToLowerInvariant();
        var dir = (sortDir ?? "desc").Trim().ToLowerInvariant();
        var desc = dir is "desc" or "descending";
        q = (sort) switch
        {
            "id" => desc ? q.OrderByDescending(d => d.Id) : q.OrderBy(d => d.Id),
            "collectionid"
                => desc ? q.OrderByDescending(d => d.CollectionId) : q.OrderBy(d => d.CollectionId),
            _ => desc ? q.OrderByDescending(d => d.CreatedAt) : q.OrderBy(d => d.CreatedAt)
        };
        var total = await q.CountAsync(ct);
        var etag = ETagHelper.ComputeWeakETag(
            total.ToString(),
            page.ToString(),
            pageSize.ToString(),
            collectionId?.ToString() ?? "",
            sort,
            dir,
            createdAfter?.ToUnixTimeMilliseconds().ToString() ?? "",
            createdBefore?.ToUnixTimeMilliseconds().ToString() ?? ""
        );
        // If-None-Match: short-circuit with 304 when ETag matches
        var inm = Request.Headers.IfNoneMatch;
        if (!StringValues.IsNullOrEmpty(inm) && AnyIfNoneMatchMatches(inm, etag))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Try HybridCache for list page
        var cacheKey = Key(
            "list",
            collectionId?.ToString() ?? string.Empty,
            page.ToString(),
            pageSize.ToString(),
            sort,
            dir,
            createdAfter?.ToUnixTimeMilliseconds().ToString() ?? string.Empty,
            createdBefore?.ToUnixTimeMilliseconds().ToString() ?? string.Empty,
            etag
        );
        if (_cache is not null)
        {
            var cached = await _cache.GetOrCreateWithMetricsAsync(
                cacheKey,
                async token =>
                {
                    var list = await q.Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(d => new
                        {
                            d.Id,
                            d.CollectionId,
                            d.Content,
                            d.CreatedAt
                        })
                        .ToListAsync(token);
                    var dto = list.Select(x => new DocumentDto(
                            x.Id,
                            x.CollectionId,
                            ToElement(x.Content),
                            x.CreatedAt
                        ))
                        .ToList();
                    return new
                        {
                            total,
                            page,
                            pageSize,
                            items = dto
                        } as object;
                },
                service: "database",
                operation: "documents.list",
                cancellationToken: ct
            );
            Response.Headers.ETag = etag;
            return Ok(cached);
        }

        var items = await q.Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.CollectionId,
                d.Content,
                d.CreatedAt
            })
            .ToListAsync(ct);
        var itemsDto = items
            .Select(x => new DocumentDto(x.Id, x.CollectionId, ToElement(x.Content), x.CreatedAt))
            .ToList();
        Response.Headers.ETag = etag;
        return Ok(
            new
            {
                total,
                page,
                pageSize,
                items = itemsDto
            }
        );
    } // End of Method List

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "db.read")]
    public async Task<ActionResult<DocumentDto>> Get([FromRoute] Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var cacheKeyItem = Key("item", id.ToString());
        if (_cache is not null)
        {
            var cached = await _cache.GetOrCreateWithMetricsAsync(
                cacheKeyItem,
                async token =>
                {
                    var e0 = await db.Documents
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == id, token);
                    return e0 is null
                        ? null
                        : new DocumentDto(
                            e0.Id,
                            e0.CollectionId,
                            ToElement(e0.Content),
                            e0.CreatedAt
                        );
                },
                service: "database",
                operation: "documents.get",
                cancellationToken: ct
            );
            if (cached is null)
                return NotFound();
            var etag0 = ETagHelper.ComputeWeakETag(
                cached.id.ToString(),
                cached.createdAt.ToUnixTimeMilliseconds().ToString()
            );
            var inm0 = Request.Headers.IfNoneMatch;
            if (!StringValues.IsNullOrEmpty(inm0) && AnyIfNoneMatchMatches(inm0, etag0))
            {
                Response.Headers.ETag = etag0;
                return StatusCode(StatusCodes.Status304NotModified);
            }
            Response.Headers.ETag = etag0;
            return Ok(cached);
        }

        var e = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();
        var etag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var inm = Request.Headers.IfNoneMatch;
        if (!StringValues.IsNullOrEmpty(inm) && AnyIfNoneMatchMatches(inm, etag))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }
        Response.Headers.ETag = etag;
        return Ok(new DocumentDto(e.Id, e.CollectionId, ToElement(e.Content), e.CreatedAt));
    } // End of Method Get

    [HttpPost]
    [Authorize(Policy = "db.write")]
    public async Task<ActionResult<DocumentDto>> Create(
        [FromBody] CreateDocumentDto input,
        CancellationToken ct
    )
    {
        // Idempotency: if key is present and a prior event exists, return the original result
        string? idem = Request.Headers["Idempotency-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(idem))
        {
            await using var dbCheck = await _factory.CreateAsync(HttpContext, ct);
            var prior = await dbCheck
                .OutboxEvents.AsNoTracking()
                .Where(e => e.IdempotencyKey == idem && e.Type == "document.created")
                .OrderByDescending(e => e.OccurredAt)
                .FirstOrDefaultAsync(ct);
            if (prior?.Payload is JsonDocument pdoc)
            {
                try
                {
                    var root = pdoc.RootElement;
                    if (
                        root.TryGetProperty("documentId", out var idProp)
                        && Guid.TryParse(idProp.GetString(), out var priorDocId)
                    )
                    {
                        await using var dbLookup = await _factory.CreateAsync(HttpContext, ct);
                        var existing = await dbLookup
                            .Documents.AsNoTracking()
                            .FirstOrDefaultAsync(d => d.Id == priorDocId, ct);
                        if (existing is not null)
                        {
                            var dtoExisting = new DocumentDto(
                                existing.Id,
                                existing.CollectionId,
                                ToElement(existing.Content),
                                existing.CreatedAt
                            );
                            Response.Headers.ETag = ETagHelper.ComputeWeakETag(
                                existing.Id.ToString(),
                                existing.CreatedAt.ToUnixTimeMilliseconds().ToString()
                            );
                            return CreatedAtAction(
                                nameof(Get),
                                new { id = existing.Id },
                                dtoExisting
                            );
                        }
                    }
                }
                catch { }
            }
        }
        if (input.collectionId == Guid.Empty)
        {
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "DocumentCreate",
                    Outcome = "Failure",
                    ReasonCode = "ValidationError",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { input.collectionId },
                new[] { nameof(input.collectionId) }
            );
            return Problem(
                title: "collectionId is required",
                statusCode: StatusCodes.Status400BadRequest
            );
        }
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        // Ensure collection exists
        var exists = await db
            .Collections.AsNoTracking()
            .AnyAsync(c => c.Id == input.collectionId, ct);
        if (!exists)
        {
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "DocumentCreate",
                    Outcome = "Failure",
                    ReasonCode = "CollectionNotFound",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { input.collectionId },
                new[] { nameof(input.collectionId) }
            );
            return Problem(
                title: "collection not found",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var entity = new Document
        {
            Id = Guid.NewGuid(),
            CollectionId = input.collectionId,
            // Store structured JSON
            Content = input.content.HasValue
                ? JsonDocument.Parse(input.content.Value.GetRawText())
                : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Documents.Add(entity);
        // outbox enqueue (transactional); include idempotency key if present
        idem = Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new
            {
                tenant,
                collectionId = entity.CollectionId,
                documentId = entity.Id,
                op = "document.created"
            };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "document.created", payloadDoc, idem);
        }
        catch { }
        await db.SaveChangesAsync(ct);
        try
        {
            _versions?.Increment(Tenant());
            HybridCacheMetrics.RecordEviction("database", "documents.create", "tenant_version_increment");
        }
        catch { }
        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "DocumentCreate",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { entity.Id, entity.CollectionId },
            new[] { nameof(entity.Id), nameof(entity.CollectionId) }
        );

        // Optional: upsert vector embedding if provided and column exists
        if (input.embedding is { Length: > 0 })
        {
            try
            {
                // Only attempt upsert if dimension matches our schema (1536). Otherwise, skip gracefully.
                if (input.embedding.Length == 1536)
                {
                    // Build vector literal like "[0.1,0.2,...]" and cast to vector(1536) to avoid driver type mapping issues
                    var vec =
                        "["
                        + string.Join(
                            ',',
                            input.embedding.Select(f =>
                                f.ToString("G9", CultureInfo.InvariantCulture)
                            )
                        )
                        + "]";
                    var sql =
                        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='documents' AND column_name='embedding') THEN UPDATE documents SET embedding = CAST($1 AS vector(1536)) WHERE id = $2; END IF; END$$;";
                    await db.Database.ExecuteSqlRawAsync(sql, new object[] { vec, entity.Id }, ct);
                }
                else
                {
                    _logger.LogInformation(
                        "Skipping embedding upsert due to dimension mismatch: provided={Provided}, expected=1536",
                        input.embedding.Length
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector upsert failed (embedding column may be missing)");
            }
        }

        var dto = new DocumentDto(
            entity.Id,
            entity.CollectionId,
            ToElement(entity.Content),
            entity.CreatedAt
        );
        Response.Headers.ETag = ETagHelper.ComputeWeakETag(
            entity.Id.ToString(),
            entity.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, dto);
    } // End of Method Create

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "db.write")]
    public async Task<ActionResult<DocumentDto>> Update(
        [FromRoute] Guid id,
        [FromBody] UpdateDocumentDto input,
        CancellationToken ct
    )
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var e = await db.Documents.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();
        // If-Match precondition (when provided)
        var currentEtag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var ifm = Request.Headers.IfMatch;
        if (!StringValues.IsNullOrEmpty(ifm) && !AnyIfMatchMatches(ifm, currentEtag))
        {
            Response.Headers.ETag = currentEtag;
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "DocumentUpdate",
                    Outcome = "Failure",
                    ReasonCode = "PreconditionFailed",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { id },
                new[] { nameof(id) }
            );
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        if (input.content.HasValue)
        {
            e.Content = JsonDocument.Parse(input.content.Value.GetRawText());
        }
        // outbox enqueue
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new
            {
                tenant,
                collectionId = e.CollectionId,
                documentId = e.Id,
                op = "document.updated"
            };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "document.updated", payloadDoc);
        }
        catch { }
        await db.SaveChangesAsync(ct);
        try
        {
            _versions?.Increment(Tenant());
            HybridCacheMetrics.RecordEviction("database", "documents.update", "tenant_version_increment");
        }
        catch { }
        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "DocumentUpdate",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { e.Id, e.CollectionId },
            new[] { nameof(e.Id), nameof(e.CollectionId) }
        );
        if (input.embedding is { Length: > 0 })
        {
            try
            {
                if (input.embedding.Length == 1536)
                {
                    var vec =
                        "["
                        + string.Join(
                            ',',
                            input.embedding.Select(f =>
                                f.ToString("G9", CultureInfo.InvariantCulture)
                            )
                        )
                        + "]";
                    var sql =
                        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='documents' AND column_name='embedding') THEN UPDATE documents SET embedding = CAST($1 AS vector(1536)) WHERE id = $2; END IF; END$$;";
                    await db.Database.ExecuteSqlRawAsync(sql, new object[] { vec, e.Id }, ct);
                }
                else
                {
                    _logger.LogInformation(
                        "Skipping embedding upsert due to dimension mismatch: provided={Provided}, expected=1536",
                        input.embedding.Length
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector upsert failed (embedding column may be missing)");
            }
        }
        Response.Headers.ETag = currentEtag;
        return Ok(new DocumentDto(e.Id, e.CollectionId, ToElement(e.Content), e.CreatedAt));
    } // End of Method Update

    /// <summary>
    /// Partially updates a document using JSON Patch (RFC 6902) operations.
    /// </summary>
    /// <param name="id">Document ID.</param>
    /// <param name="patchDoc">JSON Patch document with operations (add, remove, replace, move, copy, test).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated document.</returns>
    /// <response code="200">Document successfully patched.</response>
    /// <response code="400">Invalid JSON Patch document.</response>
    /// <response code="404">Document not found.</response>
    /// <response code="412">Precondition failed (If-Match mismatch).</response>
    /// <remarks>
    /// JSON Patch allows efficient partial updates. Example:
    /// [
    ///   { "op": "replace", "path": "/content/title", "value": "New Title" },
    ///   { "op": "add", "path": "/content/tags/-", "value": "new-tag" }
    /// ]
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "db.write")]
    [Consumes("application/json-patch+json")]
    [ProducesResponseType(typeof(DocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<DocumentDto>> Patch(
        [FromRoute] Guid id,
        [FromBody] Microsoft.AspNetCore.JsonPatch.JsonPatchDocument<UpdateDocumentDto> patchDoc,
        CancellationToken ct
    )
    {
        if (patchDoc == null)
            return Problem(title: "JSON Patch document is required", statusCode: StatusCodes.Status400BadRequest);

        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var e = await db.Documents.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();

        // If-Match precondition
        var currentEtag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var ifm = Request.Headers.IfMatch;
        if (!StringValues.IsNullOrEmpty(ifm) && !AnyIfMatchMatches(ifm, currentEtag))
        {
            Response.Headers.ETag = currentEtag;
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "DocumentPatch",
                    Outcome = "Failure",
                    ReasonCode = "PreconditionFailed",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { id },
                new[] { nameof(id) }
            );
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        // Create a DTO from current state
        var dto = new UpdateDocumentDto(
            ToElement(e.Content),
            null // Don't expose embedding in patch operations for simplicity
        );

        // Apply patch operations
        patchDoc.ApplyTo(dto);
        
        // Validate the model after patching
        if (!TryValidateModel(dto))
        {
            return ValidationProblem(ModelState);
        }

        // Update entity with patched values
        if (dto.content.HasValue)
        {
            e.Content = JsonDocument.Parse(dto.content.Value.GetRawText());
        }

        // Outbox event
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new
            {
                tenant,
                collectionId = e.CollectionId,
                documentId = e.Id,
                op = "document.patched"
            };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "document.patched", payloadDoc);
        }
        catch { }

        await db.SaveChangesAsync(ct);

        // Cache invalidation
        try
        {
            _versions?.Increment(Tenant());
            HybridCacheMetrics.RecordEviction("database", "documents.patch", "tenant_version_increment");
        }
        catch { }

        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "DocumentPatch",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { e.Id, e.CollectionId },
            new[] { nameof(e.Id), nameof(e.CollectionId) }
        );

        Response.Headers.ETag = currentEtag;
        return Ok(new DocumentDto(e.Id, e.CollectionId, ToElement(e.Content), e.CreatedAt));
    } // End of Method Patch

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "db.write")]
    public async Task<ActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateAsync(HttpContext, ct);
        var e = await db.Documents.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
            return NotFound();
        // If-Match precondition (when provided)
        var currentEtag = ETagHelper.ComputeWeakETag(
            e.Id.ToString(),
            e.CreatedAt.ToUnixTimeMilliseconds().ToString()
        );
        var ifm = Request.Headers.IfMatch;
        if (!StringValues.IsNullOrEmpty(ifm) && !AnyIfMatchMatches(ifm, currentEtag))
        {
            Response.Headers.ETag = currentEtag;
            _audit?.TryEnqueueRedacted(
                new AuditEvent
                {
                    Category = "Database",
                    Action = "DocumentDelete",
                    Outcome = "Failure",
                    ReasonCode = "PreconditionFailed",
                    Subject = HttpContext.User?.Identity?.Name ?? "system"
                },
                new { id },
                new[] { nameof(id) }
            );
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        db.Documents.Remove(e);
        try
        {
            var tenant = Request.Headers["X-Tansu-Tenant"].ToString();
            var payloadObj = new
            {
                tenant,
                collectionId = e.CollectionId,
                documentId = e.Id,
                op = "document.deleted"
            };
            var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payloadObj));
            _outbox.Enqueue(db, "document.deleted", payloadDoc);
        }
        catch { }
        await db.SaveChangesAsync(ct);
        try
        {
            _versions?.Increment(Tenant());
            HybridCacheMetrics.RecordEviction("database", "documents.delete", "tenant_version_increment");
        }
        catch { }
        // Audit success
        _audit?.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Database",
                Action = "DocumentDelete",
                Outcome = "Success",
                Subject = HttpContext.User?.Identity?.Name ?? "system"
            },
            new { e.Id, e.CollectionId },
            new[] { nameof(e.Id), nameof(e.CollectionId) }
        );
        return NoContent();
    } // End of Method Delete

    public sealed record VectorSearchDto(Guid collectionId, float[] query, int k = 10);

    [HttpPost("search/vector")]
    [Authorize(Policy = "db.read")]
    public async Task<ActionResult<object>> VectorSearch(
        [FromBody] VectorSearchDto input,
        CancellationToken ct
    )
    {
        if (input.collectionId == Guid.Empty || input.query is not { Length: > 0 })
            return Problem(
                title: "collectionId and query are required",
                statusCode: StatusCodes.Status400BadRequest
            );
        await using var db = await _factory.CreateAsync(HttpContext, ct);

        // Guard if embedding column is missing
        var hasEmbedding =
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM information_schema.columns WHERE table_name='documents' AND column_name='embedding'"
            ) >= 0; // raw check in query below

        try
        {
            // Use raw SQL to leverage pgvector ANN with cosine distance; limit by k
            var sql =
                @"SELECT id, collection_id, content, created_at FROM documents 
                        WHERE collection_id = $1 AND embedding IS NOT NULL 
                        ORDER BY embedding <-> $2 LIMIT $3";
            var rowsRaw = await db.Set<Document>()
                .FromSqlRaw(sql, input.collectionId, input.query, Math.Max(1, input.k))
                .AsNoTracking()
                .Select(d => new
                {
                    d.Id,
                    d.CollectionId,
                    d.Content,
                    d.CreatedAt
                })
                .ToListAsync(ct);
            var rows = rowsRaw
                .Select(d => new DocumentDto(
                    d.Id,
                    d.CollectionId,
                    ToElement(d.Content),
                    d.CreatedAt
                ))
                .ToList();
            return Ok(new { total = rows.Count, items = rows });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector search failed; is pgvector enabled and column present?");
            return Problem(
                title: "Vector search not available",
                statusCode: StatusCodes.Status501NotImplemented
            );
        }
    } // End of Method VectorSearch

    public sealed record GlobalVectorSearchDto(float[] query, int k = 10, int perCollection = 0);

    [HttpPost("search/vector-global")]
    [Authorize(Policy = "db.read")]
    public async Task<ActionResult<object>> VectorSearchGlobal(
        [FromBody] GlobalVectorSearchDto input,
        CancellationToken ct
    )
    {
        if (input.query is not { Length: > 0 })
            return Problem(title: "query is required", statusCode: StatusCodes.Status400BadRequest);

        await using var db = await _factory.CreateAsync(HttpContext, ct);
        try
        {
            // Two-step per-collection cap using window function, then global top-K by distance
            var sql =
                @"SELECT id, collection_id, content, created_at
FROM (
    SELECT id, collection_id, content, created_at,
           embedding <-> $1 AS dist,
           ROW_NUMBER() OVER (PARTITION BY collection_id ORDER BY embedding <-> $1) AS rn
    FROM documents
    WHERE embedding IS NOT NULL
) q
WHERE CASE WHEN $2 <= 0 THEN TRUE ELSE rn <= $2 END
ORDER BY dist ASC
LIMIT $3";

            var itemsRaw = await db.Set<Document>()
                .FromSqlRaw(
                    sql,
                    input.query,
                    Math.Max(0, input.perCollection),
                    Math.Max(1, input.k)
                )
                .AsNoTracking()
                .Select(d => new
                {
                    d.Id,
                    d.CollectionId,
                    d.Content,
                    d.CreatedAt
                })
                .ToListAsync(ct);
            var items = itemsRaw
                .Select(d => new DocumentDto(
                    d.Id,
                    d.CollectionId,
                    ToElement(d.Content),
                    d.CreatedAt
                ))
                .ToList();
            return Ok(new { total = items.Count, items });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Global vector search failed; is pgvector enabled and column present?"
            );
            return Problem(
                title: "Vector search not available",
                statusCode: StatusCodes.Status501NotImplemented
            );
        }
    } // End of Method VectorSearchGlobal
} // End of Class DocumentsController
