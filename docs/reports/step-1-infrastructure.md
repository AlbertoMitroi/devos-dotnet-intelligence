# Step 1 report: infrastructure

Date: 2026-10-29. Branch `step-1/infra`. Repository: https://github.com/AlbertoMitroi/dotnet-intelligence (public, D1).

## Gate 1

| Gate item | Result | Evidence |
|---|---|---|
| Image builds for both architectures; self-test shows the SDK set, matching runtimes and a compatible Roslyn | **pass for SDK 10**; SDK 9 deferred (D15) | CI run 40880354904, jobs `verify (amd64)` and `verify (arm64)`; self-test below |
| `verify.sh` and `verify.ps1` pass on a host with only Docker | **pass** | macOS host (Docker Desktop 29.7.2, arm64); CI `ubuntu-24.04` and `ubuntu-24.04-arm` |
| Skeleton analyze through the Action on a fixture produces a manifest and the declared outputs in CI | **pass**, with a caveat | CI step "Run the action on a fixture" |
| Report written | this file | |

Caveat for the third item: no image is published yet. CI runs the real `action.yml` with its `image:` line replaced by a `Dockerfile` that builds `FROM` the image verified a step earlier. Inputs, workspace mount, outputs and file ownership are the real mechanics; pulling `docker://ghcr.io/...` is not exercised until the first release.

## What was built

- `DotnetIntelligence.slnx` with Contracts, Analysis, Adapters (empty until Step 3), Artifacts, Cli; unit and integration test projects (xUnit v3 on Microsoft.Testing.Platform).
- `analyze`: committed-state export (D8), solution discovery (`.slnx`, `.sln`, `.slnf`), `graph/projects.ndjson`, `graph/diagnostics.ndjson`, `manifest.json` last, then `run.json`. Every generation is `partial` (`extraction-not-available`).
- `version --json`, `gha` (GitHub Action mode, D12), exit codes 0, 1, 2, 10, 30.
- Dockerfile stages `build`, `test`, `runtime`, `action` (D16) on the pinned SDK 10.0.403 image.
- `scripts/verify.sh`, `scripts/verify.ps1`, `scripts/fixture-repo.sh`, `scripts/update-dependencies.sh`.
- `action.yml`, `ci.yml`, `release.yml`, `examples/workflows/`.
- Docs: `USAGE.md`, `DEPENDENCIES.md` (images, actions, 39 NuGet packages with licenses), decisions D16 and D17, D3 and D4 updated.

## Commands and results

### Local, macOS host

```bash
scripts/verify.sh
```

Result: exit 0 in 42 s (warm Docker cache). Test stage: unit `total: 73, failed: 0`; integration `total: 21, failed: 0, skipped: 0`. `f00-minimal: two runs identical`. Action mode: `status=partial`, `coverage={"failed":0,"notAnalyzed":1,"semantic":0,"syntax":0,"total":1}`.

```bash
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v di-dockercli:/opt/dockercli:ro \
  -e PATH=/opt/dockercli:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
  -v "$PWD:$PWD" -w "$PWD" mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a... pwsh -NoProfile -File scripts/verify.ps1
```

The host has no PowerShell, so `verify.ps1` ran in the SDK image (which ships `pwsh`) against the host's Docker daemon, with the Docker CLI copied from `docker.io/library/docker:29-cli@sha256:5e6baf67325a79f64ed3bed6863954d5e05d0f3d4e57cbdb14809346814bbfdb`. Result: exit 0, every step passed.

### Self-test (`version --json`, runtime image, arm64)

```json
{"engine":{"name":"dotnet-intelligence","version":"0.1.0"},"imageDigest":"unknown","roslyn":{"apiVersion":"5.9.0","build":"5.9.0-1.26357.3"},"roslynCompatible":true,"runtime":{"architecture":"arm64","framework":".NET 10.0.14","os":"linux"},"runtimes":[{"name":"Microsoft.AspNetCore.App","version":"10.0.14"},{"name":"Microsoft.NETCore.App","version":"10.0.14"}],"schemaVersion":"0.1","sdks":[{"compilerApiVersion":"5.9.0","compilerBuild":"5.9.0-1.26523.112","version":"10.0.403"}]}
```

### CI

- Run 40879625981: `verify.sh` passed on both architectures; `verify.ps1` failed with docker exit code 125 because PowerShell bound `-r` and `-x` to the helper's own parameters. Fixed in commit "Step 1: pass docker arguments to verify.ps1 helper as one array".
- Run 40880354904: **success**. `verify (amd64)` 46 s, `verify (arm64)` 56 s, `compare amd64 and arm64` printed `amd64 and arm64 generations are byte-identical`, `lint` (actionlint 1.7.12) passed. Action smoke: `status=partial manifest=action-out/manifest.json generation=action-out`, and the manifest belongs to the runner user (`test -O`).

### Roslyn rule

The nuget.org package `Microsoft.CodeAnalysis` 5.9.0 is build `5.9.0-1.26357.3`; SDK 10.0.403 ships build `5.9.0-1.26523.112`. The first test run compared those strings and failed (`Engine Roslyn 5.9.0-1.26357.3 is older than an SDK compiler`). Both assemblies have version 5.9.0, which is what analyzers bind to, so the rule now compares assembly versions (D3, corrected). Both builds are recorded in `version --json` and the manifest.

### Spike S1

`WorkspaceSpikeTests.Msbuild_workspace_loads_the_minimal_fixture_semantically` passed in the test stage on both architectures: `MSBuildWorkspace.Create()` without `Microsoft.Build.Locator`, only runtime 10.0.14, `.slnx` solution, one project loaded with `Greeter.cs`, no failure-level `WorkspaceFailed` diagnostic, no compilation error. D4 accepted for SDK 10.

### Image size

`docker image ls`: runtime `1.4GB`, action `1.4GB`, test stage `1.87GB` (arm64, local).

### First run on eShop (current engine)

```bash
git clone --filter=blob:none --no-checkout https://github.com/dotnet/eShop.git src
git -C src checkout 913acea66708a59595351174cee9b29f37179d29
docker run --rm --network none --user "$(id -u):$(id -g)" -v "$PWD/src:/src:ro" -v "$PWD/out:/out" dotnet-intelligence:verify analyze --src /src --out /out
```

Exit 10. 28 projects from `eShop.slnx`, `eShop.Web.slnf`, `src/ClientApp/ClientApp.sln`, `tests/ClientApp.UnitTests/ClientApp.UnitTests.sln`. Stages: export 422 ms, discovery 13 ms, artifacts 60 ms. This corrects PLAN A.6, which said 21 and 10 projects: there are 19 under `src/` and 9 under `tests/`.

## Not validated

- SDK 9 (deferred, D15).
- Release workflow: not run. A public release needs a license (Q5); the GHCR package must then be made public (Q1). The manual dry run (`workflow_dispatch`) becomes possible once `release.yml` is on `main`.
- Example workflows: linted, not executed (they need a published image).
- `--offline` with a full cache: nothing is restored yet (Step 3).

## Deviations

- D15: SDK 10 only.
- D16: the action image runs as root.
- D17: the release commit carries the version; the workflow checks it instead of committing it.
- Exit code 2 (usage error) added next to the documented set.
