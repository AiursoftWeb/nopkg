using Aiursoft.UiStack.Layout;
using Aiursoft.Nopkg.Registry;
namespace Aiursoft.Nopkg.Models.DashboardViewModels;

public record PackageVersion(string Version, DateTimeOffset? Published, IReadOnlyList<string> Tags, bool Deprecated);
public record PackageDependency(string Name, string Range, string Kind);

public class DetailsViewModel : UiStackLayoutViewModel
{
    public DetailsViewModel() => PageTitle = "Package details";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public int Page { get; init; } = 1;
    public int VersionsPage { get; init; } = 1;
    public int DependentsPage { get; init; } = 1;
    public bool IsLocallyPublished { get; init; }
    public string? After { get; init; }
    public string? Before { get; init; }
    public string Query { get; init; } = "";
    public string ActiveTab { get; init; } = "overview";
    public string Description { get; init; } = "";
    public string License { get; init; } = "";
    public string Author { get; init; } = "";
    public string Deprecated { get; init; } = "";
    public string ReadmeHtml { get; init; } = "";
    public string? Homepage { get; init; }
    public string? Repository { get; init; }
    public DateTimeOffset? Published { get; init; }
    public IReadOnlyList<PackageVersion> Versions { get; init; } = [];
    public IReadOnlyList<PackageDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<CachedPackage> Dependents { get; init; } = [];
    public int? ErrorStatus { get; init; }
}
