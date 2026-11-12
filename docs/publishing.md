# Publishing packages

Create a personal key under **API Keys**. Keys are stored as hashes, shown once,
and can expire or be revoked. They use the account's current permissions.
Publishing requires **Publish Packages**; administrators use **Manage All Packages**
to assign ownership or explicitly convert a mirrored package to internal publishing.

Add a token reference to your private `.npmrc` (never commit a token):

```ini
//nopkg.aiursoft.com/npm/:_authToken=${NOPKG_TOKEN}
```

Then publish and install with the standard npm client:

```sh
npm publish package.tgz --registry https://nopkg.aiursoft.com/npm/ --provenance=false
npm install @example/package --registry https://nopkg.aiursoft.com/npm/
npm dist-tag add @example/package@1.0.0 stable --registry https://nopkg.aiursoft.com/npm/
```

The first successful authorized publication reserves a new package name. Other
publishers cannot take it over. Mirrored names require explicit administrator
conversion; old mirror files are retained but do not become internal releases.
An internal name serves internal versions only and never fills gaps from upstream.
Deleting an owner retains the name and releases for administrator management.
Published versions cannot be overwritten or unpublished. Distribution tags may
point only to existing internal versions.

Package identity and installation metadata come from the actual archive. The
original tarball is stored unchanged with server-computed integrity values.
Uploads are limited to 64 MiB by default (`Registry:MaxPublishArchiveBytes`, also
bounded by `Registry:MaxArchiveBytes`). Reads remain public. Mirrored packages
continue to require S360 admission when enabled; internal publishing follows
publisher permissions and does not approve a project's dependencies in S360.

Interactive npm login, provenance attestations, unpublishing and the npm audit
API are not implemented. Audit requests return 501, not a clean security report.
Use the existing S360 audit workflow.

## Publish the same artifact to two registries

Use Node.js 24 and npm 11 or newer. Build and approve a tarball once, then run the distribution step on a worker
allowed to reach the public registry. Build runners and NugetNinja can remain
internal-only. With `NOPKG_TOKEN` and `NPM_TOKEN` provided by the worker's secret
store:

```sh
node scripts/publish-package.mjs ./package.tgz --report ./publish-results.json
```

The helper snapshots the archive before either submission and sends the same bytes to Nopkg and npm, attempts both targets even
if one fails, and writes separate results and the archive hash. It uses npm with
explicit registry selection, scoped temporary credentials, and lifecycle scripts
disabled. Use a current npm client that gives CLI flags priority over publishConfig.
Retry a failed target without rebuilding:

```sh
node scripts/publish-package.mjs ./package.tgz --target public --report ./public-retry.json
```

A duplicate succeeds only after the existing version's SHA512 integrity matches
and its distribution tag is updated successfully. A same-version conflict with
different bytes remains an error. `NOPKG_REGISTRY` and `NPM_REGISTRY` can select
alternative registries for isolated testing; no credential is embedded in a URL.
