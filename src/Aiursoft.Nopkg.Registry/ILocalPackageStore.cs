using System.Text.Json.Nodes;
namespace Aiursoft.Nopkg.Registry;

public sealed record LocalPackage(JsonObject Metadata, IReadOnlyDictionary<string, string> ArchiveHashes);

public interface ILocalPackageStore
{
    Task<LocalPackage?> FindAsync(string name, CancellationToken token);
    Task<bool> RecordMirrorAsync(string name, CancellationToken token);
    Task<IReadOnlyList<CachedPackage>> ListAsync(CancellationToken token);
}
