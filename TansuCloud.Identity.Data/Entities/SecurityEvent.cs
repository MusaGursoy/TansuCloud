// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Identity.Data.Entities;

public sealed class SecurityEvent
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Type { get; set; } = default!; // e.g., TokenIssued, LoginFailed, ImpersonationStarted

    [MaxLength(64)]
    public string? UserId { get; set; }

    [MaxLength(256)]
    public string? TenantId { get; set; }

    [MaxLength(256)]
    public string? ActorId { get; set; } // e.g., impersonator

    [MaxLength(2048)]
    public string? Details { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
} // End of Class SecurityEvent
