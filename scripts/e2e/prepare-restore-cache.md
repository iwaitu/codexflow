# prepare-restore-cache.py — trusted NuGet restore cache for the verification guest

Creates the read-only cache that the isolation backend mounts at `/restore-cache` (`Isolation:RestoreCacheHostPath`), so the
guest (`--network none`) can `dotnet restore` without any network. **Do not use `prepare-audit-cache.py` for this**: that
script builds a deliberately vulnerable attack fixture for tests only.

## What it guarantees
* Input = a reviewed JSON list of exact `id` + `version` (no ranges, wildcards, floating versions, duplicates; unknown keys,
  missing `reviewedBy`/`reviewedAtUtc` are rejected). A target repository is never read, parsed or executed.
* The script writes its own minimal project (`[x.y.z]` exact references) and a `nuget.config` with `<clear/>` + nuget.org only,
  inside the pinned verifier image (never pulled), with `HOME=/tmp/h`. Only the new empty output directory is mounted; no host
  NuGet.config, credentials or global-packages folder. Prep has network; verification runs stay `--network none`.
* The host then verifies the result: every requested package exists at the exact version, every package's
  `.nupkg.metadata` source is `https://api.nuget.org/v3/index.json`, and `restore-cache-manifest.json` is written
  (image id, input-list SHA-256, every package incl. transitive with `.nupkg` SHA-256, `packagesSha256`).
* Any restore error, missing package, foreign source or non-empty output directory => exit code 1. The cache is built in a
  staging directory and moved into place only after verification.

## Input schema (`schema: 1`)
Common keys: `reviewedBy` (required), `reviewedAtUtc` (required, ISO-8601 **UTC** timestamp of the real review), `targetFramework` (optional, default `net10.0`). Unknown keys are rejected. Give **exactly one** of:

* `packages`: `[ {"id": "...", "version": "x.y.z"} ]` — one generated project restores all of them (fine for a single project / single dependency graph).
* `groups`: `[ {"name": "<project>", "packages": [ {id, version}, ... ], "sdkPackages": [ {id, version}, ... ]} ]` — one generated project **per group**, all restored into the same cache. `name` is `[A-Za-z0-9._-]`, unique (case-insensitive), max 64 groups; ids must be unique inside a group (not across groups, versions may differ between groups). `sdkPackages` is optional.

### Why per-project groups (closure rule)
NuGet resolves transitive versions **per project** (nearest-wins within that project's graph, including packages that flow in through `ProjectReference`). One merged project can therefore pick *higher* transitive versions than a real project needs, and the offline guest then cannot find the lower ones (observed: `CleanApp.ServiceDefaults` needed five `Microsoft.Extensions.*` 10.0.0 packages that a merged restore had unified upward). So write one group per real project, and let each group contain that project's own `PackageReference`s **plus those of its whole `ProjectReference` closure**, with the exact versions written in the csproj files. Verify with a replica restore (`--network none`) of every project, not only with the manifest.

### `sdkPackages` (PackageDownload)
For MSBuild SDKs referenced with `<Sdk Name="X" Version="Y"/>` (or `Sdk="X/Y"`): they cannot be `PackageReference`s (`NU1213`, package type DotnetPlatform). They are emitted as `<PackageDownload Include="X" Version="[Y]"/>` (exact): the package is only downloaded and its nuget.org source verified; the SDK is **not** evaluated or executed during preparation. An SDK may add **implicit** packages whose ids/versions it chooses (Aspire AppHost: `Aspire.Dashboard.Sdk.linux-x64` and `Aspire.Hosting.Orchestration.linux-x64` at the `Aspire.Hosting.AppHost` version, observed with 13.1.0): list those under `sdkPackages` too. In the guest the NuGet SDK resolver reads only the *global* packages folder (never fallback folders), so the cache must be the global folder (`NUGET_PACKAGES=/restore-cache`, mounted read-only) — see `.committee-review/claude-ro-global-cache-experiment.md`. Example (AppHost group of `restore-cache-cleanapp-bug.packages.json`):
```json
{ "name": "CleanApp.AppHost",
  "packages": [ {"id": "Aspire.Hosting.AppHost", "version": "13.1.0"}, /* + closure packages */ ],
  "sdkPackages": [ {"id": "Aspire.AppHost.Sdk", "version": "9.5.0"},
                   {"id": "Aspire.Dashboard.Sdk.linux-x64", "version": "13.1.0"},
                   {"id": "Aspire.Hosting.Orchestration.linux-x64", "version": "13.1.0"} ] }
```
The manifest records `roots` (all packages and sdkPackages), `groups`, and every restored package with its `.nupkg` SHA-256.

## `reviewedAtUtc` must be the real UTC review time
Do not write a future or date-only placeholder. (The first versions of the two example lists said `2026-11-08T00:00:00Z`, later than the actual 2026-11-07T17:18Z build; corrected to the real UTC review time `2026-11-07T17:35:31Z`. Metadata only: package ids/versions are unchanged. The frozen cleanapp cache's `inputListSha256` `579968cc…` refers to the archived original input `.committee-review/restore-cache-input-cleanapp-bug.original.json`.)

## Usage
```
export ARTIFACT_AUDIT_IMAGE=codexflow-verification:10.0.403-gitleaks8.30.1
export ARTIFACT_AUDIT_CACHE_PACKAGES=/path/to/reviewed-packages.json     # see scripts/e2e/fixtures/restore-cache-clean.packages.json
export ARTIFACT_AUDIT_CACHE_OUT=/path/to/NEW-empty-cache-dir
python3 scripts/e2e/prepare-restore-cache.py
# later / in CI, offline, detects added/removed/modified packages:
ARTIFACT_AUDIT_CACHE_OUT=/path/to/cache ARTIFACT_AUDIT_CACHE_VERIFY_ONLY=1 python3 scripts/e2e/prepare-restore-cache.py
```
Then point `Isolation:RestoreCacheHostPath` at the directory. Review the manifest (transitive packages are included) before
use; replace a cache by building a new directory, never by mutating a running one.

## Verified (2026-11-08, Windows + Docker Desktop, image 10.0.403-gitleaks8.30.1)
Clean fixture `restore-cache-clean.packages.json` (Newtonsoft.Json 13.0.3, Serilog 4.1.0; neither is in a NuGet advisory range) built
into an external directory: exit 0, 2 packages, `packagesSha256=6de38b08…`; `VERIFY_ONLY` exit 0; a guest with `--network none` restored a
project using both packages from the cache (exit 0). Negative runs all exit 1: floating `13.*`, range, duplicate id, missing
version `999.0.0`, nonexistent id, injection attempt in id, missing review fields, unknown key, non-empty output dir, tampered nupkg
(verify-only).
