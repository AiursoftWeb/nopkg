using Aiursoft.UiStack.Layout;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Models.DashboardViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IReadOnlyList<CachedPackage> Packages { get; init; } = [];
    public string Query { get; init; } = string.Empty;
    public int Page { get; init; } = 1;
    public string? After { get; init; }
    public string? Before { get; init; }
    public string? NextAfter => Packages.LastOrDefault()?.Name ?? Before;
    public string? PreviousBefore => Packages.FirstOrDefault()?.Name ?? After;
    public bool HasPrevious { get; init; }
    public bool HasNext { get; init; }
    public int TotalPackages { get; init; }
    public bool AdmissionEnabled { get; init; }
    public bool AdmissionUnavailable { get; init; }
    public bool CacheOnly { get; init; }
    public IndexViewModel() => PageTitle = "Packages";
}
