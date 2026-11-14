using System.Net;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aiursoft.Nopkg.Registry;

public partial class NpmRegistry(HttpClient client, RegistryOptions options, S360AdmissionClient? admission = null, ILocalPackageStore? localPackages = null)
{
    private readonly SemaphoreSlim[] _archiveGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly SemaphoreSlim[] _metadataGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly S360AdmissionClient? _admission = options.S360.Enabled
        ? admission ?? throw new ArgumentException("S360 admission must be configured when enabled.") : null;
    private readonly Uri _origin = ValidateOrigin(options.Upstream);
    private readonly string _root = Path.GetFullPath(options.StoragePath);

    [GeneratedRegex(@"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static string PackageName(string input)
    {
        if (input.Length > 214 || !NamePattern().IsMatch(input) || input.Split('/').Any(s => s is "." or ".."))
            throw new ArgumentException("Invalid npm package name.");
        return input;
    }

    private static Uri ValidateOrigin(string value)
    {
        var uri = new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
        if ((uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The npm upstream must use HTTPS, or HTTP on loopback for local development.");
        return uri;
    }

    private string PackageDirectory(string name) => Path.Combine(_root,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PackageName(name)))));

    private static SemaphoreSlim Gate(SemaphoreSlim[] gates, string key) => gates[(int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key))) % (uint)gates.Length)];

    private async Task<byte[]> DownloadMetadataAsync(string name, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_origin, Uri.EscapeDataString(name)));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("Package not found upstream.");
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        await CopyLimitedAsync(input, output, 32 * 1024 * 1024, token);
        return output.ToArray();
    }

    public async Task<JsonObject> MetadataAsync(string name, CancellationToken token = default)
    {
        PackageName(name);
        if (localPackages != null && await localPackages.FindAsync(name, token) is { } local) return local.Metadata;
        var approved = _admission == null || _admission.UsesRegistryPolicy ? null : await _admission.ApprovedAsync(name, _origin.AbsoluteUri.TrimEnd('/'), token);
        if (approved is { Count: 0 }) throw new FileNotFoundException("No admitted package versions found.");
        var gate = Gate(_metadataGates, name);
        await gate.WaitAsync(token);
        try
        {
            var directory = PackageDirectory(name);
            var file = Path.Combine(directory, "metadata.json");
            byte[] data;
            if (options.CacheOnly)
            {
                if (!File.Exists(file)) throw new FileNotFoundException("Package is not cached.");
                data = await File.ReadAllBytesAsync(file, token);
            }
            else
            {
                // Cache fallback is limited to unavailable upstreams. A real 404 is not hidden.
                try { data = await DownloadMetadataAsync(name, token); }
                catch (HttpRequestException) when (File.Exists(file)) { data = await File.ReadAllBytesAsync(file, token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && File.Exists(file)) { data = await File.ReadAllBytesAsync(file, token); }
            }
            var metadata = JsonNode.Parse(data) as JsonObject ?? throw new InvalidDataException("Invalid npm metadata.");
            if (metadata["name"]?.GetValue<string>() != name || metadata["versions"] is not JsonObject)
                throw new InvalidDataException("Upstream metadata does not describe the requested package.");
            ValidateVersions((JsonObject)metadata["versions"]!);
            if (localPackages != null && !await localPackages.RecordMirrorAsync(name, token))
                return (await localPackages.FindAsync(name, token) ?? throw new FileNotFoundException("Package ownership changed. Retry the request.")).Metadata;
            if (!options.CacheOnly)
            {
                // A version already cached locally is immutable, even when upstream metadata changes.
                if (Directory.Exists(directory))
                    foreach (var savedFile in Directory.EnumerateFiles(directory, "*.tgz.json"))
                    {
                        var version = Path.GetFileName(savedFile)[..^9];
                        var archive = savedFile[..^5];
                        if (!File.Exists(archive) || metadata["versions"]?[version]?["dist"] is not JsonObject incoming) continue;
                        var saved = JsonNode.Parse(await File.ReadAllBytesAsync(savedFile, token)) as JsonObject
                            ?? throw new InvalidDataException("Invalid cached artifact metadata.");
                        var oldAlgorithm = IntegrityAlgorithm(saved, out var oldHash);
                        var newAlgorithm = IntegrityAlgorithm(incoming, out var newHash);
                        if (oldAlgorithm != newAlgorithm || !oldHash.SequenceEqual(newHash))
                            await VerifyAsync(archive, incoming, token);
                    }
                Directory.CreateDirectory(directory);
                await AtomicWriteAsync(file, data, token);
            }
            return await FilterMetadataAsync(metadata, approved, token);
        }
        finally { gate.Release(); }
    }

    private static void ValidateVersions(JsonObject versions)
    {
        if (versions.Any(v => v.Key.Length > 128 || !VersionPattern().IsMatch(v.Key)))
            throw new InvalidDataException("Invalid package version in metadata.");
    }

    public static JsonObject RewriteMetadata(JsonObject metadata, string registryUrl)
    {
        var result = (JsonObject)metadata.DeepClone();
        var name = PackageName(result["name"]!.GetValue<string>());
        if (result["versions"] is JsonObject versions)
            foreach (var (version, node) in versions)
            {
                if (version.Length > 128 || !VersionPattern().IsMatch(version)) throw new InvalidDataException("Invalid package version in metadata.");
                if (node is not JsonObject manifest || manifest["dist"] is not JsonObject dist)
                    throw new InvalidDataException("Missing package distribution metadata.");
                dist["tarball"] = registryUrl.TrimEnd('/') + "/" + name + "/-/" + Uri.EscapeDataString(name.Split('/')[^1] + "-" + version + ".tgz");
            }
        return result;
    }

    private static HashAlgorithmName IntegrityAlgorithm(JsonObject dist, out byte[] expected)
    {
        var integrity = dist["integrity"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(integrity))
        {
            var entries = integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (prefix, algorithm) in new[] { ("sha512-", HashAlgorithmName.SHA512), ("sha384-", HashAlgorithmName.SHA384), ("sha256-", HashAlgorithmName.SHA256) })
            {
                var entry = entries.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
                if (entry == null) continue;
                try { expected = Convert.FromBase64String(entry[prefix.Length..]); }
                catch (FormatException) { throw new InvalidDataException("Invalid npm integrity value."); }
                return algorithm;
            }
            throw new InvalidDataException("Unsupported npm integrity algorithm.");
        }
        var shasum = dist["shasum"]?.GetValue<string>();
        if (shasum == null || shasum.Length != 40 || !shasum.All(Uri.IsHexDigit))
            throw new InvalidDataException("Missing package integrity metadata.");
        expected = Convert.FromHexString(shasum);
        return HashAlgorithmName.SHA1; // npm's legacy shasum; S360 admission is a separate stage.
    }

    public async Task<string> ArchiveAsync(string name, string version, CancellationToken token = default)
    {
        PackageName(name);
        if (version.Length > 128 || !VersionPattern().IsMatch(version)) throw new ArgumentException("An exact npm version is required.");
        if (localPackages != null && await localPackages.FindAsync(name, token) is { } local)
            return await LocalArchiveAsync(local, version, token);
        var gate = Gate(_archiveGates, name + "@" + version);
        await gate.WaitAsync(token);
        try
        {
            var directory = PackageDirectory(name);
            var path = Path.Combine(directory, version + ".tgz");
            // An artifact has a frozen integrity record; mutable tags never replace its bytes.
            var manifestFile = path + ".json";
            if (File.Exists(path) && File.Exists(manifestFile))
            {
                var saved = JsonNode.Parse(await File.ReadAllBytesAsync(manifestFile, token)) as JsonObject
                    ?? throw new InvalidDataException("Invalid cached artifact metadata.");
                await VerifyAsync(path, saved, token);
                await AdmitArchiveAsync(path, name, version, saved, manifestFile, token);
                if (localPackages != null && await localPackages.FindAsync(name, token) is { } owned)
                    return await LocalArchiveAsync(owned, version, token);
                return path;
            }
            if (options.CacheOnly) throw new FileNotFoundException("Package version is not cached.");
            var expected = await ExpectedHashAsync(name, version, token);
            var metadata = await MetadataAsync(name, token);
            if (metadata["versions"]?[version] is not JsonObject manifest || manifest["dist"] is not JsonObject dist)
                throw new FileNotFoundException("Package version not found.");
            if (manifest["name"]?.GetValue<string>() != name || !VersionsMatch(manifest["version"]?.GetValue<string>(), version))
                throw new InvalidDataException("Package version identity does not match metadata.");
            ValidateTarballOrigin(dist);
            var url = new Uri(dist["tarball"]!.GetValue<string>(), UriKind.Absolute);
            IntegrityAlgorithm(dist, out _);
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("Tarball not found upstream.");
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await CopyLimitedAsync(input, output, options.MaxArchiveBytes, token);
                await VerifyAsync(temporary, dist, token);
                await VerifyIdentityAsync(temporary, name, version, token);
                await VerifyAdmissionAsync(temporary, name, version, dist, expected, token);
                // Finish both files after verification, even if the downloading client disconnects.
                // A request still requires a complete sidecar and repeats admission on every read.
                File.Move(temporary, path, overwrite: true);
                await AtomicWriteAsync(manifestFile, Encoding.UTF8.GetBytes(dist.ToJsonString()), CancellationToken.None);
                if (localPackages != null && await localPackages.FindAsync(name, token) is { } owned)
                    return await LocalArchiveAsync(owned, version, token);
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> CachedMirrorNamesAsync(CancellationToken token)
    {
        var names = new List<string>();
        if (!Directory.Exists(_root)) return names;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var path = Path.Combine(directory, "metadata.json");
            if (!File.Exists(path)) continue;
            var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(path, token)) as JsonObject;
            var name = PackageName(metadata?["name"]?.GetValue<string>() ?? throw new InvalidDataException("Invalid cached package name."));
            if (PackageDirectory(name) != directory) throw new InvalidDataException("Cached package directory does not match its identity.");
            names.Add(name);
        }
        return names;
    }

    public Task<bool> HasCachedMirrorAsync(string name) => Task.FromResult(File.Exists(Path.Combine(PackageDirectory(name), "metadata.json")));

    public string PublishedArchivePath(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid published archive identity.");
        return Path.Combine(_root, "published", hash.ToLowerInvariant() + ".tgz");
    }

    private async Task<string> LocalArchiveAsync(LocalPackage local, string version, CancellationToken token)
    {
        if (!local.ArchiveHashes.TryGetValue(version, out var expected)) throw new FileNotFoundException("Published package version not found.");
        var path = PublishedArchivePath(expected);
        await using var input = File.OpenRead(path);
        if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)).Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException("Published archive integrity verification failed.");
        return path;
    }

    public async Task<JsonObject> ValidatePublishedArchiveAsync(string path, string name, string version, CancellationToken token)
    {
        PackageName(name);
        if (version.Length > 128 || !VersionPattern().IsMatch(version)) throw new ArgumentException("An exact npm version is required.");
        await VerifyIdentityAsync(path, name, version, token, Math.Min(options.MaxArchiveBytes, options.MaxPublishArchiveBytes) * 8);
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        JsonObject? result = null;
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            var segments = entry.Name.TrimEnd('/').Split('/');
            if (entry.Name.StartsWith('/') || entry.Name.Contains('\\') || segments.Any(x => x is "" or "." or ".." || x.Contains(':')) ||
                entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                throw new InvalidDataException("Published archive contains an unsafe path or link.");
            if (entry.Name.Split('/') is [_, "package.json"] && entry.DataStream != null)
            {
                using var manifest = new MemoryStream();
                await CopyLimitedAsync(entry.DataStream, manifest, 1024 * 1024, token);
                result = JsonNode.Parse(manifest.ToArray()) as JsonObject ?? throw new InvalidDataException("Invalid package manifest.");
            }
        }
        return result ?? throw new InvalidDataException("Missing package manifest.");
    }

    private static bool VersionsMatch(string? manifest, string version) => manifest == version || manifest == "v" + version;

    private async Task VerifyIdentityAsync(string path, string name, string version, CancellationToken token, long? maxExpandedBytes = null)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        var found = false;
        long total = 0;
        var count = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            total = checked(total + entry.Length);
            if (++count > 100000 || total > (maxExpandedBytes ?? options.MaxArchiveBytes * 8))
                throw new InvalidDataException("Unpacked package exceeds the configured size limit.");
            // npm strips one directory layer. DefinitelyTyped uses the package name
            // instead of "package"; accept that layout without weakening identity checks.
            var parts = entry.Name.Split('/');
            if (parts.Length != 2 || parts[1] != "package.json") continue;
            if (parts[0] is "" or "." or ".." || parts[0].Contains('\\') || parts[0].Contains(':') ||
                found || entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null)
                throw new InvalidDataException("Ambiguous or invalid package identity.");
            using var data = new MemoryStream();
            await CopyLimitedAsync(entry.DataStream, data, 1024 * 1024, token);
            var manifest = JsonNode.Parse(data.ToArray()) as JsonObject;
            if (manifest?["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var actualName) || actualName != name ||
                manifest["version"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var actualVersion) || !VersionsMatch(actualVersion, version))
                throw new InvalidDataException("Tarball package identity does not match the requested version.");
            found = true;
        }
        if (!found) throw new InvalidDataException("Tarball has no top-level package manifest.");
    }

    private static async Task VerifyAsync(string path, JsonObject dist, CancellationToken token)
    {
        var algorithm = IntegrityAlgorithm(dist, out var expected);
        using var hasher = IncrementalHash.CreateHash(algorithm);
        await using var input = File.OpenRead(path);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0) hasher.AppendData(buffer, 0, read);
        if (!CryptographicOperations.FixedTimeEquals(hasher.GetHashAndReset(), expected))
            throw new InvalidDataException("Tarball integrity does not match registry metadata.");
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > maximum) throw new InvalidDataException("Registry response exceeds the configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    public async Task<IReadOnlyList<CachedPackage>> CachedPackagesAsync(CancellationToken token = default, string query = "")
    {
        var local = localPackages == null ? [] : await localPackages.ListAsync(token);
        var localNames = local.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var matchingLocal = local.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!Directory.Exists(_root)) return matchingLocal;
        var cached = new List<(string Name, string Directory, List<string> Versions)>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var file = Path.Combine(directory, "metadata.json");
            if (!File.Exists(file)) continue;
            var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(file, token)) as JsonObject
                ?? throw new InvalidDataException("Invalid cached metadata.");
            var name = PackageName(metadata["name"]!.GetValue<string>());
            if (localNames.Contains(name) || !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var versions = metadata["versions"] as JsonObject ?? throw new InvalidDataException("Invalid cached versions.");
            ValidateVersions(versions);
            cached.Add((name, directory, versions.Select(v => v.Key).ToList()));
        }

        // Read one current policy snapshot for the matching catalog. Never retain admission
        // decisions across requests: revocations and pending verification must remain visible.
        HashSet<(string Name, string Version)>? allowed = null;
        if (_admission != null && cached.Count > 0)
        {
            if (_admission.UsesRegistryPolicy)
            {
                var candidates = cached.SelectMany(p => p.Versions.Select(v =>
                    new MirrorCandidate("npm", p.Name, v, _admission.PolicySource))).ToList();
                allowed = (await _admission.CandidatesAsync(candidates, token))
                    .Select(c => (c.Name, c.Version)).ToHashSet();
            }
            else
            {
                var approved = cached.Count == 1
                    ? (await _admission.ApprovedAsync(cached[0].Name, _origin.AbsoluteUri.TrimEnd('/'), token)).Values.ToList()
                    : await _admission.ApprovedCatalogAsync(_origin.AbsoluteUri.TrimEnd('/'), token);
                var identities = cached.SelectMany(p => p.Versions.Select(v => (p.Name, Version: v))).ToHashSet();
                var decisions = await _admission.AssessAsync(approved.Where(a => identities.Contains((a.Name, a.Version))).ToList(), token);
                allowed = decisions.Where(d => d.Trusted && d.PendingVerification == false)
                    .Select(d => (d.Dependency.Name, d.Dependency.Version)).ToHashSet();
            }
        }
        var result = new List<CachedPackage>();
        foreach (var package in cached)
        {
            var versions = package.Versions.Where(v => allowed == null || allowed.Contains((package.Name, v))).ToList();
            if (versions.Count == 0) continue;
            var stored = versions.Count(v => File.Exists(Path.Combine(package.Directory, v + ".tgz")) &&
                File.Exists(Path.Combine(package.Directory, v + ".tgz.json")));
            result.Add(new CachedPackage(package.Name, versions.Count, stored));
        }
        return result.Concat(matchingLocal).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static async Task AtomicWriteAsync(string path, byte[] data, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, data, token); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
