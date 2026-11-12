using System.ComponentModel.DataAnnotations;
using Aiursoft.Nopkg.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.Nopkg.Models.UserApiKeysViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public List<UserApiKey> Keys { get; set; } = [];
}

public class CreateViewModel : UiStackLayoutViewModel
{
    [Required, MaxLength(100)]
    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Range(0, 36500)]
    [Display(Name = "Expiration")]
    public int ExpirationDays { get; set; } = 365;
}

public class UsageViewModel : UiStackLayoutViewModel
{
    public required UserApiKey Key { get; set; }
    public string? RawKey { get; set; }
    public string RegistryUrl { get; set; } = string.Empty;
    public bool CanPublish { get; set; }
}

public class DeleteViewModel : UiStackLayoutViewModel
{
    public required UserApiKey Key { get; set; }
}
