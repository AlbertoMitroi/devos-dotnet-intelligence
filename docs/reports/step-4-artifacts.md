# Report: Step 4, artifact builder

Date: 2026-10-30. Branch `step-4-artifacts`. Requirements and gates: [PLAN.md, Step 4](../PLAN.md#step-4-artifact-builder). Decisions: [D25](../DECISIONS.md#d25-indexsqlite-in-the-manifest-shared-schema-one-site-per-observation-step-4), [D26](../DECISIONS.md#d26-context-packs-and-wiki-derived-from-the-written-records-step-4).

## What was built

| Slice | Code | Result |
|---|---|---|
| 4a linker | `Artifacts/Linker.cs` (from step 3) | unchanged rules; new checks: every link holds the evidence of both sides (all 24 fixtures), ambiguous, unresolved and weakest-part unit tests |
| 4b index | `Artifacts/IndexWriter.cs`, `IndexDump.cs`, `Locations.cs` | `index.sqlite` built from the NDJSON files; tables of angular-intelligence plus four evidence tables; FTS5 over entities, snippet lines and observation fields; canonical dump |
| 4c context packs | `Artifacts/ServiceModel.cs`, `ContextPackWriter.cs` | overview with services, service map and contracts; one service card per project; one contract card per message contract and gRPC service |
| 4d wiki | `Artifacts/WikiWriter.cs` | `wiki/index.md`, `wiki/services/`, `wiki/contracts/`; Mermaid service map and flows; legend; commit, engine and the queries behind each page |
| 4e query | `Artifacts/QueryEngine.cs`, `Cli/QueryCommand.cs` | `symbol`, `refs`, `callers`, `callees`, `impl`, `di`, `routes` (`endpoints`), `http`, `calls-to`, `messages`, `trace`, `search`, `dump`; `index <generation> <file.sqlite>` for older generations |
| 4f finalization | `Cli/AnalyzeRunner.cs`, `Cli/GitHubActionMode.cs`, `GenerationVerifier.cs` | order: NDJSON, index, packs, wiki, validation, manifest, `run.json`; Action outputs read from `manifest.json`; `verify` checks the index against the graph |

Shared definition with angular-intelligence: the index tables and columns and the `query` verbs and JSON rows are the Angular engine's, kept as they are; this engine's additions are additive. The proposal for the contract is in the coordinator's notes folder (`dotnet-step4-index-query-proposal.md`) and was sent to the coordinator session; `devos-intelligence-contract` was not edited.

## Gate 4

| Gate | Check | Result |
|---|---|---|
| G4.1 links on fixtures 7 to 10 with evidence on both sides | `FixtureTests` (expected links of f06 to f10 as sets; evidence of both sides for every link of every fixture) | pass, 52 of 52 integration tests in the dev container (`dotnet test` of the integration project, SDK image with SDKs 10, 9, 8) |
| G4.2 packs and wiki byte-identical across runs and architectures; dump identical | `verify.sh` two runs per fixture (`diff -r`, `index.sqlite` included); dumps written to `.verify/out/*.index-dump`; CI `compare` job diffs both architectures | two runs: pass (fixtures, eShopOnAbp twice, unit test). Architectures: CI run in "CI" below |
| G4.3 verify passes on every fixture generation, fails on a tampered copy | `FixtureTests` runs `GenerationVerifier.Verify` on each generation; `verify.sh` and `verify.ps1` append a byte to a copy and expect exit 50 | pass |
| G4.4 query answers fixture questions; exit 3 with candidates | `queries:` in `expected.yml` of f02, f03, f05, f06, f07, f08, f09, f10 (21 questions written from the fixture sources) | pass; `symbol Format` (f02) and `symbol Widget` (f03) exit 3 with both candidates |
| G4.5 report | this file | |

Unit tests: 116 of 116 (adds `IndexAndQueryTests`, `ArtifactRuleTests` on a hand-written generation).

Mermaid: every diagram of the eShopOnAbp, lab-leasing and fixture f02 to f10 generations (59 diagrams) rendered with `minlag/mermaid-cli:11.4.2@sha256:99c983b3ab4e14033f2880bc1b9de17e5090b4515dabd63fe9cf8c0ae6130956` (`mmdc` on each block): 59 rendered, 0 errors. This check ran once by hand; it is not in CI.

## Real generations

Commands (runtime image of this branch, tag `di-step4:runtime`):

```bash
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD/eshoponabp:/src:ro" -v "$PWD/out:/out" -v "$PWD/cache:/cache" di-step4:runtime analyze --src /src --out /out
docker run --rm -v "$PWD/out:/g:ro" di-step4:runtime query /g http --route /api/basket
```

| Generation | Commit | Status | Files | Size | index.sqlite | Context | Wiki | `artifacts` stage | Total (cold / warm) |
|---|---|---|---|---|---|---|---|---|---|
| eShopOnAbp | `7bfed7a` | partial (exit 10, as in step 3b) | 187 | 111.7 MiB | 72.6 MiB (14.1 MiB gzip) | 102 files, 537 KiB | 39 files, 137 KiB | 15.1 s | 428 s cold cache / 120 s warm |
| lab-leasing | `c2dbb16` | complete | 57 | 15.8 MiB | 10.4 MiB | 22 files, 79 KiB | 21 files, 65 KiB | 0.8 s | 9.4 s warm (`lab-cache`) |

- The graph of eShopOnAbp is byte-identical to the step 3 generation in `_generations/eshoponabp-dotnet` (`diff -r graph`); Step 4 only adds files.
- Two eShopOnAbp runs are byte-identical except `run.json`, `index.sqlite` included.
- The index is the largest file: ids are long (project path plus documentation id) and repeat across tables and indexes. It compresses to 20 %.
- Building the eShopOnAbp index alone (`index` command): 6.9 s including container start.

### Query answers with evidence

eShopOnAbp (each line from the run, shortened ids):

| Question | Command | Answer |
|---|---|---|
| callers | `callers IBasketRepository.GetAsync --depth 2` | 4 callers in `BasketAppService` and `OrderPlacedEventHandler` (`BasketAppService.cs:28`, `:52`, `:71`, `OrderPlacedEventHandler.cs:20`); depth 2: `UserBasketProvider.GetBasketAsync` at `UserBasketProvider.cs:29` as a possible runtime target of `IBasketAppService.GetAsync` |
| refs | `refs EShopOnAbp.BasketService.Entities.Basket` | 10 usages, each with the code line, for example `BasketServiceModule.cs:178 typeof` |
| routes | `routes --route /api/basket` | `GET /api/basket/basket` → `BasketAppService.GetAsync` at `BasketAppService.cs:24`, plus `POST` and `DELETE /api/basket/basket/product` |
| http | `http --route /api/basket` | 3 calls from `BasketClientProxy.Generated.cs:20/28/36`, each linked (candidate) to the endpoint in `BasketAppService.cs` |
| search | `search OrderPlaced` | `OrderPlacedEto` at `OrderPlacedEto.cs:9`, the handler class at `OrderPlacedEventHandler.cs:8` |
| trace | `trace BasketClientProxy.GetAsync` | PublicWeb HTTP call → Basket endpoint → `GetBasketDtoAsync` → `BasketProductService.GetProductAsync` → gRPC call → Catalog `PublicProductGrpService.cs:21`; 8 steps, weakest link step 2 (candidate route link) |

lab-leasing:

| Question | Command | Answer |
|---|---|---|
| callers | `callers CalculateQuoteAsync --depth 2` | `QuotePricingService.PriceAsync` at `QuotePricingService.cs:14`; depth 2: `PriceQuoteHandler.Handle` at `PriceQuoteHandler.cs:11` and two tests, as possible runtime targets of `IQuotePricing.PriceAsync` |
| refs | `refs Leasing.Pricing.QuotePricingRequest` | 8 usages with code lines |
| routes | `routes` | 16 endpoints of `Leasing.Host`, for example `POST /v1/quotes/{}/accept` → `QuotesController.Accept` at `QuotesController.cs:31` |
| http | `http` | **no HTTP calls** (exit 4): lab-leasing makes none in its own source; its calls to Pricing go through the package `Pricing.Client` (`IPricingClient`, visible with `refs`, `callers` and `search IPricingClient`), which is resolved across repositories by federation, not by this generation |
| search | `search pricing --kind observation` | the `IQuotePricing` registration at `PricingModuleExtensions.cs:20`, the `pricing-api` Aspire resource at `AppHost/Program.cs:10`, the `Clients:Pricing:BaseAddress` keys |
| trace | `trace QuotesController.Accept --to Leasing.Pricing.QuotePricingService --depth 12` | 8 steps: in-process send at `QuotesController.cs:34` → handler → flow engine → `PriceQuoteHandler.Handle` → `QuotePricingService.PriceAsync`; weakest link step 7 (candidate runtime target) |

`refs Pricing.Client.IPricingClient` returns exit 3: the solution references `Pricing.Client` 1.0.0 and 1.1.0, so there are two entities; the query lists both and picks none.

### Query latency

Wall time of one `query` process inside the runtime image (process start included), median of 3, on the Apple M-series host (linux/arm64 Docker):

| Query | eShopOnAbp | lab-leasing |
|---|---|---|
| `symbol` | 60 ms | 75 ms |
| `callers --depth 2` | 37 ms | 61 ms |
| `refs` | 39 ms | 52 ms |
| `routes` | 43 ms | 46 ms |
| `http` | 53 ms | 38 ms (no results) |
| `search` | 57 ms | 83 ms |
| `messages` | 45 ms | 46 ms |
| `trace` | 57 ms | 75 ms |
| `dump` | 1,073 ms (164,871 lines) | 164 ms (22,617 lines) |

## Decisions and deviations

- `query` verbs: the plan listed `endpoints`, `calls-to`, `messages`, `trace`; the shared set with the Angular engine adds `routes` (here: HTTP endpoints, `endpoints` is an alias), `http` and `dump`. All of them exist.
- Observation locations (D25): the contract has no "primary evidence", so the index and the cards show the site by a rule (evidence inside the named entity's declaration, else the last code evidence). Proposed for v1.2: an optional `site` evidence id on observations.
- The index file is byte-identical across runs and is listed in the manifest (D25). If the CI comparison ever shows a difference between architectures, the fallback in D25 applies.
- Documentation comments are not part of the records, so FTS5 indexes names, signatures, snippet lines and observation fields only.
- `search` matches every term as a prefix (`OrderPlaced` finds `OrderPlacedEto`); the Angular engine matches whole terms. Text output differs by engine; JSON rows do not.

## What was not verified

- The `compare` job between amd64 and arm64 is the only cross-architecture check; see "CI".
- The GitHub Action outputs from the manifest are covered by the existing CI smoke test (`status`, `manifest-path`) and by unit tests; not on a real repository through a published image (no release yet).
- The Mermaid rendering check is manual (above), not in CI.
- No macOS-host comparison (Step 5).

## CI

Filled after the run on the final commit.
