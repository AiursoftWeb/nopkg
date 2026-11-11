using Aiursoft.UiStack.Layout;

namespace Aiursoft.Nopkg.Models.PermissionsViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Permissions";
    }

    public required List<PermissionWithRoleCount> Permissions { get; init; }
}