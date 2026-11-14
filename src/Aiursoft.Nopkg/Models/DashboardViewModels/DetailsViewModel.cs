using Aiursoft.UiStack.Layout;

namespace Aiursoft.Nopkg.Models.DashboardViewModels;

public class DetailsViewModel : UiStackLayoutViewModel
{
    public DetailsViewModel() => PageTitle = "Package details";
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int Page { get; init; } = 1;
    public bool IsLocallyPublished { get; init; }
    public string Query { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public IReadOnlyList<string> Versions { get; init; } = [];
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();
    public int? ErrorStatus { get; init; }
}
