English | [简体中文](README.zh-CN.md)

# CodexFlow

**An AI coding workspace on your desktop, with local execution, human approvals, and reusable business Skills.**

CodexFlow brings conversations, planning, tool approvals, workspace files, changes, and jobs into one Electron application. A .NET host runs locally, persists state in SQLite, and connects to your configured model endpoint. You can work against a local folder, review a proposed plan, approve execution, and inspect the result without switching between separate API scripts.

The first-generation desktop application is implemented. The source version is **0.1.6**; this repository contains the desktop client, local host, reusable .NET libraries, and test tooling.

![CodexFlow Desktop workspace](docs/images/desktop-workspace.jpg)

## Contents

- [Features](#features)
- [Screenshots](#screenshots)
- [Run from source](#run-from-source)
- [Your first workflow](#your-first-workflow)
- [Plugins and business Skills](#plugins-and-business-skills)
- [Configuration and data](#configuration-and-data)
- [Standalone host](#standalone-host)
- [Execution and verification](#execution-and-verification)
- [Architecture](#architecture)
- [Development and testing](#development-and-testing)
- [Troubleshooting](#troubleshooting)
- [Documentation and license](#documentation-and-license)

## Features

| Area | What you can do |
| --- | --- |
| Workspace conversations | Create workspaces, open local folders, and organize multiple conversations under each workspace. |
| Chat experience | Read streamed responses, Markdown and code blocks; attach files; inspect tool calls and context usage. |
| Planning and approvals | Review plans and respond to tool execution approval cards before the relevant operation proceeds. |
| Workspace inspection | Open the Files, Changes, and Jobs panels alongside the conversation. |
| Execution | Use local execution or opt into a Docker backend for isolated execution and container verification. |
| Model settings | Configure the endpoint, API mode, model, context limit, and sampling parameters from the desktop UI. |
| Business integrations | Manage OpenAPI / MCP business plugins, discover capabilities, and build reusable Skills through a five-step wizard. |
| Local lifecycle | Let the desktop manage its backend, or connect it to an independently running local host. |
| Persistence and recovery | Keep application state in SQLite; use SSE streaming with reconnection and reconciliation. |

Local execution uses your machine and its installed tools. A locally running host does not imply that model inference is offline: prompts and relevant task context go to the model endpoint you configure.

## Screenshots

These are real Windows screenshots captured with computer use from a source-built desktop client on **2026-11-17**. The client was connected to its real managed backend using a separate, empty demonstration data directory. No sample conversations or successful jobs were fabricated.

### Plugin catalog

Manage business system connections and capability catalogs in one place.

![Business plugin catalog](docs/images/desktop-plugins.jpg)

### Five-step Skill builder

Connect a system → choose capabilities → describe and generate → validate and test → publish a version.

![Business Skill builder](docs/images/desktop-skill-builder.jpg)

### Execution settings

The captured instance uses **Local** execution and **Ask** permissions.

![Execution and permission settings](docs/images/desktop-settings.jpg)

The screenshot session verified startup, backend connection, and navigation through these screens. It did not run a model task, publish a Skill, or perform Docker verification.

## Run from source

### Prerequisites

| Dependency | Purpose |
| --- | --- |
| .NET 10 SDK | Build and run the backend. `global.json` requests `10.0.401` with latest-patch roll-forward. |
| Node.js 22 and npm | Install and run the desktop client; Node 22 is also used by client CI. |
| Git | Clone the repository and support repository workflows. |
| Compatible model endpoint | Required for AI conversations and generation. Model settings accept `ChatCompletions` and `Responses`. |
| Docker (optional) | Needed for Docker execution and container verification. |

Build the backend **before** launching the desktop client. This avoids file locks and ensures the managed host uses the current source.

```powershell
git clone https://github.com/iwaitu/codexflow.git
cd codexflow

dotnet build CodexFlow.Local -c Release

cd clients/desktop
npm ci
npm run dev
```

For a built frontend instead of the development server:

```powershell
# From clients/desktop, after the backend build and npm ci
npm run build
npm run preview
```

The desktop starts and monitors its own local backend. You do not need to start a second `dotnet run` process for this mode. Outside packaged builds, backend discovery prefers the Release executable, then Debug, then falls back to `dotnet run`.

To use an isolated data directory for a demo or development session, set this before starting the desktop:

```powershell
$env:CODEXFLOW_DATA_DIR = Join-Path $env:TEMP 'codexflow-demo'
npm run dev
```

This separates host data; it does **not** isolate user-level model settings. Close the demo instance and remove the environment override from your terminal when returning to your usual data directory.

The repository also defines Windows x64, Linux x64, and macOS arm64 packaging. See the repository's [Releases page](https://github.com/iwaitu/codexflow/releases) for published assets and [release documentation](docs/releasing.md) for the build pipeline. This README's runtime walkthrough was verified on Windows.

## Your first workflow

1. Start the desktop and confirm the bottom status bar reports a connected managed host.
2. Complete the welcome configuration if prompted, or open the gear icon → model settings. Enter the endpoint, API mode, model, and credentials required by your provider.
3. Choose **Open folder** to work with an existing local project, or create a workspace. Use the **+** next to a workspace to add a conversation.
4. Begin with a focused request, for example: “Explain this project's structure and identify its test entry points.”
5. For changes, ask for a plan, review the proposed work, and respond to the relevant approval cards.
6. Inspect **Files**, **Changes**, and **Jobs**. Review verification results and any skipped checks before accepting the outcome.

Example requests:

```text
Explain this repository's entry point and main modules.
Investigate this failing test, propose a fix, then wait for approval.
Implement this small feature and show the changes and test results.
```

These are suggested prompts, not recorded results from the screenshot session. Available actions depend on the configured model, tools, backend, and permissions.

## Plugins and business Skills

The bundled host enables this feature by default (`BusinessSkills:Enabled=true`). Set it to `false` in host configuration to disable it.

The **Plugins & Skill** area provides three views: **My plugins**, **My Skills**, and the **five-step builder**.

1. **Connect a system:** add a business plugin and configure its OpenAPI / MCP connection and credentials.
2. **Choose capabilities:** discover the capability catalog and select the operations needed by the workflow.
3. **Describe and generate:** explain the business task and generate a draft Skill.
4. **Validate and test:** inspect validation feedback and test against the intended system; handle required approvals.
5. **Publish a version:** publish the reviewed Skill and access it from My Skills.

The implementation separates document and execution credential slots and exposes human-controlled risk classification and write-operation approvals. Connections, credentials, model access, and a usable capability catalog are prerequisites for a real workflow; an empty catalog, as shown above, is a fresh starting state.

Business plugin credential persistence currently uses Windows DPAPI. Linux and macOS reject credential persistence until a native secure store is implemented; enabling the feature does not remove that platform limitation.

## Configuration and data

Model settings saved through the UI are stored in the user-level `~/.codexflow/appsettings.json` and are watched for changes. The settings API returns key status and a masked suffix, rather than the full key. Keep that file and runtime credentials out of source control and screenshots.

Environment variables are useful for scripted standalone launches. Nested .NET settings use double underscores:

| Setting | Purpose |
| --- | --- |
| `VllmAgent__ApiUrl` | Model endpoint URL; the adapter supports an endpoint-name placeholder such as `/v1/{1}`. |
| `VllmAgent__ApiMode` | `ChatCompletions` or `Responses`; match the provider. |
| `VllmAgent__Model` | Provider model identifier. |
| `VllmAgent__ApiKey` | Provider credential for the process environment. |
| `Local__Port` | Standalone listening port; default `5206`. Managed mode requests a dynamic port. |
| `Local__PreflightMode` | Startup checks: use `Report` to report problems without strict startup rejection, or `Off` to disable preflight. |
| `CODEXFLOW_DATA_DIR` | Desktop-managed backend data-directory override. |
| `CODEXFLOW_BACKEND_BIN` | Desktop backend executable or DLL override. |

In managed mode, host data defaults to the desktop application's user-data directory under `host-data`, and backend logs are under `logs/backend.log`. The desktop supplies access and control credentials through a pipe. Standalone mode writes discovery/token files for client connections. Back up the data directory with the host stopped; user-level settings are separate from that backup.

## Standalone host

Use this mode for HTTP clients, integrations, or the developer simulation runner. From the repository root, in PowerShell:

```powershell
$env:VllmAgent__ApiUrl = 'https://your-model-host/v1/{1}'
$env:VllmAgent__ApiMode = 'ChatCompletions'
$env:VllmAgent__Model = 'your-model'
# Supply VllmAgent__ApiKey securely in the process environment.
dotnet run --project CodexFlow.Local
```

The host listens on loopback, normally `http://127.0.0.1:5206`. Use the generated `host.token` for API authentication; missing or invalid credentials return `401`. The desktop connection wizard can discover a local instance or accept its address and token file. Operations requiring the control plane also need the corresponding control credential.

See [OpenAPI contracts](docs/contracts/openapi.json) and [SSE event schemas](docs/contracts/sse-events.schema.json) for the API surface. There is no dedicated `codexflow` CLI in this repository; the desktop application and HTTP host are the primary entry points.

## Execution and verification

**Local** runs supported commands on the host machine. **Docker** runs work in the configured container environment. Permissions and isolation are separate choices: approval is not a container boundary, and selecting Docker does not eliminate the need to review operations.

For the Docker verification backend, build the pinned image from the repository root:

```powershell
docker build -t codexflow-verification:10.0.403-gitleaks8.30.1 docker/verification
```

Then select/configure the Docker backend. Container verification includes build/test and audit paths; local-mode receipts may explicitly skip container checks. A completed job is not proof that every verification layer passed.

The image and secret-scanning configuration hash are pinned in `CodexFlow.Local/appsettings.json`. If changing `docker/verification/gitleaks.toml`, update the corresponding hash as well.

NuGet advisories are refreshed by the host. npm and PyPI advisory data require offline preparation:

```powershell
python scripts/e2e/prepare-osv-advisories.py --out ./osv-advisories
```

Set `ArtifactAudit:OsvAdvisoryDirectory` to the output directory. The download can be several hundred MB; data older than 48 hours is rejected by the dependency audit path. See the [verification cache guide](scripts/e2e/prepare-restore-cache.md) for related preparation.

## Architecture

```mermaid
flowchart LR
    UI[Electron + React desktop] --> Main[Electron main process]
    Main --> Host[Local ASP.NET Core host]
    Host --> Runtime[Planning and orchestration]
    Host --> DB[(SQLite)]
    Runtime --> Model[Configured model endpoint]
    Runtime --> Local[Local execution]
    Runtime --> Docker[Optional Docker execution]
    Runtime --> Plugins[Business plugins and Skills]
```

| Directory | Responsibility |
| --- | --- |
| `clients/desktop` | Electron lifecycle, preload bridge, React UI, client tests and packaging. |
| `CodexFlow.Local` | Local host entry point and composition. |
| `CodexFlow.Contracts` | Shared contracts. |
| `CodexFlow.Core` | Core services, planning, execution, configuration and business Skill foundations. |
| `CodexFlow.Orchestration` | Gateway sessions, jobs, workflow orchestration and audits. |
| `CodexFlow.Gateway.Http` | HTTP-facing API layer. |
| `CodexFlow.Storage.Sqlite` | SQLite persistence. |
| `CodexFlow.Core.Tests` | Backend unit and integration test coverage. |
| `scripts/e2e/devsim` | Natural-language workflow scenarios and structured assertions. |
| `docs/contracts` | OpenAPI and SSE contracts used by the desktop client. |
| `docker/verification` | Verification image and scanning configuration. |

The five library projects are packaged together with a shared version from `Directory.Build.props`. The desktop package version is stamped by the release pipeline.

## Development and testing

Backend tests, from the repository root:

```powershell
dotnet test CodexFlow.Core.Tests
```

Desktop checks, from `clients/desktop`:

```powershell
npm run lint
npm run typecheck
npm test
npm run build
npm run test:e2e
```

Regenerate API types after contract changes with `npm run generate:types`, then review changes under `src/shared/contracts`. Tests requiring Docker or a live model have additional prerequisites; passing client tests alone does not validate those integrations.

For a running standalone host, the developer simulator drives conversations, planning, approvals, jobs, and verification:

```powershell
python scripts/e2e/devsim list
python scripts/e2e/devsim run bugfix-python-pager --api http://127.0.0.1:5206 --token-file .codexflow/host.token
```

Adjust the token path to the active host's actual data directory. See the [devsim guide](scripts/e2e/devsim/README.md) for scenarios and fault injection.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Desktop is disconnected or repeatedly restarting | Inspect `logs/backend.log`; confirm the backend executable exists and builds successfully. |
| Backend build cannot copy DLLs | Close the desktop/host using that output directory, build again, then relaunch. |
| Changes to backend source do not appear | Rebuild Release; desktop discovery prefers an existing Release build over Debug. |
| UI opens but AI requests fail | Check endpoint, API mode, model, credentials, and provider connectivity. UI connection status only describes the local backend. |
| API returns `401` | Use the token from the active host instance and correct data directory. |
| Approval or settings controls are unavailable | Check whether the standalone connection has control-plane credentials. |
| Skill builder cannot proceed | Connect a plugin, discover capabilities, and confirm model configuration and feature availability. |
| Docker verification fails | Check the Docker daemon, pinned image, verification caches, and advisory freshness. |

## Documentation and license

- [Release process](docs/releasing.md)
- [Local execution threat model](docs/local-execution-threat-model.md)
- [Desktop and local execution design](docs/plans/local-execution-and-desktop-client.md)
- [Business Skill builder design](docs/plans/embedded-business-skill-builder.md)
- [Developer simulation guide](scripts/e2e/devsim/README.md)

Design documents describe design intent and may include work beyond a particular release. Use source, release notes, and runtime behavior to establish what is available.

[MPL-2.0](LICENSE). Copyright © 2026 iwaitu.
