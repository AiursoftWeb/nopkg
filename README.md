# Nopkg

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/aiursoftweb/nopkg/blob/master/LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/nopkg/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/nopkg/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/nopkg/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/nopkg/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fnopkg)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fnopkg&scope=baseline&ecosystem=all)
[![Man hours](https://manhours.aiursoft.com/r/github.com/aiursoftweb/nopkg.svg)](https://manhours.aiursoft.com/r/github.com/aiursoftweb/nopkg.html)
[![Website](https://img.shields.io/website?url=https%3A%2F%2Fnopkg.aiursoft.com)](https://nopkg.aiursoft.com)
[![Docker](https://img.shields.io/docker/pulls/aiursoft/nopkg.svg)](https://hub.docker.com/r/aiursoft/nopkg)

Nopkg is an npm registry built on Aiursoft Template, and the npm sibling of [Nupkg](https://github.com/aiursoftweb/nupkg). Publish packages you own, cache upstream tarballs on demand, and optionally restrict mirrored packages to versions admitted by S360.

![screenshot](./screenshot.png)

## Try

Browse the running [Nopkg instance](https://nopkg.aiursoft.com).

## What is Nopkg?

- Standard npm metadata, scoped packages, exact versions and dist-tags under `/npm/`.
- On-demand caching of original tarballs, with integrity and package identity checks.
- Optional S360 admission for metadata and every download, including cached archives.
- Personal API keys, package ownership, immutable publishing and distribution-tag management.
- Cache-only operation when the upstream is unavailable.
- Shared Aiursoft accounts, OIDC, roles, themes and localization.

See [publishing packages](./docs/publishing.md) for personal keys, ownership and publishing the same artifact to internal and public registries. The npm audit API is not implemented; requests return 501 rather than a clean security report.

## Use the registry

Point npm at your own deployment:

```sh
npm config set registry https://nopkg.example.com/npm/
npm install your-package
```

For a public npm lockfile, use npm's `--replace-registry-host=always` option with your registry to download the locked versions through Nopkg without changing their integrity values.

## Run manually

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js](https://nodejs.org/).

```sh
cd src/Aiursoft.Nopkg/wwwroot
npm ci
npm run build
cd ..
dotnet run
```

Open `http://localhost:5000`. The default local administrator is `admin@default.com` with password `Admin@123456!`. Configure authentication before exposing a deployment.

## Run in Microsoft Visual Studio

Open `Aiursoft.Nopkg.sln` and press **F5**.

## Run in Docker

The shared CI builds Linux amd64 and arm64 images. Successful master pipelines publish `aiursoft/nopkg:latest` and `hub.aiursoft.com/aiursoft/nopkg:latest`.

```sh
docker run -d --name nopkg -p 8080:5000 -v nopkg-data:/data aiursoft/nopkg:latest
```

Persist `/data` for the default SQLite database, package cache and application files. The registry is `/npm/`; health checks use `/health`. Use the standard port-selecting installer:

```sh
curl -fsSL https://raw.githubusercontent.com/aiursoftweb/nopkg/master/install.sh | sudo bash -s -- 8080
```

## Configuration

Set `Registry:Upstream` and `Registry:StoragePath` for the source and persistent cache. Set `Registry:CacheOnly=true` to prevent upstream requests. Use one application instance per cache directory.

S360 admission is disabled in the development default. To enable it, configure `Registry:S360:Enabled`, `Registry:S360:Server` and a dedicated `Registry__S360__CheckKey` environment secret. See [registry configuration](docs/registry.md) for admission, offline use and protocol limits. Shared account, database and theme settings follow [Aiursoft Template](https://template-docs.aiursoft.com/).

## Contributing

Use the existing build, tests and `lint.sh`. NugetNinja maintains the shared Template files and dependency conventions through `ninja.yaml`. Normal `nuget.config` uses public NuGet for external contributors; company CI uses the separate internal build configuration.

## Credits

Built on [Aiursoft Template](https://github.com/aiursoftweb/template). Licensed under the [MIT License](./LICENSE).
