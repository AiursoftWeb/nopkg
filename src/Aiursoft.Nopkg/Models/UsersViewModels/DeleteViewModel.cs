using System.ComponentModel.DataAnnotations;
using Aiursoft.Nopkg.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.Nopkg.Models.UsersViewModels;

public class DeleteViewModel : UiStackLayoutViewModel
{
    public DeleteViewModel()
    {
        PageTitle = "Delete User";
    }

    [Display(Name = "User")]
    public required User User { get; set; }
}
