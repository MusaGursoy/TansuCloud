// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace TansuCloud.Gateway.Data;

/// <summary>
/// EF Core entity for Gateway policies stored in PostgreSQL.
/// Maps to gateway_policies table in Identity database.
/// </summary>
[Table("gateway_policies")]
public class PolicyEntity
{
    /// <summary>Unique policy identifier (primary key).</summary>
    [Key]
    [MaxLength(128)]
    [Column("id")]
    public required string Id { get; set; }
    
    /// <summary>Policy type: 0=CORS, 1=IpAllow, 2=IpDeny.</summary>
    [Column("type")]
    public required int Type { get; set; }
    
    /// <summary>Enforcement mode: 0=Shadow, 1=AuditOnly, 2=Enforce.</summary>
    [Column("mode")]
    public required int Mode { get; set; }
    
    /// <summary>Human-readable description.</summary>
    [MaxLength(500)]
    [Column("description")]
    public string Description { get; set; } = string.Empty;
    
    /// <summary>Policy-specific configuration as JSONB (CorsConfig or IpConfig).</summary>
    [Column("config", TypeName = "jsonb")]
    public required string ConfigJson { get; set; }
    
    /// <summary>Policy enabled flag (for soft delete).</summary>
    [Column("enabled")]
    public bool Enabled { get; set; } = true;
    
    /// <summary>Creation timestamp (UTC).</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Last update timestamp (UTC).</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
} // End of Class PolicyEntity
