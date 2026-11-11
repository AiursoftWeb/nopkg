using System.ComponentModel.DataAnnotations;

namespace Aiursoft.Nopkg.Models.UsersViewModels;

public class UserRoleViewModel
{
    [Display(Name = "Role name")]
    public required string RoleName { get; set; }

    [Display(Name = "Is Selected")]
    public bool IsSelected { get; set; }
}