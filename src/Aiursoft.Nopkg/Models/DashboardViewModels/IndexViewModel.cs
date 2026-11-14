using Aiursoft.UiStack.Layout;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Models.DashboardViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IReadOnlyList<CachedPackage> Packages { get; init; } = [];
    public string Query { get; init; } = string.Empty;
    public int Page { get; init; } = 1;
    public int TotalHits { get; init; }
    public int TotalPages => (TotalHits + 19) / 20;
    public int TotalPackages { get; init; }
    public bool AdmissionEnabled { get; init; }
    public bool AdmissionUnavailable { get; init; }
    public bool CacheOnly { get; init; }
    public IndexViewModel() => PageTitle = "Packages";
}
