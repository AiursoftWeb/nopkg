using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
namespace Aiursoft.Nopkg.Entities;
[Index(nameof(Name), IsUnique = true)]
public class PackageFamily
{
    [Key] public int Id { get; init; }
    [Required, MaxLength(214)] public required string Name { get; init; }
    public string? OwnerUserId { get; set; }
    [JsonIgnore, ForeignKey(nameof(OwnerUserId))] public User? OwnerUser { get; set; }
    public bool IsLocallyPublished { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    [ConcurrencyCheck, MaxLength(36)] public string Revision { get; set; } = Guid.NewGuid().ToString();
    [Required] public string TagsJson { get; set; } = "{}";
    [JsonIgnore, InverseProperty(nameof(PublishedPackage.Family))]
    public IReadOnlyCollection<PublishedPackage> Versions { get; private set; } = new List<PublishedPackage>();
}
