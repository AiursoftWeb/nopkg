namespace Aiursoft.Nopkg.Registry;

public record CachedPackage(string Name, bool IsLocallyPublished = false)
{
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset? Published { get; init; }
}
