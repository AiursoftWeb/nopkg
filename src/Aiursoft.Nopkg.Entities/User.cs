using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.Nopkg.Entities;

[ExcludeFromCodeCoverage]
public class User : IdentityUser
{
    [JsonIgnore, InverseProperty(nameof(UserApiKey.User))]
    public IReadOnlyCollection<UserApiKey> ApiKeys { get; private set; } = new List<UserApiKey>();
    [JsonIgnore, InverseProperty(nameof(PackageFamily.OwnerUser))]
    public IReadOnlyCollection<PackageFamily> OwnedPackages { get; private set; } = new List<PackageFamily>();
    [JsonIgnore, InverseProperty(nameof(PublishedPackage.UploadedByUser))]
    public IReadOnlyCollection<PublishedPackage> PublishedPackages { get; private set; } = new List<PublishedPackage>();

    public const string DefaultAvatarPath = "avatar/default-avatar.jpg";

    [MaxLength(30)]
    [MinLength(2)]
    public required string DisplayName { get; set; }

    [MaxLength(150)] [MinLength(2)] public string AvatarRelativePath { get; set; } = DefaultAvatarPath;

    public DateTime CreationTime { get; init; } = DateTime.UtcNow;
}
