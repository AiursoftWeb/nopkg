# Registry configuration

Nopkg serves the npm protocol under `/npm/`. This preview supports read-only metadata and archive requests; publishing returns 405 and npm audit returns 501. An unsupported audit endpoint is never reported as a successful empty vulnerability scan.

## Storage and upstream

```text
Registry__Upstream=https://registry.npmjs.org/
Registry__StoragePath=/data/npm
Registry__CacheOnly=false
```

Use HTTPS for the upstream. Loopback HTTP is accepted for local development. Archive URLs must remain on the configured upstream origin; redirects are not followed. Run one application instance per cache directory.

Metadata lists known versions. An archive is downloaded only when requested, checked against npm integrity and its internal package identity, then committed to the persistent cache. Concurrent requests for one version share a download within the application instance. Files interrupted before verification are not served.

## S360 admission

```text
Registry__S360__Enabled=true
Registry__S360__Server=https://s360.example.com
Registry__S360__CheckKey=<dedicated personal S360 check key>
```

Deploy S360 with its artifact admission API first. Configure the same original registry source in both applications. Use a dedicated check key without baseline publication permission; keep it outside Git.

With admission enabled, metadata and tags expose only admitted exact versions, including versions not yet downloaded. A version grant alone is insufficient: independent source and vulnerability verification must be current. Downloads must match the admitted original source, version and raw SHA256, as well as npm integrity and the package's real identity. Admission is checked again after download and on every cached read. The package catalog shows admitted known versions separately from stored archives.

Revoked versions are removed from metadata, and cached archive requests return 403. S360 failures return 503; there is no uncontrolled fallback. Metadata, conditional requests, GET, HEAD and byte ranges all use the same gate. Package responses use `Cache-Control: no-store`; reverse proxies must respect it. A previously downloaded client copy cannot be revoked remotely.

The first protocol preview had no provenance sidecar. Adoption of those cached files requires checking original distribution origin, integrity, package identity and current independent admission. Already recorded provenance is never silently reassigned to a different upstream.

Global artifact admission does not approve a project's dependency graph or declare production use. Keep the project's existing S360 checks. Nopkg does not create approval rules or submit observations while handling registry reads.

## Offline operation

Prepare the entire required dependency closure before disconnecting the upstream. Lazy caching alone cannot serve an archive that has never been downloaded.

Set `Registry__CacheOnly=true` to prevent all upstream requests. Cached metadata and original archives remain usable; the internal S360 service must still be reachable when admission is enabled. Cache-only mode does not disable source identity or admission checks.

Use a lockfile whose resolved archive URLs point to this Nopkg deployment. Preserve package versions and integrity values when migrating URLs. Verify with an empty npm client cache, normal install scripts and the application's real build commands. Native tools, SDKs and packages that fetch additional assets need their own internal supply before a complete build can work without the internet.

Disabling `Registry__S360__Enabled` disables admission enforcement. Do not use that switch as a workaround for rejected dependencies or a policy outage.

### One policy for the internal registry

Set `Registry:S360:PolicySource` to this registry's HTTPS URL, for example `https://nopkg.aiursoft.com`. In S360, allow it in `SupplyChain:NpmSources` and add a `SupplyChain:Mirrors` entry with `Ecosystem=npm`, `Source` equal to the internal URL, and `Upstream=https://registry.npmjs.org`. Set `Registry:Upstream` to the same upstream. Approve each package name and version range only for Nopkg; no duplicate npmjs.org approval is required.

Metadata advertises policy-eligible versions without downloading tarballs. A cold tarball remains private until S360 independently downloads the configured upstream archive, checks its identity and SHA256, and completes its vulnerability check. Cached downloads still require current admission. Locally published names take precedence over mirrors. Leaving `PolicySource` empty preserves origin-based admission.
