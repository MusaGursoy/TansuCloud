// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Identity.Infrastructure.External;

public sealed class ExternalProviderSetting
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string TenantId { get; set; } = default!;

    [Required]
    [MaxLength(64)]
    public string Provider { get; set; } = default!; // e.g., "oidc"

    [MaxLength(128)]
    public string? DisplayName { get; set; }

    [Required]
    [MaxLength(512)]
    public string Authority { get; set; } = default!;

    [Required]
    [MaxLength(128)]
    public string ClientId { get; set; } = default!;

    [MaxLength(128)]
    public string? ClientSecret { get; set; }

    [MaxLength(1024)]
    public string Scopes { get; set; } = "openid profile email"; // space-separated

    public bool Enabled { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
} // End of Class ExternalProviderSetting
