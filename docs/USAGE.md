# Usage

**What it does today:** exports the committed state, resolves each solution's SDK, restores, loads every project with Roslyn (syntax fallback otherwise), and writes the graph, observations, links, snippets, generated sources, context packs, `manifest.json` (written last, after validation against [contract v1](CONTRACT.md)) and `run.json`. `verify <generation>` repeats the checks. Restore needs the network; mount `/cache` to keep packages between runs. `--config` takes a YAML file with conventions: `messaging` (publish, subscribe, handler interfaces; see `tests/eshop/conventions.yml`), `workflow` (plugin interfaces of an in-house workflow engine and how each names its BPMN node or flow event; `metadata.nodeFields` copied from `*.ProcessMetaData.json`; see `tests/fixtures/f22-workflow-terraform/conventions.yml`) and `realtime` (helpers over SignalR). Configuration values are stored only as destinations (D21).

## GitHub Action

```yaml
- uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
- id: intelligence
  uses: AlbertoMitroi/dotnet-intelligence@v1
  with:
    fail-on: failed          # failed | partial | never
- uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7.0.2
  with:
    name: context
    path: ${{ steps.intelligence.outputs.generation-path }}
```

| Input | Default | Meaning |
|---|---|---|
| `path` | `.` | Repository to analyze, relative to the workspace |
| `solution` | | Only this solution (repository-relative) |
| `config` | | Configuration file, relative to the workspace (hashed; not applied in 0.1.0) |
| `output` | `intelligence-out` | Generation directory, relative to the workspace; must be absent or empty |
| `include-working-tree` | `false` | Analyze the working tree; the generation is marked `dirty` |
| `fail-on` | `failed` | When the step fails: `failed`, `partial` or `never` |

| Output | Meaning |
|---|---|
| `generation-path` | Generation directory, relative to the workspace |
| `manifest-path` | `manifest.json`, relative to the workspace; empty without a generation |
| `status` | `complete`, `partial` or `failed` |
| `coverage` | Compact JSON: `total`, `semantic`, `syntax`, `failed`, `notAnalyzed` |

- Docker container actions run only on Linux runners (amd64 or arm64).
- The NuGet cache used by the engine is `.dotnet-intelligence/nuget` in the workspace; restore it with `actions/cache`.
- The action image runs as root, as GitHub requires for Docker container actions (D16). The engine hands the output and cache directories back to the owner of the workspace when it finishes.
- Diagnostics with severity warning or error appear as workflow annotations.

## docker run

```bash
mkdir -p intelligence-out
docker run --rm --user "$(id -u):$(id -g)" \
  -v "$PWD:/src:ro" -v "$PWD/intelligence-out:/out" \
  ghcr.io/albertomitroi/dotnet-intelligence:0.1.0 analyze --src /src --out /out
```

| Mount | Mode | Content |
|---|---|---|
| `/src` | read-only | The repository (a Git checkout; shallow clones work) |
| `/out` | read-write | The generation directory (absent or empty) |
| `/cache` | read-write, optional | NuGet packages (`NUGET_PACKAGES=/cache/nuget`) |
| `/config` | read-only, optional | Caller configuration and convention files |

- `--user "$(id -u):$(id -g)"` makes the output belong to the calling user on Linux hosts; the image's default user is the non-root `app` (uid 1654).
- `version --json` prints the engine version, the Roslyn build and API version, the SDK set with each compiler, the runtimes, and whether the Roslyn rule holds.
- `analyze` options: `--src`, `--out`, `--config`, `--solution`, `--include-working-tree`, `--offline` (recorded; restore still runs and reports failures without network).
- Private NuGet feeds (used from Step 3 on): pass `NuGetPackageSourceCredentials_<source key>=Username=...;Password=...` as an environment variable; it never appears in outputs or logs.
- `DI_IMAGE_DIGEST`: the image cannot know its own digest; pass it to have it recorded in the manifest, otherwise the manifest says `unknown`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | complete |
| 10 | partial |
| 20 | preflight blocked |
| 30 | nothing could be loaded (for example, `/src` is not a Git repository); no manifest |
| 40 | cancelled; no manifest |
| 50 | integrity check failed; no manifest |
| 1 | internal error (also: output directory not empty) |
| 2 | usage error |

## Events

`analyze` writes one JSON object per line on stdout: `run.start`, `stage.start` and `stage.end` (stages `export`, `discovery`, `preflight`, `restore`, `load`, `extract`, `syntax-fallback`, `adapters`, `links`, `artifacts`), `diagnostic`, `run.end` (with `status` and `exitCode`). Logs go to stderr.

## Working copy

- The default analyzes the committed `HEAD`: every blob is read from Git objects, so bytes and blob ids are the committed ones on every host OS (D8). `.gitattributes` end-of-line rules do not change them.
- Symbolic links are not followed (`symlink-skipped`). Submodules are not analyzed (`submodule-uninitialized` or `submodule-not-analyzed`). Git LFS pointers stay pointers (`lfs-pointer`).
- Remote URLs are recorded without credentials.

## Threat model

Analysing a repository will (from Step 3 on) run its MSBuild evaluation and restore, which can execute code from that repository inside the container. The container is the isolation boundary: mount only the four directories above, give `/src` read-only, and pass credentials only through environment variables for the feeds they belong to.
