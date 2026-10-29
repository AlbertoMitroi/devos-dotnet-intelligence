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
- **Choice:** `10.0.403` (runtime 10.0.14) and `9.0.320` (runtime 9.0.22), the latest on 2026-10-29 (PLAN Appendix A.2). .NET 11 (RC) is excluded. The first image ships `10.0.403` only; `9.0.320` follows in the deferred slice (D15).
- **Reason:** both eShop pins use `rollForward: latestFeature`, so they resolve to these SDKs; the latest patches carry security fixes; one SDK per major keeps the image smaller.
- **Revisit:** on each .NET patch Tuesday (new digest, new image release); when .NET 11 reaches GA the scope decision 1.2 must change first.
- **Status:** accepted.

## D3: Roslyn version of the engine

- **Context:** rule 2.2: engine Roslyn ≥ compiler of every SDK in the image.
- **Options:** latest stable `Microsoft.CodeAnalysis.*`; the exact version shipped in SDK 10.0.403.
- **Choice:** `5.9.0`. The compiler in SDK 10.0.403 is `5.9.0-1.26523.112` on amd64 and arm64 (PLAN Appendix A.5), a prerelease of 5.9.0, so `5.9.0` is newer. The build-time test (R1.4) enforces the rule from Step 1 on, for every SDK in the image.
- **Reason:** latest stable; newer than or equal to the in-box compiler keeps analyzers and generators from the SDK loadable.
- **Revisit:** whenever D2 changes; SDK 9.0.320 must be checked when slice 9a adds it (expected to ship an older compiler, not yet probed).
- **Status:** accepted for SDK 10.

## D4: SDK selection per solution

- **Context:** a repository may contain solutions pinned to SDK 9 and SDK 10. MSBuild evaluation must use the SDK that the solution's `global.json` selects (4.2.3).
- **Options:** (a) MSBuildWorkspace 5.x out-of-process BuildHost, which locates MSBuild per project directory; (b) one engine subprocess per resolved SDK, each with that SDK's MSBuild registered, results merged by the parent; (c) a single MSBuild for all projects.
- **Choice:** (a) if spike S1 confirms it under the image's runtime set; otherwise (b). (c) is rejected: it silently changes evaluation results.
- **Reason:** (a) needs no custom process orchestration; (b) is the proven fallback.
- **Revisit:** result of S1 in Step 1b (single SDK) and slice 9a (SDK 9 added); recorded here.
- **Status:** proposed; with one SDK in the first image only the BuildHost start-up part of S1 applies.

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
- **Status:** accepted (deviation from 2.2, recorded in every gate report until closed).
