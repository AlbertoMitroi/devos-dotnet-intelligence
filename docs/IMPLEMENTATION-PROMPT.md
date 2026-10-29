# dotnet-intelligence: Context Engine Implementation Prompt (Docker, GitHub Action, eShop)

29 oct. 2026 · @Ghitulescu Stefan

> Imported from the shared document as Markdown. Tables that lost their layout when copied were rebuilt with the same cells. The only textual change: em dashes in headings were replaced with colons (see docs/DECISIONS.md, D14).

## How to use this prompt

This prompt creates a new, independent repository (working name `dotnet-intelligence`): a context engine that takes a .NET repository at a commit and produces context artifacts, nothing else. It knows nothing about DevOS, Jira or any consumer. It is designed from the first line as a CI step: one Docker image and one GitHub Action, the same everywhere.

### What the machine needs

- Docker only (or a compatible engine such as Podman). No .NET SDK, Node or other toolchain on the host. Building, testing and running the engine all happen inside containers.
- Git, to clone the new repository.
- A GitHub repository for the engine, with GitHub Actions and GitHub Container Registry (GHCR) available.

### Setup

1. Create an empty repository `dotnet-intelligence`. Export this doc as Markdown and commit it as `docs/IMPLEMENTATION-PROMPT.md`.
2. Start Claude Code in that repository and paste the kickoff message.

```text
Read docs/IMPLEMENTATION-PROMPT.md completely. Implement it step by step, starting with Step 0.
Use only Docker for building, testing and running; never install SDKs on the host.
Stop at every gate marked STOP and report. Never invent results: every claim must come
from a command you ran, and the command is recorded in the report.
```

### Scope

**In:** the artifact contract, the .NET extractor, the artifact builder (index, context packs, wiki, verification, a read-only query command for validation), the multi-architecture Docker image, the GitHub Action, the engine's own CI and release workflow, and full validation on dotnet/eShop run through the Action.

**Out:** any consumer (servers, DevOS, Copilot integration, UIs), publishing artifacts to a registry on behalf of users (the calling workflow decides that), Angular, embeddings, LLM-written text, cross-repository linking.

## Part 1: Role, mission and rules

### 1.1 Mission

You are the senior engineer building dotnet-intelligence, an independent context engine for .NET. Its single job:

> Given a .NET repository at one commit, produce a complete, verifiable, deterministic set of context artifacts (a generation): a semantic graph with evidence, a queryable index, context packs and a wiki.

The quality bar is IDE-grade semantic navigation (references and usages by kind, callers, implementations, partial types, dependency injection), persisted as artifacts, plus relationships between services inside the repository, each backed by evidence. Consumers (servers, IDE tools, AI assistants) are separate products that read the artifacts through the published contract.

### 1.2 Decisions already taken (do not reopen)

1. **Docker-first.** The engine is a Linux container image. Development, tests, releases and every validation run in containers. The host needs only Docker.
2. **CI-native.** The primary entry point is a GitHub Action wrapping the image. The same image runs with `docker run` in any CI or on any machine.
3. **OS-agnostic output.** The same image + commit + configuration produces byte-identical graph files on any host OS and CPU architecture.
4. **Artifacts only.** The engine does not publish, upload, serve or notify. The calling workflow decides what to do with the output directory.
5. **One runtime.** The engine is written in .NET (C#), including the index writer, so the image contains one application runtime plus the .NET SDKs it needs to evaluate analyzed projects.
6. **Roslyn is the semantic core.** When a project cannot be loaded semantically, the fallback is Roslyn syntax analysis, reported as such. No tree-sitter.
7. **Not in this project:** MCP, external model APIs, embeddings, LLM-generated text, knowledge of any specific consumer.

### 1.3 Definition of complete

A step is complete only when:

1. It is implemented end to end, with no stubs, placeholders, TODO/FIXME or hard-coded results in the product path.
2. Every requirement in the step maps to an automated test or a recorded, reproducible check (traceability table: requirement → code → test → result).
3. All tests pass inside containers through the repository's single verify command, locally and in the engine's own CI.
4. Environment-dependent results (SDK set, Docker host, architecture, network) are validated with recorded versions or reported as not validated, with the reason.
5. README and docs match the actual behavior and limits.
6. The step's gate report is written in `docs/reports/` and you stop for review.

### 1.4 Non-negotiable rules

1. **No invented results.** Every number or pass/fail in a report comes from a recorded command.
2. **No built-in knowledge of analyzed repositories.** Never encode names, routes or event names of a specific repository (including eShop) in the engine. Repository-specific conventions come only from a configuration file supplied by the caller.
3. **Unknown is not empty.** "No results", "not analyzed", "unsupported", "failed" and "partial" are distinct in data, output and exit codes.
4. **Evidence on every relationship:** repository-relative path, Git blob id, line and column span, extractor and method, analysis context. No evidence, no relationship.
5. **Proven, possible and observed are different.** Declared call targets, possible runtime targets, framework dispatch and configured routes are distinct kinds. Runtime observation does not exist in this engine.
6. **Secrets stay out.** Configuration values are never stored; secret-looking content is redacted from snippets; credentials passed for restore never appear in outputs or logs.
7. **The analyzed repository is read-only.** The engine works on its own copy inside the container.
8. **Processes start with argument arrays.** Text from the analyzed repository is never interpolated into commands.
9. **Determinism.** Same image digest + commit + configuration → byte-identical graph files. No timestamps, random ids, host paths or OS-specific line endings in them.

### 1.5 Working method

1. **Step 0: Plan.** Write `docs/PLAN.md`: component plan, traceability tables for every step, and decisions you need. STOP for review.
2. Small vertical slices, build green after each, one commit per slice naming its step.
3. Decisions in `docs/DECISIONS.md` (context, options, choice, reason, how to revisit); questions you cannot answer in `docs/OPEN-QUESTIONS.md`, then continue with independent work.
4. Every dependency is pinned, with its license recorded. Base images are pinned by digest.

## Part 2: Step 1: Docker-first repository, image and GitHub Action

### 2.1 Repository layout

```text
dotnet-intelligence/
  action.yml                     Docker container action using the published image
  Dockerfile                     multi-stage: build, test, runtime
  scripts/verify.sh, verify.ps1  the only local entry points; both call Docker
  .devcontainer/                 optional: edit and debug inside a container
  global.json                    SDK used to BUILD the engine (inside the build stage)
  DotnetIntelligence.slnx
  schema/v1/*.schema.json        the artifact contract (Part 3)
  src/
    DotnetIntelligence.Contracts/   types generated from the schema
    DotnetIntelligence.Analysis/    loading, extraction, identity, evidence
    DotnetIntelligence.Adapters/    DI, ASP.NET Core, HTTP, gRPC, messaging, config, topology, data
    DotnetIntelligence.Artifacts/   index writer, context packs, wiki, verify, query
    DotnetIntelligence.Cli/         entry point: version, preflight, analyze, verify, query
  tests/unit/  tests/integration/  tests/fixtures/  tests/eshop/
  examples/workflows/            how callers use the Action (upload, OCI push, other CIs)
  docs/  PLAN.md  DECISIONS.md  OPEN-QUESTIONS.md  CONTRACT.md  USAGE.md  reports/
  .github/workflows/  ci.yml  release.yml  eshop-validation.yml
```

### 2.2 The image

- **Base:** an official .NET 10 SDK image pinned by digest, plus the .NET 9 SDK and runtime side by side. `dotnet --list-sdks` and `dotnet --list-runtimes` inside the image must show both. The SDK set is part of the image identity and is printed by `version --json`.
- **Roslyn rule, checked at build time:** the engine's Roslyn version is at least the compiler version of every SDK in the image. A test in the test stage fails the build otherwise. Because the image controls the SDKs, there is one host and no host selection.
- **Architectures:** linux/amd64 and linux/arm64, built with Buildx. The graph output must be identical on both.
- **Runtime contract:**

  | Mount | Mode | Content |
  |---|---|---|
  | `/src` | read-only | The repository to analyze (a Git checkout) |
  | `/out` | read-write | The generation directory |
  | `/cache` | read-write, optional | NuGet packages and engine cache (`NUGET_PACKAGES` points here) |
  | `/config` | read-only, optional | Caller configuration and convention files |

- Runs as a non-root user; the entry point is the CLI. Network is needed only for restore (and cloning in `--repo-url` mode); with a complete cache, `--offline` must work with `--network none`.
- **Private NuGet feeds:** credentials are passed as `NuGetPackageSourceCredentials_<source key>` environment variables (NuGet's documented mechanism, format `Username=...;Password=...`); they never appear in outputs or logs.

### 2.3 Inside the container

- Git sees `/src` as owned by another user; register it as a safe directory for the engine's own Git calls only.
- **Default: analyze the committed state.** Export HEAD from Git objects into a working copy under `/work` (for example `git archive`), so the bytes are the committed ones on every host OS. `--include-working-tree` copies the working tree instead and marks the generation dirty.
- Paths in artifacts are repository-relative with forward slashes. File identity is the Git blob id. Spans are line and column, never byte offsets.
- Shallow clones (CI default) must work. Uninitialized submodules are reported as diagnostics.

### 2.4 Local development with Docker only

- `scripts/verify.sh` and `scripts/verify.ps1` run the same steps: build the image's test stage (unit and integration tests), then run fixture analyses with the runtime image. No host SDK, Node or Python.
- The dev container is optional and only for editing comfort; nothing may require it.

### 2.5 The GitHub Action

- **Type:** Docker container action referencing the prebuilt, published image by version (no Dockerfile build per use). GitHub documents that Docker container actions run only on Linux runners; that is the CI host requirement. On other hosts and in other CIs the same image runs with `docker run`.
- **Inputs:** `path` (default: the workspace), `solution` (optional), `config` (optional file), `output` (default `intelligence-out`), `include-working-tree` (default `false`), `fail-on` (`failed`, `partial` or `never`).
- **Outputs:** `generation-path`, `manifest-path`, `status` (`complete`, `partial`, `failed`), `coverage` (compact JSON).
- The output and the NuGet cache live inside the workspace, so the caller can use `actions/upload-artifact` and `actions/cache`.
- `examples/workflows/`: checkout → Action → upload artifact; checkout → Action → push the generation as an OCI artifact (the caller's choice, not the engine's); the equivalent `docker run` for other CI systems.

### 2.6 The engine's own CI and release

- `ci.yml`: build both architectures, run the test stage, analyze the fixtures and compare the outputs of amd64 and arm64 byte for byte.
- `release.yml` on a version tag: push the image to GHCR with SBOM and provenance attestations, then update `action.yml` to the released version; publish the schema with the release.
- **Versioning:** engine semver; the contract has its own `schemaVersion`; the Action has a major tag (`v1`).

### Gate 1: STOP when

- [ ] The image builds for both architectures; the self-test shows SDK 9 and 10, matching runtimes and a compatible Roslyn.
- [ ] `verify.sh` and `verify.ps1` pass on a host with only Docker.
- [ ] A skeleton analyze run through the Action on a fixture repository produces a manifest and the declared outputs in CI.
- [ ] Report written: `docs/reports/step-1-infrastructure.md`.

## Part 3: Step 2: the artifact contract v1

**Goal:** a published, technology-neutral format for a generation. .NET detail travels as namespaced extensions, so other engines (for example Angular) can emit the same contract later and consumers never depend on engine internals.

### 3.1 Generation layout

```text
<output>/
  manifest.json          written LAST; lists every file with sha256
  graph/
    projects.ndjson      projects and analysis contexts
    entities.ndjson      logical symbols
    declarations.ndjson  where each entity is declared (N per partial type)
    relations.ndjson     typed edges
    evidence.ndjson      path, blob id, span, extractor, method, context
    observations.ndjson  normalized facts: endpoints, calls, messages, DI, config keys, topology
    links.ndjson         relationships between services inside the repository (Part 5)
    diagnostics.ndjson   failed, partial, unsupported or unknown, with reasons
  snippets.ndjson        redacted source excerpts keyed by blob id and span
  index.sqlite           queryable index (Part 5)
  context/  wiki/        deterministic projections (Part 5)
  run.json               timings, host, image digest; excluded from determinism checks
```

### 3.2 Records (minimum fields)

| Record | Required content |
|---|---|
| Manifest | `schemaVersion` (major.minor), `requiredCapabilities[]`, engine version, image digest, Roslyn version, SDK set, repository identity (remote URL if known), commit, dirty, configuration hash, analysis contexts, coverage per project, file list with sha256 |
| Project | Stable id, repository-relative path, assembly name, target frameworks, resolved SDK, project and package references |
| Analysis context | Id, project, TFM, configuration, preprocessor symbols, load mode (`semantic`, `syntax`, `failed`), reason |
| Entity | Stable logical id, kind, name, container, signature, accessibility, modifiers, origin (`source`, `generated`, `metadata`) |
| Declaration | Entity, analysis context, path, blob id, span, generator name when generated |
| Relation | Id, kind, from, to, analysis context, resolution (`exact`, `ambiguous`, `candidate`, `unresolved`), evidence ids, extensions |
| Evidence | Path, blob id, span (line and column), extractor `name@version`, method (`semantic`, `syntax`, `metadata`, `config`, `configured-convention`), analysis context |
| Observation | Normalized kind and fields (3.4), evidence ids, extensions |
| Link | Kind, both sides, resolution, all evidence used (both sides plus configuration or topology) |
| Diagnostic | Severity, code, scope, state (`failed`, `partial`, `unsupported`, `unknown`), message |

### 3.3 Identity

- **Logical entity:** `dotnet:{projectPath}:{documentationCommentId}`. The project path disambiguates identical names in different assemblies. Package symbols use `dotnet:pkg:{packageId}@{version}:{assemblyName}:{documentationCommentId}`. Symbols without a usable documentation comment id (local functions, lambdas) are out of scope or get a container-relative id; record the choice.
- **Observation per context:** relations and declarations carry their analysis context; the same method in two TFMs keeps separate observations.
- **Evidence location:** path + blob id + span.
- Ids contain no absolute paths, no host-specific data and no random parts.

### 3.4 Kinds (v1)

- **Code:** `declares`, `contains`, `references` (usage: `read`, `write`, `invocation`, `nameof`, `typeof`, `attribute`, `other`), `calls_declared_target`, `overrides`, `implements`, `inherits`, `possible_runtime_target`.
- **Application:** `di.registers` (service, implementation or factory, lifetime, key, conditional, registration path), `di.consumes`.
- **Observations:** `http.endpoint` (method, canonical route template, project), `http.call` (method, route or URL template, base-address reference), `grpc.service`, `grpc.call`, `messaging.publish`, `messaging.subscribe` (contract identity, destination and transport if known), `config.key` (key, file, secret flag; never the value), `topology.resource`, `topology.reference`, `data.maps_to`.
- **Links:** `consumes_contract`, `configured_route`, `possible_communication`. There is no "observed" kind.
- Framework detail goes into `ext.dotnet.*`; normalized fields carry everything a consumer needs for linking.

### 3.5 Versioning and compatibility

- Consumers accept the same major and any minor. Minors are strictly additive and keep existing meaning; anything a consumer must understand is listed in `requiredCapabilities`.
- Rules published in `docs/CONTRACT.md`: an unknown required capability → refuse; unknown fields and extensions → preserve without interpreting; unknown kinds → keep but exclude from traversal and linking, and report them.

### 3.6 Validation

- The C# types are generated from the schema; the build fails if regeneration produces a diff.
- Before writing the manifest, the engine validates its own output against the schema and runs the integrity checks: every referenced id exists, no duplicate ids, every evidence references a file present at that blob id, every context exists, checksums match. A failed check means no manifest and exit code `failed`.
- `verify <generation>` repeats these checks for any consumer.

### 3.7 Fixtures and gate

`tests/contract/`: one minimal valid generation; invalid ones (dangling reference, duplicate id, checksum mismatch, unknown required capability); one from a newer minor with extra fields and an unknown kind.

### Gate 2: STOP when

- [ ] Schema, generated types, `CONTRACT.md` and fixtures exist.
- [ ] `verify` accepts the valid fixtures and rejects each invalid one with a readable reason.
- [ ] Report written: `docs/reports/step-2-contract.md`.

## Part 4: Step 3: the .NET extractor

**Goal:** compiler-accurate extraction from any SDK-style .NET 9 or .NET 10 repository, with every limitation reported instead of hidden.

### 4.1 Commands and protocol

| Command | Purpose |
|---|---|
| `version --json` | Engine, Roslyn, runtime, SDK set, image digest if known |
| `preflight --src /src --json` | What can be determined before analysis (4.2, step 3) |
| `analyze --src /src --out /out [--config /config/x.yml] [--solution path] [--include-working-tree] [--offline]` | Full generation |
| `analyze --repo-url <url> --ref <commit or branch> --out /out` | Same, cloning inside the container (for runs outside CI) |

- Progress and diagnostics are NDJSON events on stdout; logs go to stderr. Inside GitHub Actions, important diagnostics are also emitted as workflow annotations.
- **Exit codes, documented and tested:** `0` complete; `10` partial; `20` preflight blocked (for example an SDK the image does not contain); `30` nothing could be loaded; `40` cancelled; `50` integrity check failed; `1` internal error.
- SIGTERM triggers a graceful stop; partial output is never presented as a generation (no manifest).

### 4.2 Stages

1. **Working copy:** export the commit to `/work` (Part 2.3); record commit, remote URL if present and dirty.
2. **Discovery:** `*.slnx`, `*.sln` and `*.slnf` if the loader supports it, plus their projects. A project included in several solutions is analyzed once per analysis context. Default context: one primary TFM per project; others through configuration.
3. **Preflight:**
   - Resolve each solution's SDK from its `global.json` with the SDKs in the image. An unsatisfiable `global.json` is reported as `unsupported-sdk` and blocks only that solution.
   - Read the compiler version of the resolved SDK.
   - Unknowns stay unknown.
4. **Restore** into `/cache` (`NUGET_PACKAGES`). A project that fails restore, for example because of a missing workload, is reported and the run continues.
5. **Post-restore compatibility check:** from `project.assets.json`, list analyzers and source generators and the `Microsoft.CodeAnalysis` version each one references; flag any newer than the engine's Roslyn.
6. **Load and validate with MSBuildWorkspace:**
   - collect `WorkspaceFailed` diagnostics;
   - per project, flag analyzers or generators that cannot be loaded (CS8032) and projects that reference generators but produce zero generated documents;
   - record compilation error counts (they may belong to the project, not the loader);
   - assign each project a load mode: `semantic`, `syntax` or `failed`.
7. **Extract,** one pass per document, with the semantic model and IOperation:
   - entities and declarations, including every part of a partial type (`DeclaringSyntaxReferences`), partial member definition and implementation parts, and generated documents with the generator's name;
   - references with usage kind;
   - `calls_declared_target`, including constructors and extension methods resolved to their real target;
   - inheritance, implementations and overrides;
   - after the pass, `possible_runtime_target` from interface or virtual calls to known implementations.
8. **Syntax fallback** for projects not loaded semantically: Roslyn parser only, declarations and containment, labelled with method `syntax`.
9. **Adapters** (4.3), packages (4.4), observations (contract 3.4).
10. **Write** canonical NDJSON (sorted records, sorted keys, stable ids), redacted snippets and diagnostics. Hand over to the artifact builder (Part 5), which validates and writes the manifest last.

`SymbolFinder.FindReferencesAsync` is not used to build the graph (too slow at scale); tests use it as a regression cross-check.

### 4.3 Adapters (generic only)

Adapters recognize framework calls by resolved symbol (containing type and method), never by text, and never by names belonging to a specific repository.

- **Dependency injection:**
  - Registrations: `Add*`, `TryAdd*` and `AddKeyed*` (generic, `typeof` and factory forms; for factories, the lambda's return type), `AddHostedService`, `AddHttpClient<TClient, TImplementation>`.
  - User extension methods on `IServiceCollection` or the host builder are followed across projects, and the registration path is recorded. Registrations inside conditionals are marked conditional.
  - Consumers: constructor and primary-constructor parameters, `[FromServices]`, `[FromKeyedServices]`, explicit `GetService` and `GetRequiredService`.
- **ASP.NET Core:** controllers (attribute and conventional routes) and minimal APIs (`Map*`, `MapGroup` prefixes, constants folded by the semantic model) become `http.endpoint` with the canonical route template from the contract. API versioning goes into extensions.
- **HTTP clients:** `HttpClient` calls with constant or interpolated URLs become templates. A typed-client base address becomes a base-address reference. Anything unresolvable is unresolved, with the reason.
- **gRPC:**
  - `.proto` files with their server and client roles;
  - service implementations (overrides of generated bases) become `grpc.service`;
  - generated client usage becomes `grpc.call`;
  - client registrations keep their address reference.
- **Messaging:**
  - Generic heuristics produce candidates: generic handler interfaces implemented by classes, generic subscription registrations, and publish calls whose argument type implements a common event base type.
  - A convention file supplied by the caller (`/config`) can declare a codebase's own publish method, subscription registration and handler interface by symbol. Its matches are exact with method `configured-convention`.
  - The engine writes `suggested-conventions.yml` from the candidates, for a human to confirm.
- **Configuration:** keys from `appsettings*.json` and similar, with their file and a secret flag (by key name and value shape). Values are never stored.
- **Topology:** Aspire AppHost resources and `WithReference` relationships.
- **Data (minimal):** `DbContext` and its `DbSet<T>` mappings as `data.maps_to`. No read or write claims.

### 4.4 Packages

Direct and transitive references from `project.assets.json`, including central package management, and the external API actually used (references to metadata symbols grouped by package and version). No source download and no decompilation.

### 4.5 Fixtures and tests

Each fixture under `tests/fixtures/` is a tiny repository with its own `global.json`, at least one pinned to SDK 9 and one to SDK 10. Tests turn each fixture into a Git repository with fixed author and committer dates, so commit ids are stable.

The fixtures:

1. A partial type across three files plus a generated part.
2. Overloads and extension methods.
3. The same type name in two projects.
4. Multi-targeting with `#if`.
5. Interface dispatch with two implementations.
6. Every DI form in 4.3, including registration through an extension method in another project.
7. Controllers and minimal APIs with groups.
8. A typed HttpClient calling an endpoint in another project through an Aspire AppHost reference.
9. A gRPC service and client.
10. Messaging through a custom abstraction, with and without a convention file.
11. Configuration containing secrets.
12. A source generator.
13. A project that fails restore.
14. A project needing a missing workload.
15. A `global.json` asking for an SDK the image does not have.

Each fixture has a hand-written `expected.yml` with file and line evidence. Tests require full precision and recall on fixtures, check exit codes and load modes, and cross-check a sample of references with `SymbolFinder`.

### Gate 3: STOP when

- [ ] All fixtures pass in the image, on both architectures.
- [ ] Partial types are one entity with every declaration; generated parts carry their generator.
- [ ] Two runs per fixture are byte-identical, including across amd64 and arm64.
- [ ] Failures and unsupported cases are reported with reasons and never stop the whole run.
- [ ] Report written: `docs/reports/step-3-extractor.md`.

## Part 5: Step 4: the artifact builder

**Goal:** turn the extracted graph into artifacts that consumers can use immediately, without running any analysis themselves.

### 5.1 Linking inside the repository

Rules read only normalized observation fields (contract 3.4). Each link stores the evidence of both sides plus any configuration or topology evidence used.

| Link | Rule (v1) |
|---|---|
| `http.call` → `http.endpoint` | Method + canonical route template; base-address reference resolved through `topology.reference` to the project hosting the endpoint |
| `grpc.call` → `grpc.service` | Fully qualified proto service and method |
| `messaging.publish` → `messaging.subscribe` | Contract identity; destination and transport when both sides know them |
| `di.consumes` → `di.registers` | Same service type within the same composition root |

Several matches → all kept as `ambiguous`; none → `unresolved`, with the reason. A path is only as strong as its weakest link.

### 5.2 index.sqlite

- Written by the engine in .NET (`Microsoft.Data.Sqlite`). At startup, verify that the bundled SQLite supports FTS5.
- Tables mirror the contract records, with indexes for both directions: entity → usages, location → entity, callers and callees, link endpoints.
- FTS5 over names, signatures, documentation comments and snippets.
- The index schema is documented in `CONTRACT.md` and versioned with the contract. It is derived data: the NDJSON graph remains the source of truth.
- **Determinism:** SQLite files are not required to be byte-identical. A canonical dump of the index (sorted rows) must be identical across runs and architectures.

### 5.3 Context packs (deterministic Markdown)

- **Repository overview:** solutions, projects with load mode, services, the service map, gaps.
- **Service card** per project: kind, entry points, endpoints, outgoing HTTP and gRPC calls, messages published and consumed, DI summary, packages, configuration keys (no values), coverage and gaps.
- **Contract card** per message or gRPC contract: publishers, subscribers, callers, resolution.
- Every statement cites evidence (path and line). Every card ends with "what could not be determined".
- Cards are sized for AI use; when a list is cut, the card says how many items were omitted and how to get them (query command or index).

### 5.4 Wiki (deterministic)

An index page plus one page per service and per contract, with Mermaid diagrams generated from the graph (service map, message flows) and a legend for resolution. Each page records the commit, the engine version and the queries that built it. No LLM text.

### 5.5 verify and query

- `verify <generation>`: checksums, schema and integrity checks (contract 3.6); exit code `0` or `50`.
- `query <generation> <command>`: a small read-only command set over `index.sqlite`, used to validate and demonstrate the artifacts, not as a consumer product:

  | Command | Answers |
  |---|---|
  | `symbol <name or id>` | Exact match or a list of candidates (exit code 3, never picks one) |
  | `refs <id> [--usage ...]` | Usages with path, line and the code line inline |
  | `callers`, `callees <id> [--depth N]` | Bounded call graph |
  | `impl <id>` | Implementations and overrides |
  | `di <service type>` | Registrations (lifetime, conditional, path) and consumers |
  | `endpoints`, `calls-to <endpoint>` | Endpoints and their callers across services |
  | `messages <contract>` | Publishers and subscribers |
  | `trace <from> [--to <to>]` | Bounded path across code and services, with the weakest link |
  | `search <text>` | FTS5 search with filters |

- Output is concise text by default, `--json` on request. "No results" differs from "not analyzed", "partial" and "unsupported".

### 5.6 Finalization

After all artifacts exist: validate (3.6), write checksums, then `manifest.json` last, then `run.json`. The Action sets its outputs from the manifest.

### Gate 4: STOP when

- [ ] Links on fixtures 7–10 match `expected.yml`, with evidence on both sides.
- [ ] Context packs and wiki are byte-identical across two runs and both architectures; the index dump is identical.
- [ ] `verify` passes on every fixture generation and fails on a tampered copy.
- [ ] `query` answers the fixture questions and returns candidates with exit code 3 for an ambiguous name.
- [ ] Report written: `docs/reports/step-4-artifacts.md`.

## Part 6: Step 5: validation on dotnet/eShop

**Goal:** prove with measured numbers, in the same conditions as a real main CI run, what the engine gets right on a real multi-service .NET application and what it reports as unknown.

### 6.1 Pin the target

- Record the current main commit of https://github.com/dotnet/eShop; every result is bound to it. eShop's README says it is based on .NET 10; confirm from its `global.json`.
- For .NET 9: search eShop's `global.json` history for the last commit pinned to a .NET 9 SDK and validate it too. If none exists, say so and rely on the SDK 9 fixtures.

### 6.2 Ground truth first

1. Build `tests/eshop/ground-truth.yml` by reading eShop's source code only (no README architecture sections, no external wikis, no prior knowledge). Each item: question, expected answer, file:line evidence.
2. Commit it and record that commit hash before the first engine run on eShop.
3. Any later change to the ground truth is logged with reason and evidence; never adjust it to match engine output without evidence.
4. Write the messaging convention file for eShop (`tests/eshop/conventions.yml`) from the source as well; it is supplied through `/config`, never built into the engine.

### 6.3 How it runs

`.github/workflows/eshop-validation.yml` in the engine repository:

1. Build the engine image from the current commit (both architectures).
2. Check out dotnet/eShop at the pinned commit with `actions/checkout` (default shallow clone).
3. Run the engine through the Action on a Linux runner, then the same analysis again with the convention file.
4. Run the evaluator (a test project in the engine repository, executed in the test image) that compares the generation with the ground truth and writes the scores.
5. Run a second time with the NuGet cache restored through `actions/cache`, to measure the warm run.
6. Upload generations, scores and logs as workflow artifacts.

Also run the same image with `docker run` on at least one non-Linux host (Windows or macOS with Docker) and compare the graph with the CI output byte for byte. If no such host is available, report this check as not validated.

### 6.4 Test cases

| # | Area | What to check (minimum) |
|---|---|---|
| 1 | Coverage | Every project with its load mode; failures (for example projects needing a missing workload) reported with reasons |
| 2 | Partial types | At least 3 real partial types, including generated parts (for example EF Core migrations with designer files, gRPC-generated classes) |
| 3 | Razor and Blazor | Components appear as generated types with the generator's name; a Razor generator load failure is reported, never silent |
| 4 | References | 5 symbols used across projects: every usage with the correct kind |
| 5 | Overloads and extension methods | 3 calls resolved to the exact target |
| 6 | Implementations | 3 interfaces with all implementations and `possible_runtime_target` edges |
| 7 | DI | Every registration in 2 services' composition roots, including through extension methods, with lifetimes; 3 consumers |
| 8 | Endpoints | The complete endpoint list (method + route) of 2 APIs |
| 9 | HTTP links | Each typed-client call from the web front end linked to its endpoint through the topology |
| 10 | gRPC | Client → service link with proto evidence |
| 11 | Messaging | Contracts, publishers and subscribers; precision and recall without and with the convention file |
| 12 | Topology | AppHost resources and references |
| 13 | Cross-service traces | At least 2 traces across at least 3 services, one request-based and one message-based; each segment shows its resolution and evidence |
| 14 | Secrets | Keys present, secret keys flagged; searching the whole output for the known values in `appsettings*.json` finds nothing |
| 15 | Packages | Direct and transitive packages and the used external API for 2 packages |
| 16 | Determinism | Two CI runs, amd64 vs arm64, and CI vs a non-Linux host: identical graph, context packs and wiki; identical index dump |
| 17 | Robustness | A run cancelled mid-way leaves no manifest; a run on a later eShop commit produces a new, valid generation |

### 6.5 Measurements

- Precision and recall per area, listing every miss and every false result.
- Coverage (semantic, syntax, failed) and correct abstention (unknowns labelled, not guessed).
- Time per stage (image pull, export, restore, load, extract, artifacts), total Action time on a GitHub-hosted runner, cold versus warm cache, peak memory, image size, generation size.
- `query` latency (median and p95) for the ground-truth questions.

### 6.6 Agent usefulness kit (run by a human)

Prepare, but do not run or score yourself:

- 10 planning questions about eShop with expected files and symbols from the ground truth.
- Instructions for giving an AI assistant the generation (context packs, wiki, query).
- A protocol: the same assistant answers each question in independent sessions, without and with the generation; record correctness, time, interactions and, if visible, tokens.

### 6.7 Final report

Write `docs/reports/step-5-eshop.md` with:

1. Image digest, SDK set, Roslyn version and runner details.
2. The eShop commit and the ground-truth commit.
3. Coverage, precision and recall per area, with misses.
4. The traces with evidence and the query commands that reproduce them.
5. Determinism results across runs, architectures and hosts.
6. Measurements, unknown and unsupported items, decisions and deviations.
7. What is not validated.
8. A short recommendation on what a consumer needs next.

### Gate 5: STOP when

- [ ] Every area in 6.4 has measured results bound to the pinned commit, produced by the workflow.
- [ ] Both traces reproduce from the listed query commands.
- [ ] Determinism holds across runs and architectures (and hosts, or reported as not validated).
- [ ] The usefulness kit is ready for a human.
- [ ] Report written; stop for review.
