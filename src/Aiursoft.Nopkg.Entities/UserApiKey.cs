using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.Nopkg.Entities;

[Index(nameof(KeyHash), IsUnique = true)]
public class UserApiKey
{
    [Key]
    public int Id { get; init; }

    // The column type follows the existing Identity user key.
    [Required] public required string UserId { get; init; }

    [JsonIgnore]
    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    [Required, MaxLength(100)] public required string Name { get; set; }
    [Required, MaxLength(64)] public required string KeyHash { get; init; }
    [Required, MaxLength(8)] public required string KeyPrefix { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Null means this key has not authenticated a request yet.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Null means this key does not expire.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Successful authentications recorded since usage tracking was introduced.</summary>
    public long TotalUses { get; set; }
    public long PublishedPackagesCount { get; set; }
    public DateTime? LastPublishedAt { get; set; }
    [MaxLength(214)] public string? LastPublishedPackage { get; set; }
    [MaxLength(128)] public string? LastPublishedVersion { get; set; }

    [NotMapped] public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
}
