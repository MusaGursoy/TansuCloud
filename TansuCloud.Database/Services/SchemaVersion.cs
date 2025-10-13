// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Database.Services;

/// <summary>
/// Represents a schema version record in the __SchemaVersion table.
/// Tracks the current schema version and migration history for a database.
/// </summary>
public sealed class SchemaVersion
{
    /// <summary>
    /// Unique identifier for this version record.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Database name (e.g., tansu_identity, tansu_audit, tansu_tenant_acme).
    /// </summary>
    public required string DatabaseName { get; init; }

    /// <summary>
    /// Current schema version (e.g., 1.0.0).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// When this version was applied (UTC).
    /// </summary>
    public required DateTimeOffset AppliedAt { get; init; }

    /// <summary>
    /// Optional description of changes in this version.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional metadata about the migration (JSON).
    /// </summary>
    public string? Metadata { get; init; }
} // End of Class SchemaVersion
