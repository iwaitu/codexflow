English | [简体中文](README.zh-CN.md)

# CodexFlow

A local host for an AI coding agent: chat → plan → approve → execute in isolated Docker containers → verify (build, tests, secret scan, dependency vulnerability scan, model review).
All state lives in a single SQLite file, depending only on Docker and a model endpoint.

**Status: Early stage.** There is no `codexflow` CLI yet; interact with it via the local host's HTTP API, and drive/verify workflows using the scenario scripts in `scripts/e2e/devsim`.

## Requirements

- .NET 10 SDK
- Docker (both verification and user code run inside containers)
- An OpenAI-compatible chat endpoint (`ChatCompletions` mode)

## Running

Configure the model endpoint via the `VllmAgent` configuration section or environment variables. **Keep API keys in environment variables only—do not write them into files.**

```bash
export VllmAgent__ApiUrl='https://your-model-host/v1/{1}'
export VllmAgent__ApiMode='ChatCompletions'
export VllmAgent__Model='your-model'
export VllmAgent__ApiKey='...'
dotnet run --project CodexFlow.Local
```

- Listens only on `127.0.0.1`, port configured by `Local:Port` (default `5206`).
- Generates an access token on startup and writes it to `host.token` next to the database (default `.codexflow/host.token`); requests missing a token or using an invalid token return `401`.
- Startup preflight checks verify that model settings are complete, the model endpoint responds, and the Docker daemon is available. Any issues are reported together and the process exits with code `1`. Set `Local:SkipPreflight=true` to disable.
- Default state database is `.codexflow/state.db`, configured by `Storage:SqlitePath`, with migrations applied automatically on startup.

### Verification Container Image

Verification and user code run inside the `codexflow-verification` image, which must be built first:

```bash
docker build -t codexflow-verification:10.0.403-gitleaks8.30.1 docker/verification
```

`CodexFlow.Local/appsettings.json` pins the image name and the SHA-256 hash of the secret-scanning configuration. Do not alter the bytes of `docker/verification/gitleaks.toml` (`.gitattributes` already disables line-ending conversion); if you modify it, update that hash accordingly.

### Vulnerability Advisories

- NuGet advisories are refreshed automatically by the host.
- npm and PyPI advisories are prepared offline; if they are older than 48 hours, the gateway refuses to use them (the dependency layer returns `Error` and will not pass). Refresh them manually, which downloads several hundred MB from `osv.dev`:

```bash
python scripts/e2e/prepare-osv-advisories.py --out ./osv-advisories
```

Then point `ArtifactAudit:OsvAdvisoryDirectory` to that directory.

## Testing

```bash
dotnet test CodexFlow.Core.Tests
```

Currently only `CodexFlow.Core.Tests` is included. Integration tests for the gateway, job executor, and verification are not yet added (a SQLite-based test host is needed first). Some tests require Docker or a live model endpoint and are skipped when unavailable.

## Developer Simulation (`devsim`)

`scripts/e2e/devsim` converses with a running host in natural language (cloning a repository, asking questions, requesting a plan, approving, waiting for jobs, verification) and checks whether the system behaves correctly using structured assertions—targeting system issues rather than differences in model phrasing.

```bash
python scripts/e2e/devsim list
python scripts/e2e/devsim run bugfix-python-pager --api http://127.0.0.1:5206 --token-file .codexflow/host.token
```

See `scripts/e2e/devsim/README.md` for details. `faultproxy.py` acts as a proxy between the host and the model to deterministically trigger rarely hit paths such as retries.

## License

[MPL-2.0](LICENSE). Copyright: iwaitu.
