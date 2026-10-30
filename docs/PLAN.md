# dotnet-intelligence: implementation plan (Step 0)

Status: Steps 1 to 4 implemented (see the traceability results and `docs/reports/`); Step 5 pending.
Source of requirements: [`IMPLEMENTATION-PROMPT.md`](IMPLEMENTATION-PROMPT.md) (section numbers below refer to it).
Decisions: [`DECISIONS.md`](DECISIONS.md). Open questions: [`OPEN-QUESTIONS.md`](OPEN-QUESTIONS.md).

Every fact in this plan comes from a command recorded in [Appendix A](#appendix-a-recorded-facts). Everything else is a plan, marked as such.

## 1. Baseline (recorded 2026-10-29)

| Item | Value | Appendix |
|---|---|---|
| .NET 10 (LTS, active) | SDK `10.0.403`, runtime `10.0.14` | A.2 |
| .NET 9 (STS, maintenance) | SDK `9.0.320`, runtime `9.0.22`; **deferred, not in the first image (D15)** | A.2 |
| SDK scope of the first image | **.NET 10 only (`10.0.403`)**, the SDK eShop main resolves to; SDK 9 added later as its own slice (D15) | A.6 |
| .NET 11 | `11.0.100-rc.3`, not GA; out of scope (D2) | A.2 |
| Base image (.NET 10 SDK) | `mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b` (multi-arch index) | A.3 |
| Source of the .NET 9 SDK (for the deferred slice) | `mcr.microsoft.com/dotnet/sdk:9.0.320-noble@sha256:9a905addac4d4d3bfdd28185030f874251614559b3a038200849619710b3136b` (multi-arch index) | A.3 |
| Roslyn for the engine | `Microsoft.CodeAnalysis.*` `5.9.0` (latest stable on nuget.org), MIT | A.4 |
| Compiler inside SDK 10.0.403 | `5.9.0-1.26523.112` on amd64 and arm64, so engine Roslyn `5.9.0` ≥ in-box compiler (D3) | A.5 |
| eShop main pin | `913acea66708a59595351174cee9b29f37179d29` (2026-10-15), `global.json` SDK `10.0.302`, `rollForward: latestFeature` | A.6 |
| eShop .NET 9 pin | `94e2643dc73b0be47cb956bd79af9863b87df845` (2025-09-18), `global.json` SDK `9.0.200`, `rollForward: latestFeature`, `allowPrerelease: true` | A.6 |
| Development host | Docker 29.7.2, linux/arm64 engine, Buildx builder with linux/amd64 | A.1 |
| CI runners | `ubuntu-24.04` (amd64) and `ubuntu-24.04-arm` (arm64), free on public repositories | A.7 |

## 2. Component plan

Dependency direction (an arrow means "is used by"): `Contracts → Analysis → Adapters → Artifacts → Cli`. `Artifacts` does not reference Roslyn; it reads only `Contracts` types, which keeps the builder usable for graphs from other engines.

| Component | Responsibility | Key types (planned) | Depends on |
|---|---|---|---|
| `schema/v1/*.schema.json` | The contract: one schema per record (3.2) plus the manifest | | |
| `tools/SchemaGen` | Deterministic generator: JSON Schema (the subset used by the contract) → C# records in `Contracts/Generated/` (D5) | `SchemaReader`, `CSharpEmitter` | none |
| `DotnetIntelligence.Contracts` | Generated records, canonical JSON writer and reader, id hashing, contract reader rules for consumers (3.5) | `CanonicalJson`, `IdFactory`, `GenerationReader`, `CapabilitySet` | none |
| `DotnetIntelligence.Analysis` | Working copy, discovery, preflight, restore, MSBuildWorkspace load, semantic and syntax extraction, evidence, snippets and redaction | `WorkingCopyExporter`, `SolutionDiscovery`, `SdkResolver`, `RestoreRunner`, `WorkspaceLoader`, `DocumentExtractor`, `SyntaxFallbackExtractor`, `EvidenceFactory`, `SnippetRedactor`, `ProcessRunner` (argument arrays only) | Contracts, Roslyn |
| `DotnetIntelligence.Adapters` | Generic framework adapters by resolved symbol: DI, ASP.NET Core, HTTP clients, gRPC (incl. `.proto`), messaging (heuristics + caller conventions), configuration keys, Aspire topology, EF Core `DbContext`, packages | `IAdapter`, `SymbolMatcher`, `ConventionFile`, `ProtoParser`, `ConfigKeyScanner`, `AssetsReader` | Analysis, Contracts |
| `DotnetIntelligence.Artifacts` | Linking (5.1), integrity validation (3.6), `index.sqlite`, context packs, wiki, `verify`, `query`, finalization (5.6) | `Linker`, `IntegrityValidator`, `SchemaValidator`, `IndexWriter`, `IndexDump`, `ContextPackWriter`, `WikiWriter`, `QueryEngine`, `GenerationFinalizer` | Contracts, Microsoft.Data.Sqlite |
| `DotnetIntelligence.Cli` | Commands `version`, `preflight`, `analyze`, `verify`, `query`, plus the GitHub Action mode (D12); NDJSON events, exit codes, SIGTERM handling | `Program`, `EventWriter`, `ExitCodes`, `GitHubActionMode` | all of the above |
| `tests/unit` | Pure logic: ids, canonical JSON, redaction, route canonicalization, link rules, query formatting | | |
| `tests/integration` | Fixtures 1 to 15 analysed in the image, contract fixtures, determinism, exit codes | | |
| `tests/eshop` | Ground truth, conventions, evaluator (run in the test image) | | |

### 2.1 Image (planned, D2, D3, D15)

- `build` stage: `sdk:10.0.403-noble@digest`, restores with a lock file (`packages.lock.json`, locked mode), publishes the CLI framework-dependent for `net10.0`.
- `test` stage: from `build`; runs unit and integration tests and the Roslyn rule test (R1.4).
- `runtime` stage: `sdk:10.0.403-noble@digest` (an SDK is required to evaluate analysed projects). Engine under `/opt/dotnet-intelligence`. User `app` (uid 1654, already present in the base image, A.5). Entry point: the CLI.
- Deferred SDK 9 slice (D15): copy `sdk/9.0.320`, `shared/Microsoft.NETCore.App/9.0.22`, `shared/Microsoft.AspNetCore.App/9.0.22` and the 9.0 targeting packs from `sdk:9.0.320-noble@digest`. Nothing in the first image may assume a single SDK: SDK resolution, the Roslyn rule and `version --json` iterate over the SDK set from the start.
- Image identity printed by `version --json`: engine version, Roslyn version, SDK set, runtime set, image digest when passed in by the build (`DI_IMAGE_DIGEST` is not knowable inside its own image, so the Action and CI pass it; otherwise `unknown`).

### 2.2 Determinism strategy (planned, D7)

- One canonical JSON writer for every NDJSON line and every JSON file: keys sorted ordinally, no insignificant whitespace, UTF-8 without BOM, `\n` line endings, numbers in invariant culture.
- Records sorted by id (ordinal). Ids are content hashes (D7), never counters or GUIDs.
- No timestamps, host paths, user names, durations or machine data in `graph/`, `snippets.ndjson`, `context/`, `wiki/` or `manifest.json`; those live only in `run.json`.
- Markdown writers use a fixed culture, fixed sort orders and `\n`.
- `index.sqlite` is checked through `IndexDump` (every table, rows sorted by primary key, canonical JSON lines).
- Determinism tests: two runs in the same container, two containers, amd64 vs arm64 in CI, CI vs macOS host (Step 5).

## 3. Delivery slices

One commit per slice, build green after each, commit message prefixed with the step (`Step 1: ...`).

| Step | Slices |
|---|---|
| 1 | 1a solution skeleton, `global.json`, lock files, dependency list with licenses · 1b Dockerfile build/test/runtime with both SDKs, Roslyn rule test, spike S1 recorded · 1c `version --json` · 1d working copy export, discovery, skeleton `analyze` writing projects, diagnostics and a manifest (real data, scope stated in README) · 1e `verify.sh`, `verify.ps1` · 1f `action.yml` + GitHub Action mode · 1g `ci.yml` (both architectures, byte comparison) · 1h `release.yml` (GHCR, SBOM, provenance, schema asset, `v1` tag) · 1i examples · 1j report |
| 2 | 2a schemas for every record · 2b SchemaGen and generated types, drift check · 2c canonical JSON and id factory · 2d integrity and schema validation, `verify` · 2e consumer reader rules · 2f contract fixtures · 2g `CONTRACT.md` · 2h report |
| 3 | 3a preflight and SDK resolution · 3b restore and post-restore analyzer check · 3c workspace load and load modes · 3d entities, declarations, partial and generated parts · 3e references and calls · 3f inheritance, implementations, possible runtime targets · 3g syntax fallback · 3h DI · 3i ASP.NET Core · 3j HTTP clients · 3k gRPC · 3l messaging and conventions · 3m configuration and secrets · 3n topology and data · 3o packages · 3p events, exit codes, SIGTERM, annotations · 3q `--repo-url` · 3r fixtures 1 to 15 with `expected.yml` · 3s report |
| 4 | 4a linker · 4b `index.sqlite` and dump · 4c context packs · 4d wiki · 4e `query` · 4f finalization and Action outputs · 4g report |
| 5 | 5a pin and ground truth (committed before any engine run) · 5b conventions · 5c evaluator · 5d `eshop-validation.yml` · 5e macOS host comparison · 5f measurements · 5g usefulness kit · 5h report |
| SDK 9 (D15) | 9a add SDK 9 to the image, spike S1 · 9b SDK 9 fixture(s) and two-solution fixture · 9c eShop .NET 9 commit through the workflow · 9d report addendum. Scheduled after Gate 5 unless the review moves it earlier. |

## 4. Risks and spikes

Each spike runs at the start of its step; its result is recorded in DECISIONS.md before dependent work starts.

| Id | Risk | Spike | Fallback |
|---|---|---|---|
| S1 | MSBuildWorkspace 5.x runs MSBuild in an out-of-process BuildHost. Unverified: that the BuildHost starts with only the image's runtimes, and (once SDK 9 is added) that it picks the SDK from each solution's `global.json`. | Step 1b: load a net10.0 fixture in the image and record the resolved MSBuild path. Slice 9a: repeat with a net9.0 fixture pinned to SDK 9. | One analysis subprocess per resolved SDK (`DOTNET_MSBUILD_SDK_RESOLVER_*` set explicitly), results merged by the parent. |
| S2 | The Razor source generator fails to load in the workspace (CS8032), so Blazor components vanish silently. | Step 3c: Razor fixture plus eShop `WebAppComponents`. | Report as `partial` with CS8032 detail; syntax fallback for `.razor`-generated types is not possible, so the gap stays visible. |
| S3 | eShop `ClientApp` and `HybridApp` need MAUI workloads that the image does not contain. | Step 3b with fixture 14. | Expected outcome, reported as `unsupported` (missing workload) with the workload id; run continues. |
| S4 | Roslyn output differs between amd64 and arm64 (ordering, floating point in constant folding). | Byte comparison in `ci.yml` from Step 1g on. | Fix by canonicalisation; never by excluding fields. |
| S5 | Aspire 13 hosting APIs differ from older samples (`AddProject<T>`, `WithReference` overloads). | Step 3n on fixture 8 and eShop `eShop.AppHost`. | Adapter matches by containing type and method name across the overloads present in the resolved assemblies. |
| S6 | Image size with two SDKs makes the Action slow to pull. | Measured from Step 1b, reported in every gate. | Drop unused packs from the 9.0 copy; never drop an SDK. |
| S7 | Analysing projects runs their MSBuild targets and analyzers (code execution from the analysed repository) inside the container. | Documented threat model in USAGE.md (Step 1). | Container isolation is the boundary; no host mounts other than the four in 2.2; credentials only through env. |

## 5. Traceability

Columns: requirement id, prompt section, planned code, planned test or recorded check, result. All results are `pending` until the step runs. Every gate checkbox has its own row (`G<step>.<n>`).

### Step 1: infrastructure

| Id | Requirement | Section | Planned code | Planned test / check | Result |
|---|---|---|---|---|---|
| R1.1 | Repository layout as specified | 2.1 | whole repo | `tests/unit/LayoutTests` checks required paths exist | pass: LayoutTests |
| R1.2 | .NET 10 SDK base pinned by digest; `--list-sdks` and `--list-runtimes` show the image's SDK set. SDK 9 side by side is deferred (D15, slice 9a) | 2.2 | `Dockerfile` runtime stage | `tests/integration/ImageSelfTest` runs both commands in the runtime image and compares with the declared SDK set | pass for SDK 10 (ImageToolchainTests, verify self-test); SDK 9 deferred (D15) |
| R1.3 | SDK set printed by `version --json` | 2.2, 4.1 | `Cli/VersionCommand` | `ImageSelfTest` compares `version --json` with `--list-sdks` | pass: verify self-test |
| R1.4 | Engine Roslyn ≥ compiler of every SDK in the image; build fails otherwise | 2.2 | `Analysis/RoslynCompatibility` | `RoslynRuleTest` in the test stage reads `csc.dll -version` per SDK | pass: assembly 5.9.0 ≥ 5.9.0 (D3 corrected) |
| R1.5 | linux/amd64 and linux/arm64 with Buildx | 2.2 | `ci.yml`, `release.yml` | CI matrix on native runners (D10) | pass: CI run 40880354904, native runners |
| R1.6 | Mounts `/src` ro, `/out`, `/cache` with `NUGET_PACKAGES`, `/config` ro | 2.2 | `Dockerfile` env, `Cli` defaults | `MountContractTest`: write attempt to `/src` fails, `NUGET_PACKAGES=/cache/nuget` | partial: analyze works with `/src:ro` and `NUGET_PACKAGES=/cache/nuget`; dedicated write-attempt test not written |
| R1.7 | Non-root user; entry point is the CLI | 2.2 | `Dockerfile` | `ImageSelfTest` checks `id -u` ≠ 0 and entrypoint | pass: verify checks `id -u` ≠ 0 |
| R1.8 | Network only for restore and clone; `--offline` works with `--network none` and a full cache | 2.2 | `Analysis/RestoreRunner` | Step 1: skeleton analyze under `--network none`; full check in Step 3 (R3.10) | partial: every run uses `--network none`; offline restore from cache is Step 3 |
| R1.9 | Private feed credentials via `NuGetPackageSourceCredentials_*`, never in outputs or logs | 2.2, 1.4.6 | `Analysis/RestoreRunner`, log filter | `CredentialLeakTest`: sentinel credential, grep of `/out`, stdout, stderr | pass: Credentials_never_reach_outputs_or_logs (no restore yet) |
| R1.10 | `safe.directory` for the engine's own Git calls only | 2.3 | `Analysis/Git` (`-c safe.directory=/src` per call) | `GitSafeDirectoryTest`: repo owned by another uid; global git config unchanged | pass: Repositories_owned_by_another_user_are_read_without_changing_git_config |
| R1.11 | Export committed HEAD to `/work`; `--include-working-tree` copies the tree and marks dirty | 2.3 | `Analysis/WorkingCopyExporter` (D8) | `WorkingCopyTests`: CRLF blob, `eol=crlf` attribute, dirty flag | pass: WorkingCopyTests |
| R1.12 | Repository-relative forward-slash paths, blob ids, line and column spans | 2.3 | `Analysis/EvidenceFactory` (D9) | `PathAndSpanTests` | partial: paths and blob ids pass; spans arrive with Step 3 |
| R1.13 | Shallow clones work; uninitialised submodules become diagnostics | 2.3 | `WorkingCopyExporter` | `WorkingCopyTests`: `--depth 1` clone, submodule fixture | pass: WorkingCopyTests |
| R1.14 | `verify.sh` and `verify.ps1` run the same Docker-only steps | 2.4 | `scripts/` | Run both on the macOS host (pwsh in a container for `.ps1` if no host pwsh) and in CI | pass: CI on amd64 and arm64; macOS host (`verify.sh`, and `verify.ps1` through pwsh in a container) |
| R1.15 | Dev container optional; nothing requires it | 2.4 | `.devcontainer/` | CI never uses it; check recorded in report | pass: CI does not use it |
| R1.16 | Docker container action referencing the published image by version | 2.5 | `action.yml` (D11) | `ActionManifestTest` parses `action.yml`: `runs.using: docker`, image `docker://ghcr.io/...:<semver>` | pass: ActionManifestTests; published image not yet released |
| R1.17 | Inputs `path`, `solution`, `config`, `output`, `include-working-tree`, `fail-on` with defaults | 2.5 | `action.yml`, `Cli/GitHubActionMode` | `ActionManifestTest` + `GitHubActionModeTests` | pass: ActionManifestTests, GitHubActionModeTests |
| R1.18 | Outputs `generation-path`, `manifest-path`, `status`, `coverage` | 2.5 | `Cli/GitHubActionMode` | `GitHubActionModeTests` reads the `GITHUB_OUTPUT` file | pass: CI action smoke test |
| R1.19 | Output and NuGet cache inside the workspace | 2.5 | `GitHubActionMode` | CI job uses `actions/upload-artifact` and `actions/cache` on those paths | pass: CI action smoke test |
| R1.20 | Examples: upload artifact, OCI push, plain `docker run` | 2.5 | `examples/workflows/` | `examples-lint` job (actionlint in a container) and one example executed in CI | partial: linted by actionlint; not executed (needs a published image) |
| R1.21 | CI builds both architectures, runs tests, analyses fixtures, compares outputs byte for byte | 2.6 | `ci.yml` | CI run link recorded in the report | pass: CI run 40880354904 |
| R1.22 | Release: GHCR push with SBOM and provenance, `action.yml` updated, schema published | 2.6 | `release.yml` | First release tag run; `docker buildx imagetools inspect` shows attestations | not validated: no release yet (license Q5) |
| R1.23 | Engine semver, contract `schemaVersion`, Action major tag `v1` | 2.6 | `Directory.Build.props`, `release.yml` | `VersioningTests` | partial: versions agree (ActionManifestTests); `v1` tag not created yet |
| R1.24 | Every dependency pinned with license; base images by digest | 1.5.4 | `docs/DEPENDENCIES.md`, lock files | `DependencyPinTests`: lock files present, every `FROM` has `@sha256:`, every package listed with license | pass: DependencyPinTests |
| G1.1 | Image builds for both architectures; self-test shows SDK 9 and 10, matching runtimes, compatible Roslyn | Gate 1 | | R1.2 to R1.5 in CI | pass for SDK 10 (D15) |
| G1.2 | `verify.sh` and `verify.ps1` pass on a Docker-only host | Gate 1 | | R1.14 | pass |
| G1.3 | Skeleton analyze through the Action on a fixture produces a manifest and the declared outputs in CI | Gate 1 | | `ci.yml` job `action-smoke` | pass, with the action built FROM the verified image (no published image yet) |
| G1.4 | `docs/reports/step-1-infrastructure.md` | Gate 1 | | file exists, every claim has its command | pass: docs/reports/step-1-infrastructure.md |

### Step 2: contract v1

| Id | Requirement | Section | Planned code | Planned test / check | Result |
|---|---|---|---|---|---|
| R2.1 | Generation layout | 3.1 | `Artifacts/GenerationLayout` | `LayoutContractTests` | pending |
| R2.2 | Technology-neutral records; .NET detail only under `ext.dotnet.*` | 3 (goal), 3.4 | `schema/v1` | `SchemaNeutralityTest`: no `dotnet`-specific field outside `ext` | pending |
| R2.3 | Manifest fields | 3.2 | `schema/v1/manifest.schema.json` | schema tests on the valid fixture | pending |
| R2.4 | Project, analysis context, entity, declaration, relation, evidence, observation, link, diagnostic fields | 3.2 | `schema/v1/*.schema.json` | one schema test per record | pending |
| R2.5 | Entity and package id formats; choice for symbols without doc comment id recorded | 3.3 | `Contracts/IdFactory`, D6 | `IdFactoryTests` | pending |
| R2.6 | Per-context observations | 3.3 | relation and declaration schemas | multi-TFM fixture (fixture 4) in Step 3 | pending |
| R2.7 | Ids without absolute paths, host data or random parts | 3.3, 1.4.9 | `IdFactory` | `IdPurityTest` scans all ids in fixture generations | pending |
| R2.8 | v1 kinds enumerated | 3.4 | schemas (`enum`) | `KindCatalogTests` | pending |
| R2.9 | Minor versions additive; `requiredCapabilities` | 3.5 | `Contracts/CapabilitySet` | `CompatibilityTests` | pending |
| R2.10 | Consumer rules: unknown required capability refuses; unknown fields preserved; unknown kinds kept, excluded from traversal and linking, reported | 3.5 | `Contracts/GenerationReader` | newer-minor fixture | pending |
| R2.11 | C# types generated from schema; regeneration diff fails the build | 3.6 | `tools/SchemaGen` | test stage regenerates and runs `git diff --exit-code` equivalent in-process | pending |
| R2.12 | Self-validation before the manifest; failure means no manifest and exit 50 | 3.6, 4.1 | `Artifacts/GenerationFinalizer` | `FinalizerFailureTests` | pending |
| R2.13 | Integrity: ids exist, no duplicates, evidence file at blob id, contexts exist, checksums | 3.6 | `Artifacts/IntegrityValidator` | invalid fixtures | pending |
| R2.14 | `verify <generation>` for consumers | 3.6, 5.5 | `Cli/VerifyCommand` | contract fixtures | pending |
| R2.15 | Fixtures: one valid; dangling reference, duplicate id, checksum mismatch, unknown required capability; newer minor | 3.7 | `tests/contract/` | `ContractFixtureTests` | pending |
| R2.16 | `docs/CONTRACT.md` with rules and schemas | 3.5 | docs | doc test: every schema file linked | pending |
| G2.1 | Schema, generated types, CONTRACT.md, fixtures exist | Gate 2 | | R2.11, R2.15, R2.16 | pending |
| G2.2 | `verify` accepts valid fixtures, rejects each invalid one with a readable reason | Gate 2 | | `ContractFixtureTests` asserts message text | pending |
| G2.3 | `docs/reports/step-2-contract.md` | Gate 2 | | | pending |

### Step 3: extractor

| Id | Requirement | Section | Planned code | Planned test / check | Result |
|---|---|---|---|---|---|
| R3.1 | `version --json` | 4.1 | `Cli/VersionCommand` | `CliContractTests` | pending |
| R3.2 | `preflight --src --json` | 4.1, 4.2.3 | `Cli/PreflightCommand`, `Analysis/SdkResolver` | fixtures 13 to 15 | pending |
| R3.3 | `analyze --src --out [--config] [--solution] [--include-working-tree] [--offline]` | 4.1 | `Cli/AnalyzeCommand` | `CliContractTests` | pending |
| R3.4 | `analyze --repo-url --ref` clones inside the container | 4.1 | `Analysis/RemoteSource` | integration test against a local bare repo served over `file://` | pending |
| R3.5 | NDJSON events on stdout, logs on stderr, workflow annotations in Actions | 4.1 | `Cli/EventWriter` | `EventStreamTests` (every stdout line parses) | pending |
| R3.6 | Exit codes 0, 10, 20, 30, 40, 50, 1 | 4.1 | `Cli/ExitCodes` | one test per code | pending |
| R3.7 | SIGTERM: graceful stop, no manifest | 4.1 | `Cli/CancellationHost` | `CancellationTests` send SIGTERM mid-run | pending |
| R3.8 | Discovery of `.slnx`, `.sln`, `.slnf`; project once per context; primary TFM default; more through config | 4.2.2 | `Analysis/SolutionDiscovery` | fixtures 3, 4 | partial: .slnx, .sln, .slnf discovery and once-per-context pass (f03, eShop); secondary target frameworks reported, not analyzed (f04) |
| R3.9 | SDK per `global.json`; `unsupported-sdk` blocks only that solution; compiler version recorded | 4.2.3 | `Analysis/SdkResolver` | fixture 15 (a `global.json` pinned to SDK 9 is unsupported in the first image) + a two-solution fixture (one solution blocked, one analysed) | pass for SDK 10: f15 blocked with unsupported-sdk, exit 20; two-solution fixture not written |
| R3.10 | Restore into `/cache`; failures reported, run continues; `--offline` with `--network none` | 4.2.4, 2.2 | `Analysis/RestoreRunner` | fixtures 13, 14; offline rerun test | partial: restore into /cache, failures reported and run continues (f13, f14); offline rerun not tested |
| R3.11 | Analyzer and generator Roslyn versions from `project.assets.json`; newer than engine flagged | 4.2.5 | `Analysis/AnalyzerCompatibility` | fixture 12 with a generator referencing a newer Roslyn | implemented (analyzer-requires-newer-roslyn); no fixture exercises it |
| R3.12 | Workspace load: `WorkspaceFailed`, CS8032, zero generated documents, compile error counts, load mode | 4.2.6 | `Analysis/WorkspaceLoader` | fixtures 12 to 14 | pass: f12 (compilation errors when the generator is missing), f13 (workspace failure), f14 |
| R3.13 | Entities and declarations, all partial parts, partial members, generated documents with generator name | 4.2.7 | `Analysis/DocumentExtractor` | fixtures 1, 12 | pass: f01 (three files, partial method, RegexGenerator part), f03; eShop |
| R3.14 | References with usage kind | 4.2.7 | `DocumentExtractor` (IOperation) | fixtures 2, 3 + `SymbolFinder` cross-check | pass: f02 (usage kinds other and invocation); write/read/nameof/typeof/attribute only on eShop counts |
| R3.15 | `calls_declared_target` incl. constructors and extension methods | 4.2.7 | `DocumentExtractor` | fixture 2 | pass: f02 (overloads, extension method) |
| R3.16 | `inherits`, `implements`, `overrides` | 4.2.7 | `DocumentExtractor` | fixture 5 | pass: f05 |
| R3.17 | `possible_runtime_target` after the pass | 4.2.7 | `Analysis/DispatchResolver` | fixture 5 | pass: f05 |
| R3.18 | Syntax fallback, method `syntax` | 4.2.8 | `Analysis/SyntaxFallbackExtractor` | fixtures 13, 14 | pass: f14, f15 |
| R3.19 | DI registrations (all forms), extension methods across projects with path, conditional; consumers | 4.3 | `Adapters/DependencyInjection` | fixture 6 | pass: f06 (all forms, keyed, factory, TryAdd, hosted, typed client, conditional, extension in another project, GetRequiredService) |
| R3.20 | ASP.NET Core controllers and minimal APIs, `MapGroup`, folded constants, versioning in extensions | 4.3 | `Adapters/AspNetCore` | fixture 7 | pass: f07 (nested MapGroup through locals, folded constant, MapMethods, attribute and conventional routes); API versions only on eShop |
| R3.21 | HTTP client templates, base-address reference, unresolved with reason | 4.3 | `Adapters/HttpClients` | fixture 8 | pass: f08 (template from readonly field, base address, unresolved with reason) |
| R3.22 | gRPC: `.proto` roles, `grpc.service`, `grpc.call`, client address reference | 4.3 | `Adapters/Grpc` | fixture 9 | pass: f09 (real Grpc packages 2.84.0) |
| R3.23 | Messaging candidates, caller convention file as exact `configured-convention`, `suggested-conventions.yml` | 4.3 | `Adapters/Messaging`, `ConventionFile` | fixture 10 with and without conventions | pass: f10 without and with conventions; suggested-conventions.yml not asserted |
| R3.24 | Configuration keys with secret flag; values never stored | 4.3, 1.4.6 | `Adapters/Configuration` | fixture 11 + sentinel value grep over the whole output | pass: f11 (secret flags, no value in the generation) |
| R3.25 | Aspire resources and `WithReference` | 4.3 | `Adapters/Topology` | fixture 8 | pass: f08 with an Aspire stub (D20); real Aspire on eShop |
| R3.26 | `DbContext` and `DbSet<T>` as `data.maps_to` | 4.3 | `Adapters/Data` | fixture 6 or a dedicated EF fixture | implemented; checked on eShop only (9 data.maps_to), no fixture |
| R3.27 | Direct and transitive packages, central package management, used external API per package | 4.4 | `Adapters/Packages` | fixture 2 (CPM) | partial: packages and used package API in project records; no fixture asserts them |
| R3.28 | Canonical NDJSON, redacted snippets, diagnostics | 4.2.10 | `Contracts/CanonicalJson`, `Analysis/SnippetRedactor` | `CanonicalJsonTests`, `RedactionTests` | pending |
| R3.29 | `SymbolFinder` only as test cross-check | 4.2 | tests only | `NoSymbolFinderInProductTest` (reflection over product assemblies) | pending |
| R3.30 | No knowledge of analysed repositories in the engine | 1.4.2 | | `NoRepositoryKnowledgeTest`: names from `tests/eshop/ground-truth.yml` absent from `src/` | pending |
| R3.31 | Processes started with argument arrays only | 1.4.8 | `Analysis/ProcessRunner` | fixture with shell metacharacters in paths; analyzer rule banning `ProcessStartInfo.Arguments` | pending |
| R3.32 | Analysed repository read-only | 1.4.7 | `/src` ro mount, `WorkingCopyExporter` | `MountContractTest` (R1.6) + hash of `/src` before and after | pending |
| R3.33 | Fixtures 1 to 15, own `global.json` (≥1 SDK 9, ≥1 SDK 10), fixed Git dates, `expected.yml` with evidence; full precision and recall | 4.5 | `tests/fixtures/` | `FixtureTests` | pass for SDK 10: fixtures 1 to 15 with hand-written expected.yml and set comparison in both directions; SDK 9 fixture deferred (D15) |
| G3.1 | All fixtures pass in the image on both architectures | Gate 3 | | CI matrix | pass: test stage on amd64 and arm64 in CI (see report) |
| G3.2 | Partial types are one entity with every declaration; generated parts carry their generator | Gate 3 | | fixture 1 | pass: f01 |
| G3.3 | Two runs per fixture byte-identical, also across amd64 and arm64 | Gate 3 | | `DeterminismTests` + CI comparison | pass: verify.sh two runs per fixture; CI byte comparison amd64 vs arm64 |
| G3.4 | Failures and unsupported cases reported with reasons, never stop the run | Gate 3 | | fixtures 13 to 15 in one combined repository | pass: f12 to f15 report and continue |
| G3.5 | `docs/reports/step-3-extractor.md` | Gate 3 | | | docs/reports/step-3-fixtures.md and docs/reports/step-3-graph.md |

### Step 4: artifact builder

| Id | Requirement | Section | Planned code | Planned test / check | Result |
|---|---|---|---|---|---|
| R4.1 | Link rules for HTTP, gRPC, messaging, DI; evidence of both sides plus configuration or topology | 5.1 | `Artifacts/Linker` | fixtures 6 to 10 `expected.yml` | pass: fixtures 6 to 10 links match expected.yml (FixtureTests); every link carries the evidence of both sides (FixtureComparer check on all 24 fixtures) |
| R4.2 | Ambiguous keeps all matches; unresolved has a reason; weakest link | 5.1 | `Linker`, `QueryEngine.Trace` | `LinkerTests` | pass: ArtifactRuleTests (ambiguous keeps all targets, unresolved with reason, weakest part, fan-out D18); `trace` reports the weakest link |
| R4.3 | `index.sqlite` with Microsoft.Data.Sqlite; FTS5 checked at startup | 5.2 | `Artifacts/IndexWriter` | `Fts5StartupTest` | pass: Microsoft.Data.Sqlite 10.0.14, SQLite 3.53.3 with FTS5; checked before analyze starts and in `version --json` (Bundled_sqlite_supports_fts5) |
| R4.4 | Tables mirror records; indexes in both directions | 5.2 | `IndexWriter` | `IndexSchemaTests` (`EXPLAIN QUERY PLAN` uses the indexes) | pass: Lookups_in_both_directions_use_an_index (EXPLAIN QUERY PLAN for 9 lookups) |
| R4.5 | FTS5 over names, signatures, doc comments, snippets | 5.2 | `IndexWriter` | `SearchTests` | pass: Search_finds_names_snippets_and_observations_with_locations; no documentation comments in the records, so none are indexed |
| R4.6 | Index schema in CONTRACT.md, versioned | 5.2 | docs | doc test compares documented and actual DDL | pass: docs/CONTRACT.md `index.sqlite`, index schema 1; Documented_index_schema_matches_the_writer |
| R4.7 | Canonical index dump identical across runs and architectures | 5.2 | `Artifacts/IndexDump` | `DeterminismTests` + CI | pass: dump and file identical across runs (unit test, verify.sh, eShopOnAbp twice) and across amd64 and arm64 (CI run 41023416377) |
| R4.8 | Repository overview pack | 5.3 | `Artifacts/ContextPackWriter` | golden files per fixture | pass: context/overview.md with services, service map, contracts, gaps (ArtifactRuleTests; eShopOnAbp, lab-leasing) |
| R4.9 | Service cards | 5.3 | `ContextPackWriter` | golden files | pass: context/projects/*.md service cards (endpoints, outgoing calls, messages, dispatch, DI, data, configuration keys, packages, gaps) |
| R4.10 | Contract cards | 5.3 | `ContextPackWriter` | golden files | pass: context/contracts/*.md per message contract and gRPC service |
| R4.11 | Evidence on every statement; "what could not be determined"; truncation notice with count and how to get the rest | 5.3 | `ContextPackWriter` | `CardRulesTests` | pass: Cards_cite_evidence_end_with_gaps_and_say_what_was_cut |
| R4.12 | Wiki: index, service and contract pages, Mermaid, legend, commit, engine version, queries | 5.4 | `Artifacts/WikiWriter` | golden files + Mermaid syntax check in a container | pass: wiki/ index, services, contracts with Mermaid and legend (Wiki_pages_record_commit_engine_queries_and_draw_resolution); 59 diagrams rendered by mermaid-cli 11.4.2 in a container (report) |
| R4.13 | `verify`: checksums, schema, integrity; exit 0 or 50 | 5.5 | `Cli/VerifyCommand` | tampered copy test | pass: verify on every fixture generation (FixtureTests), exit 50 on a tampered copy (verify.sh, verify.ps1); verify also checks index.sqlite against the graph |
| R4.14 | `query` commands: `symbol`, `refs`, `callers`, `callees`, `impl`, `di`, `endpoints`, `calls-to`, `messages`, `trace`, `search` | 5.5 | `Artifacts/QueryEngine`, `Cli/QueryCommand` | one test per command on fixtures | pass with changes: symbol, refs, callers, callees, impl, di, routes (alias endpoints), http, calls-to, messages, trace, search, dump; verbs and JSON rows shared with angular-intelligence (D25); fixture questions in expected.yml |
| R4.15 | Text by default, `--json`; distinct no results, not analysed, partial, unsupported; exit 3 for ambiguous `symbol` | 5.5 | `QueryCommand` | `QueryStateTests` | pass: text default, --json; exit 3 for ambiguous names, 4 no results with a coverage note in partial generations (IndexAndQueryTests) |
| R4.16 | Finalization order: validate, checksums, manifest, `run.json`; Action outputs from the manifest | 5.6 | `Artifacts/GenerationFinalizer`, `GitHubActionMode` | `FinalizerOrderTests` (file write order observed) | pass: index, packs and wiki derived from the written NDJSON, then validation, manifest, run.json (Run_json_is_written_after_the_manifest_and_nothing_after_it); Action outputs read from manifest.json |
| G4.1 | Links on fixtures 7 to 10 match `expected.yml` with evidence on both sides | Gate 4 | | R4.1 | pass: f06 to f10 links and evidence of both sides (FixtureTests) |
| G4.2 | Context packs and wiki byte-identical across runs and architectures; index dump identical | Gate 4 | | R4.7, CI | pass: CI run 41023416377, byte comparison amd64 vs arm64 of every fixture generation (index.sqlite, context, wiki) and index dump |
| G4.3 | `verify` passes on every fixture generation, fails on a tampered copy | Gate 4 | | R4.13 | pass: FixtureTests (verify per fixture), verify.sh tampered copy |
| G4.4 | `query` answers fixture questions; exit 3 with candidates for an ambiguous name | Gate 4 | | R4.14, R4.15 | pass: queries in expected.yml of f02, f03, f05 to f10; f02 and f03 exit 3 with candidates |
| G4.5 | `docs/reports/step-4-artifacts.md` | Gate 4 | | | pass: docs/reports/step-4-artifacts.md |

### Step 5: eShop validation

| Id | Requirement | Section | Planned code | Planned test / check | Result |
|---|---|---|---|---|---|
| R5.1 | eShop main pinned; .NET 10 confirmed from `global.json` | 6.1 | `tests/eshop/pin.yml` | re-run of A.6 at Step 5 start; if main moved, the plan's pin is kept and the new head is recorded as the "later commit" for case 17 | pending |
| R5.2 | Last .NET 9 commit validated | 6.1 | `pin.yml` | A.6 identifies `94e2643dc73b`; validation runs in slice 9c | deferred (D15) |
| R5.3 | Ground truth from source only, committed before the first engine run, commit hash recorded | 6.2 | `tests/eshop/ground-truth.yml` | Git history: ground-truth commit precedes the first validation workflow run | pending |
| R5.4 | Ground-truth changes logged with reason and evidence | 6.2 | `tests/eshop/GROUND-TRUTH-LOG.md` | review | pending |
| R5.5 | Convention file written from source, supplied through `/config` | 6.2 | `tests/eshop/conventions.yml` | R3.30 still passes | pending |
| R5.6 | Workflow: build both architectures, shallow checkout at pin, Action without and with conventions, evaluator, warm-cache run, upload | 6.3 | `.github/workflows/eshop-validation.yml`, `tests/eshop/Evaluator` | workflow run link | pending |
| R5.7 | Same image on a non-Linux host, graph compared byte for byte | 6.3 | `scripts/compare-generation.sh` (runs in a container) | macOS host run (A.1) | pending |
| R5.8 to R5.24 | Test cases 1 to 17 (coverage, partial types, Razor, references, overloads, implementations, DI, endpoints, HTTP links, gRPC, messaging, topology, traces, secrets, packages, determinism, robustness) | 6.4 | evaluator areas, one per case | evaluator scores per area, every miss listed | pending |
| R5.25 | Measurements: precision and recall, coverage, abstention, times per stage, cold vs warm, memory, sizes, query latency | 6.5 | `run.json`, evaluator | report tables | pending |
| R5.26 | Usefulness kit: 10 questions, instructions, protocol (not run) | 6.6 | `tests/eshop/usefulness-kit/` | review | pending |
| R5.27 | Final report with the 8 sections | 6.7 | `docs/reports/step-5-eshop.md` | review | pending |
| G5.1 | Every area in 6.4 measured, bound to the pin, produced by the workflow | Gate 5 | | R5.8 to R5.24 | pending |
| G5.2 | Both traces reproduce from the listed query commands | Gate 5 | | commands re-run from the report | pending |
| G5.3 | Determinism across runs and architectures (and hosts, or reported as not validated) | Gate 5 | | R5.7, case 16 | pending |
| G5.4 | Usefulness kit ready | Gate 5 | | R5.26 | pending |
| G5.5 | Report written | Gate 5 | | R5.27 | pending |

## 6. Decisions needing review

Recorded in DECISIONS.md; these change what gets built, so they are flagged for the Step 0 review:

- **D4** SDK selection per solution (depends on spike S1).
- **D6** symbols without a documentation comment id are not entities in v1.
- **D8** working copy built from Git objects instead of `git archive`.
- **D9** columns counted in UTF-16 code units.
- **D10** native runners per architecture instead of QEMU.
- **D15** first image with SDK 10 only; SDK 9 deferred (owner's decision, deviation from 2.2).

## Appendix A: recorded facts

All commands ran on 2026-10-29 on the development host (macOS, Docker Desktop).

### A.1 Host

```bash
docker version --format '{{.Client.Version}} / {{.Server.Version}} {{.Server.Os}}/{{.Server.Arch}}'
docker buildx ls
```

```text
29.7.2 / 29.7.2 linux/arm64
examini-local        docker-container   running   v0.33.1    linux/amd64 (+2), linux/arm64, ...
desktop-linux*       docker             running   v0.32.2    linux/amd64 (+2), linux/arm64, ...
```

### A.2 .NET releases

```bash
curl -s https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json
```

```text
11.0 11.0.100-rc.3.26522.148 11.0.0-rc.3.26522.148 go-live sts
10.0 10.0.403 10.0.14 active lts
9.0 9.0.320 9.0.22 maintenance sts
```

### A.3 Base image digests

```bash
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0.403-noble --format '{{json .Manifest.Digest}}'
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:9.0.320-noble --format '{{json .Manifest.Digest}}'
```

```text
sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
sha256:9a905addac4d4d3bfdd28185030f874251614559b3a038200849619710b3136b
```

### A.4 Roslyn and related packages

```bash
curl -s https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.csharp.workspaces/index.json
curl -s https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.workspaces.msbuild/5.9.0/microsoft.codeanalysis.workspaces.msbuild.nuspec
curl -s https://api.nuget.org/v3-flatcontainer/sqlitepclraw.bundle_e_sqlite3/index.json
```

```text
latest versions: ... '5.0.0', '5.3.0-2.final', '5.3.0', '5.6.0', '5.9.0'
Workspaces.MSBuild 5.9.0: license MIT; dependency groups .NETFramework4.7.2 and net10.0
SQLitePCLRaw.bundle_e_sqlite3 latest: 3.0.5
```

### A.5 SDK, runtimes and compiler in the pinned .NET 10 image

The SDK 9 image was not probed (D15); its probe was stopped before it ran.

```bash
docker run --rm --platform linux/amd64 --network none \
  mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b \
  sh -c 'uname -m; dotnet --list-sdks; dotnet --list-runtimes; for s in /usr/share/dotnet/sdk/*/; do echo "csc $(basename $s): $(dotnet $s/Roslyn/bincore/csc.dll -version)"; done; git --version; id app 2>&1; du -sh /usr/share/dotnet'
# same command with --platform linux/arm64
```

```text
linux/amd64:
x86_64
10.0.403 [/usr/share/dotnet/sdk]
Microsoft.AspNetCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
Microsoft.NETCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
csc 10.0.403: 5.9.0-1.26523.112 (5a2162bea6845eabc1fc721624a43568c65db3b1)
git version 2.43.0
uid=1654(app) gid=1654(app) groups=1654(app)
624M	/usr/share/dotnet

linux/arm64:
aarch64
10.0.403 [/usr/share/dotnet/sdk]
Microsoft.AspNetCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
Microsoft.NETCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
csc 10.0.403: 5.9.0-1.26523.112 (5a2162bea6845eabc1fc721624a43568c65db3b1)
git version 2.43.0
uid=1654(app) gid=1654(app) groups=1654(app)
655M	/usr/share/dotnet
```

`docker image ls --digests mcr.microsoft.com/dotnet/sdk` reported 1.47GB for the pulled image.

### A.6 eShop

```bash
gh api repos/dotnet/eShop/commits/main --jq '.sha + " " + .commit.committer.date'
gh api repos/dotnet/eShop/contents/global.json --jq .content | base64 -d
gh api 'repos/dotnet/eShop/commits?path=global.json&per_page=30'
gh api repos/dotnet/eShop/commits/318e03744f --jq '.parents[0].sha'
gh api 'repos/dotnet/eShop/contents/global.json?ref=94e2643dc73b0be47cb956bd79af9863b87df845'
gh api 'repos/dotnet/eShop/git/trees/913acea66708a59595351174cee9b29f37179d29?recursive=1'
```

```text
913acea66708a59595351174cee9b29f37179d29 2026-10-15T20:52:28Z
global.json @ main: sdk.version 10.0.302, rollForward latestFeature, allowPrerelease false; msbuild-sdks MSTest.Sdk 4.0.2
global.json history (newest first):
  ae71a06101 2026-08-18 Upgrade Aspire and .NET 10 (#1008)
  b832741187 2025-12-03 Use latest xUnit, MSTest + move to MTP (#924)
  318e03744f 2025-09-26 Upgrading eshop to .NET 10 and Aspire 9.4.2 (#890)
  e9ef88f1cd 2025-02-26 Upgrade to .NET Aspire 9.1 (#759)
  5cdcb97949 2024-11-13 Update to .NET 9 GA (#614)
parent of 318e03744f: 94e2643dc73b0be47cb956bd79af9863b87df845 (2025-09-18, "Inventory drift (#896)")
global.json @ 94e2643: sdk.version 9.0.200, rollForward latestFeature, allowPrerelease true
solutions @ main: eShop.slnx, src/ClientApp/ClientApp.sln, tests/ClientApp.UnitTests/ClientApp.UnitTests.sln
projects @ main: 28 C# projects, 19 under src/ and 9 under tests/ (ClientApp and HybridApp are MAUI); corrected in Step 1, the Step 0 text said 21 and 10
```

### A.7 GitHub-hosted arm64 runners

Source: GitHub changelog, "arm64 standard runners are now available in private repositories" (2026-01-29) and "arm64 hosted runners for public repositories are now generally available" (2025-08-07): `ubuntu-24.04-arm` is free on public repositories (4 vCPU).
