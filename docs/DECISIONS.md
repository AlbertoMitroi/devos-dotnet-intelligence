# Decisions

Format per entry: context, options, choice, reason, how to revisit. Status `proposed` means it waits for the Step 0 review or for a spike; `accepted` means it is in force.

## D1: Repository home

- **Context:** the engine needs GitHub Actions, GHCR and arm64 runners (Step 1).
- **Options:** public repository under `AlbertoMitroi`; private repository under `AlbertoMitroi`; another owner.
- **Choice:** public `AlbertoMitroi/dotnet-intelligence`; local checkout in `~/repos/devos/dotnet-intelligence`. The remote is created at Step 1.
- **Reason:** owner's choice. Public gives free 4 vCPU arm64 runners and lets any workflow pull the Action image without credentials.
- **Revisit:** if the engine must become private, callers need GHCR read access and arm64 runners drop to 2 vCPU.
- **Status:** accepted.

## D2: SDK set in the image

- **Context:** the image must contain the .NET 10 and .NET 9 SDKs (2.2).
- **Options:** latest patch of each band at implementation time; the exact versions eShop pins (10.0.302, 9.0.200); several feature bands per major.
- **Choice:** `10.0.403` (runtime 10.0.14), `9.0.320` (runtime 9.0.22) and `8.0.427` (runtime 8.0.33), the latest on 2026-10-29 (PLAN Appendix A.2). .NET 11 (RC) is excluded. The first image shipped `10.0.403` only; `9.0.320` and `8.0.427` were added in step 3b (D15), copied with their runtimes, packs and workload manifests from the pinned SDK images. SDK 8 was added because the target system still has .NET 8 solutions.
- **Reason:** both eShop pins use `rollForward: latestFeature`, so they resolve to these SDKs; eShopOnAbp pins `9.0.0` with `latestFeature` and resolves to `9.0.320`; the latest patches carry security fixes; one SDK per major keeps the image smaller.
- **Revisit:** on each .NET patch Tuesday (new digest, new image release); when .NET 11 reaches GA the scope decision 1.2 must change first.
- **Status:** accepted.

## D3: Roslyn version of the engine

- **Context:** rule 2.2: engine Roslyn ≥ compiler of every SDK in the image.
- **Options:** latest stable `Microsoft.CodeAnalysis.*`; the exact version shipped in SDK 10.0.403.
- **Choice:** `5.9.0`, and the rule compares **assembly versions** of `Microsoft.CodeAnalysis.dll` (engine vs `sdk/<version>/Roslyn/bincore` of each SDK). Both build numbers are recorded in `version --json` and the manifest.
- **Reason:** recorded in Step 1 (report, section "Roslyn rule"): the nuget.org package `5.9.0` is build `5.9.0-1.26357.3`, while SDK 10.0.403 ships the later servicing build `5.9.0-1.26523.112` of the same release line. Comparing build strings would declare the latest stable package older than the SDK and leave no compliant package on nuget.org. Analyzers and source generators bind to the assembly version (`5.9.0.0` for both), which is what decides whether they load. The Step 0 version of this entry compared `5.9.0` with `5.9.0-1.26523.112` as SemVer strings; that was wrong and is corrected here.
- **Revisit:** whenever D2 changes; when a later `Microsoft.CodeAnalysis` release appears, prefer the one at or above the SDK build.
- **Measured in step 3b** (`version --json` of the 3-SDK image): engine `5.9.0` (build `5.9.0-1.26357.3`); SDK 10.0.403 compiler `5.9.0` (`5.9.0-1.26523.112`), SDK 9.0.320 compiler `4.14.0` (`4.14.0-3.26064.1`), SDK 8.0.427 compiler `4.11.0` (`4.11.0-3.25569.22`); `roslynCompatible: true`.
- **Status:** accepted for SDKs 8, 9 and 10; checked by `ImageToolchainTests` in the test stage.

## D4: SDK selection per solution

- **Context:** a repository may contain solutions pinned to SDK 9 and SDK 10. MSBuild evaluation must use the SDK that the solution's `global.json` selects (4.2.3).
- **Options:** (a) MSBuildWorkspace 5.x out-of-process BuildHost, which locates MSBuild per project directory; (b) one engine subprocess per resolved SDK, each with that SDK's MSBuild registered, results merged by the parent; (c) a single MSBuild for all projects.
- **Choice:** (a). Spike S1, first part, passed in Step 1: `WorkspaceSpikeTests` opens the `.slnx` of fixture f00 with `MSBuildWorkspace.Create()` (no `Microsoft.Build.Locator`) in the image's test stage, with only runtime 10.0.14 present; the project loads with its document, no failure-level `WorkspaceFailed` diagnostic and no compilation error. The second part passed in step 3b, in the image with SDKs 8, 9 and 10: fixture f18 has two solutions under different `global.json` files in one repository; each project is evaluated by its own SDK (`resolvedSdk` 8.0.427 and 10.0.403, and the SDK-derived preprocessor symbols differ per project in the same run); f16 (SDK 8) and f17 (SDK 9) load semantically. (b) is not needed. (c) is rejected: it silently changes evaluation results.
- **Reason:** (a) needs no custom process orchestration.
- **Revisit:** if a target repository shows a project evaluated by the wrong SDK.
- **Status:** accepted for SDKs 8, 9 and 10.

## D5: Generating C# types from the schema

- **Context:** 3.6 requires generated types and a build failure on drift.
- **Options:** NJsonSchema or a similar code generator; a small in-repo generator for the JSON Schema subset the contract uses.
- **Choice:** in-repo generator `tools/SchemaGen`, run by the test stage; the test fails if regenerated output differs from the committed files.
- **Reason:** output under our control (stable ordering, no tool banners, no timestamps); one less dependency; the contract uses a small subset (objects, enums, arrays, `$ref`, `additionalProperties` for extensions).
- **Revisit:** if the schema needs constructs beyond that subset.
- **Status:** accepted.

## D6: Symbols without a documentation comment id

- **Context:** 3.3: local functions and lambdas have no usable documentation comment id; record the choice.
- **Options:** out of scope; container-relative ids such as `{containerId}#local:{name}:{ordinal}`.
- **Choice:** out of scope in v1. References and calls located inside them are attributed to the containing member, with the exact span as evidence.
- **Reason:** ordinals shift with unrelated edits, which makes ids unstable across commits; attribution to the container keeps every call visible.
- **Revisit:** if consumers need call graphs through lambdas; can be added in a minor version with a new capability.
- **Status:** proposed.

## D7: Record ids

- **Context:** 1.4.9 forbids random ids; records need stable ids.
- **Options:** counters in sort order; content hashes.
- **Choice:** `rel:`, `ev:`, `obs:`, `lnk:`, `diag:`, `ctx:`, `prj:` prefix + first 32 hex characters of SHA-256 over the canonical JSON of the identifying fields (documented per record in CONTRACT.md). Entity ids follow 3.3 verbatim.
- **Reason:** independent of processing order, identical across architectures, collision probability negligible at 128 bits.
- **Revisit:** if a collision is ever detected (the integrity check reports duplicates).
- **Status:** accepted.

## D8: Building the working copy

- **Context:** 2.3: the analysed bytes must be the committed ones on every host OS.
- **Options:** `git archive HEAD | tar -x`; `git ls-tree -r -z HEAD` + `git cat-file --batch`; `git worktree add`.
- **Choice:** `ls-tree` + `cat-file --batch`, written into `/work` with the engine's own file writer.
- **Reason:** `git archive` applies `export-ignore`, `export-subst` and end-of-line attributes, so files can differ from their blob and from their blob id; `cat-file` returns exactly the blob bytes, and the blob id comes from the same listing. Symlinks, submodule gitlinks and LFS pointers are visible in the listing and become diagnostics.
- **Revisit:** if a repository needs LFS content resolved; that would be a new opt-in flag.
- **Status:** proposed.

## D9: Span columns

- **Context:** spans are line and column, never byte offsets (2.3).
- **Options:** UTF-16 code units (Roslyn's native unit, LSP default); Unicode scalar values; grapheme clusters.
- **Choice:** 1-based line, 1-based column, column counted in UTF-16 code units; stated in CONTRACT.md.
- **Reason:** no conversion from Roslyn, matches what most editors and LSP clients expect.
- **Revisit:** if a consumer needs another unit; a minor version can add an alternative column field.
- **Status:** proposed.

## D10: Multi-architecture builds

- **Context:** 2.2 and 2.6 require amd64 and arm64, and a byte comparison of outputs.
- **Options:** QEMU emulation on one runner; native `ubuntu-24.04` and `ubuntu-24.04-arm` runners.
- **Choice:** native runners, one job per architecture; images pushed by digest and merged into one manifest list.
- **Reason:** running MSBuild and Roslyn under QEMU is slow, and emulation can hide architecture-specific behaviour that the comparison is meant to catch.
- **Revisit:** if arm64 runners become unavailable.
- **Status:** proposed.

## D11: Image reference in action.yml

- **Context:** 2.5: Docker container action using the published image by version.
- **Choice:** `runs.image: docker://ghcr.io/albertomitroi/dotnet-intelligence:<semver>`. `release.yml` rewrites the version, commits, tags `v<semver>` and moves `v1`.
- **Reason:** callers pin `@v1` or `@v1.x.y`; no image build per use.
- **Revisit:** if GitHub adds digest pinning for container actions that resolves through tags.
- **Status:** accepted.

## D12: GitHub Action mode

- **Context:** the Action must map inputs, run the analysis, apply `fail-on` and set outputs.
- **Options:** shell entrypoint script; a CLI mode in C#.
- **Choice:** CLI mode `gha`, selected by `action.yml` args; reads `INPUT_*`, writes `GITHUB_OUTPUT`, emits annotations, maps exit codes with `fail-on`.
- **Reason:** keeps one runtime (1.2.5), avoids shell interpolation of caller input (1.4.8), and is unit-testable.
- **Status:** accepted.

## D13: Test framework

- **Choice:** xUnit v3 (Apache-2.0), run with `dotnet test` inside the test stage.
- **Reason:** first-class support for `TheoryData` over fixture folders and parallelism control.
- **Status:** accepted.

## D14: Em dashes in the imported prompt

- **Context:** the owner's writing rules forbid the em dash in any text in the repository.
- **Choice:** in `docs/IMPLEMENTATION-PROMPT.md` the em dashes of the headings were replaced with colons; no other wording changed.
- **Status:** accepted.

## D15: SDK 10 first, SDK 9 deferred

- **Context:** 2.2 asks for the .NET 9 and .NET 10 SDKs side by side. eShop main pins SDK `10.0.302` with `latestFeature` (PLAN Appendix A.6), so the validation target needs only SDK 10.
- **Options:** both SDKs from Step 1; SDK 10 first, SDK 9 as a later slice.
- **Choice:** SDK 10 first. SDK 9 comes as slices 9a to 9d (PLAN section 3), after Gate 5 unless the review moves it earlier. Until then: Gate 1 checks SDK 10 only, the SDK 9 fixture and the eShop .NET 9 commit (R5.2) are deferred, and fixture 15 uses a `global.json` pinned to SDK 9 as its "SDK not in the image" case.
- **Reason:** owner's decision on 2026-10-29: test on the SDK the target repository uses before widening. Smaller image (A.5) and faster CI while the extractor is built.
- **Constraint:** the code treats the SDK set as a list from the start (resolution, Roslyn rule, `version --json`), so adding SDK 9 is an image change plus fixtures, not a redesign.
- **Revisit:** at the Gate 3 review, or as soon as a target repository needs SDK 9.
- **Closed (step 3b, 2026-10-29):** the target system needs SDK 8 and 9, so both were added (D2). Fixtures f16 (SDK 8), f17 (SDK 9) and f18 (two SDKs in one repository) pass in the image; fixture 15 keeps its case, because `9.0.100` with `rollForward: latestPatch` still has no matching SDK (only `9.0.3xx` is in the image).
- **Status:** closed; the deviation from 2.2 no longer applies.

## D16: Action image runs as root

- **Context:** 2.2 asks for a non-root image. GitHub documents that Docker container actions must run as the default Docker user (root) to write `GITHUB_WORKSPACE`; `action.yml` cannot choose a user.
- **Options:** a root default user for everyone; a composite action calling `docker run --user` (not a Docker container action, against 2.5); a second tag of the same image with `USER root`.
- **Choice:** the `action` stage is the `runtime` stage plus `USER root`, published as `<version>-action`; `action.yml` uses it. The plain `<version>` tag keeps the non-root `app` user. At the end of a run in Action mode the engine changes the owner of the output and cache directories to the owner of the workspace (`chown -R --reference`).
- **Reason:** keeps the non-root default for `docker run`, satisfies GitHub's requirement, and leaves files that later steps (upload, cache, cleanup) can manage. CI checks the owner (`test -O`).
- **Revisit:** if GitHub lets container actions set a user.
- **Status:** accepted.

## D17: Release flow

- **Context:** 2.6 says the release updates `action.yml` to the released version. A tag that points at a commit whose `action.yml` names another version would ship a broken `@v<version>`.
- **Options:** the workflow commits the new `action.yml` after the tag (then the tag and the action disagree); the release commit carries the version and the workflow checks it.
- **Choice:** the release commit sets `<Version>` in `Directory.Build.props` and the image tag in `action.yml`; `release.yml` on tag `v<version>` refuses to publish unless tag, `Directory.Build.props` and `action.yml` agree, then pushes `<version>` and `<version>-action` for both architectures with SBOM and provenance, creates the GitHub release (with the schema once it exists) and moves the major tag. `ActionManifestTests` keeps `action.yml` and `Directory.Build.props` in agreement on every build.
- **Reason:** every tag is consumable as soon as its images exist.
- **Revisit:** if releases become automated from main.
- **Status:** accepted.

## D18: Message fan-out is not ambiguity

- **Context:** 5.1 says several matches are kept as ambiguous. For publish/subscribe, several subscribers of one contract are all real receivers.
- **Choice:** for messaging links each subscriber keeps the weakest resolution of the publisher and that subscriber; `ambiguous` is used for request/response targets (HTTP, gRPC, DI). Two observations of the same subscription (handler interface and subscription registration) are merged into one target.
- **Reason:** otherwise every event with two subscribers would look uncertain although the code is exact (eShop: OrderStatusChangedToPaid reaches Catalog.API, WebApp and Webhooks.API).
- **Revisit:** if a consumer needs "one of" semantics for competing consumers; that would be a new field, not a change of meaning.
- **Status:** accepted (deviation from the wording of 5.1).

## D19: Contract validation without generated types yet

- **Context:** 3.6 asks for C# types generated from the schema with a drift check (D5).
- **Choice:** in this slice the schemas exist and are enforced by an in-repo validator for the subset they use (embedded in the engine, so `verify` works anywhere); records are still built as JSON objects. The generator of D5 is not written yet.
- **Reason:** the user asked for the complete graph first; validation of every written record against the schema gives the guarantee consumers need now.
- **Revisit:** before Gate 2 is declared passed: generate the types and switch the writers to them.
- **Status:** accepted as an open gap, reported in the Step 3 report.

## D20: Aspire stub in fixture 8

- **Context:** fixture 8 needs an Aspire AppHost. The Aspire hosting packages are large and pull platform-specific dependencies on every test-stage build.
- **Options:** real Aspire packages; a small stub project in the fixture with the Aspire namespaces and member names the topology adapter matches.
- **Choice:** the stub (`tests/fixtures/f08-http-aspire/src/AspireStub`). The adapter recognizes resolved symbols by full name, so the stub exercises the same code path. Real Aspire is covered by the eShop run (24 resources in its AppHost).
- **Revisit:** if the adapter starts depending on Aspire behaviour beyond names and signatures.
- **Status:** accepted.


## D21: Configuration values used as destinations (contract C3 / D20)

- **Context:** topics, queues, exchanges, base addresses and hub routes usually come from configuration; without them messages and HTTP calls cannot be linked across repositories. Rule 1.4.6 says configuration values are never stored. The contract records the exception as decision C3 (D20 in the federation plan); this repository already used D20 for the Aspire stub, so the engine records it as D21.
- **Choice:** a configuration value is stored only when an adapter uses it as a destination and neither the key nor any of its values looks secret (`SecretScrubber.LooksSecret`). It goes into that observation's `destination` (or `baseAddress`) with `nameSource: configuration`, `configurationKey`, `configurationFiles`, and `alternatives` when environment files disagree; the evidence of every configuration line used is added to the observation. A secret-looking destination keeps `name: null` and `secret: true`. Keys come from code only: `IConfiguration` indexers, `GetValue`, `GetSection` chains, `GetConnectionString`, and options classes bound with `Configure<T>`, `Bind`, `BindConfiguration` or `Get<T>`; values come from the `appsettings*.json` of the projects that run the code (the project and every project that references it). Every other configuration value stays out (`config.key` still stores keys only).
- **Reason:** owner's decision, accepted on 2026-10-29 (`intelligence-contract` `fd582dc`).
- **Revisit:** if a destination value is found to carry a secret the flag missed: tighten the flag, never drop the check.
- **Status:** accepted (deviation from rule 1.4.6, as recorded in the contract).

## D22: Files outside C# (BPMN, Terraform, proxy descriptions)

- **Context:** contract v1.1 adds observations read from non-C# files. Observations need a project and an analysis context; the evidence `method` enum has no value for these readers.
- **Choice:** a file belongs to the deepest project whose directory contains it; files outside every project belong to the repository scope: one extra analysis context (`project: "."`, `configuration: repository`, `loadMode: syntax`, `scope: repository`), listed in the manifest, with no project record and no coverage entry. Evidence uses the existing methods: `syntax` for BPMN XML and Terraform blocks, `config` for JSON read as data (`*.ProcessMetaData.json`, ABP `*generate-proxy.json`). XML is read with DTDs prohibited and no resolver; Terraform values are never read.
- **Reason:** no contract change for the method enum; consumers already handle syntax-level and configuration evidence.
- **Revisit:** if consumers need to tell the readers apart; a minor can add method values.
- **Status:** accepted.

## D23: Generic adapters and their fixture packages

- **Context:** step 3b adds adapters by resolved symbol for MassTransit 8, MediatR, Marten, SignalR, Elasticsearch, EF Core tables, BPMN, workflow handler conventions, Terraform and ABP. Fixtures need the real libraries where possible.
- **Choice:** fixtures use real packages: MassTransit `8.5.12` (9.x is commercially licensed; the target system uses 8), MediatR `12.5.0` (13 and later are commercially licensed; the target uses 12), Marten `9.55.0` (every Marten from 7.0.0 to 9.12.0 has the critical advisory GHSA-rfx3-98h7-v3xp; the adapter also matches Marten 8, whose event and projection APIs are declared in `Marten.*`, while 9 declares them in `JasperFx.Events.*`), Elastic.Clients.Elasticsearch `8.19.27`, EF Core Relational `10.0.14`. ABP uses a stub with ABP's names (fixture 23), as the Aspire stub (D20); real ABP 9.0.0 is covered by the eShopOnAbp run. Libraries with an adapter of their own are excluded from the generic messaging heuristics, so they never produce duplicate candidates.
- **Reason:** exact matching by symbol needs the real symbols; licenses and advisories decide the versions.
- **Status:** accepted.

## D24: User entry for "docker run --user <uid>"

- **Context:** `scripts/verify.sh` and the documented usage run the image with `--user "$(id -u):$(id -g)"`, so outputs belong to the host user. Such a uid has no `/etc/passwd` entry in the image. SDK 10 restores fine; SDK 8 does not: its NuGet fails in the restore task with `System.IO.IOException: The system cannot open the device or file specified. : 'NuGet-Migrations'` (measured on fixture f16 in step 3b; root, the image user `app` and a uid with a passwd entry restore; `HOME`, `TMPDIR`, `USER`, the first-use sentinel and rolling SDK 8 forward to runtime 10 do not help).
- **Options:** a world-writable `/etc/passwd` filled at start (weakens the image); requiring the image user (outputs owned by uid 1654 on the host); nss_wrapper for the child processes.
- **Choice:** nss_wrapper. The runtime stage installs `libnss-wrapper` `1.1.15-2`; when the effective uid has no entry, the engine writes a passwd and group file under the tool `HOME` and sets `LD_PRELOAD`, `NSS_WRAPPER_PASSWD` and `NSS_WRAPPER_GROUP` for the processes it starts (restore, evaluation, the MSBuild build host). Nothing changes when the entry exists.
- **Reason:** scoped to the tools the engine runs, no change to system files, one small package.
- **Revisit:** when a later SDK 8 NuGet no longer needs a user entry, or when SDK 8 leaves the image.
- **Status:** accepted.
