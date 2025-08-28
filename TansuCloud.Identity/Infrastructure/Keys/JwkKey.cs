// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Identity.Infrastructure.Keys;

public sealed class JwkKey
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Kid { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    [MaxLength(8)]
    public string Use { get; set; } = "sig"; // signing only

    [Required]
    [MaxLength(16)]
    public string Alg { get; set; } = "RS256";

    [Required]
    public string Json { get; set; } = default!; // full JWK JSON

    public bool IsCurrent { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetireAfter { get; set; } // grace until this time
    public DateTimeOffset? RetiredAt { get; set; }
} // End of Class JwkKey
