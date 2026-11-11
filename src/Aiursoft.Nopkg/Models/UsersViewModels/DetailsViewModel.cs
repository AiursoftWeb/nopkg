using System.ComponentModel.DataAnnotations;
using Aiursoft.Nopkg.Authorization;
using Aiursoft.Nopkg.Entities;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.Nopkg.Models.UsersViewModels;

public class DetailsViewModel : UiStackLayoutViewModel
{
    public DetailsViewModel()
    {
        PageTitle = "User Details";
    }

    [Display(Name = "User")]
    public required User User { get; set; }

    [Display(Name = "Roles")]
    public required IList<IdentityRole> Roles { get; set; }

    [Display(Name = "Permissions")]
    public required List<PermissionDescriptor> Permissions { get; set; }
}
