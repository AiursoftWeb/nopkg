using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
namespace Aiursoft.Nopkg.Entities;
[Index(nameof(FamilyId), nameof(Version), IsUnique = true)]
public class PublishedPackage
{
    [Key] public int Id { get; init; }
    public int FamilyId { get; init; }
    [JsonIgnore, ForeignKey(nameof(FamilyId))] public PackageFamily? Family { get; set; }
    [Required, MaxLength(128)] public required string Version { get; init; }
    [Required, MaxLength(64)] public required string Sha256 { get; init; }
    [Required] public required string ManifestJson { get; init; }
    public string? UploadedByUserId { get; init; }
    [JsonIgnore, ForeignKey(nameof(UploadedByUserId))] public User? UploadedByUser { get; set; }
    public DateTime PublishedAt { get; init; } = DateTime.UtcNow;
}
