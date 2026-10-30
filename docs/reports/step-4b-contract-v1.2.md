# Report: contract v1.2 in the engine (follow-up of Step 4)

Date: 2026-10-30. Branch `v1.2-followup`. Contract: devos-intelligence-contract `v1.2.0` (`2828fd6`), vendored under `schema/`. Decision: [D27](../DECISIONS.md#d27-contract-v12-in-the-engine-follow-up-of-step-4).

## What changed

| Item | Code | Check |
|---|---|---|
| Package identity was null in lab-core and lab-pricing | `Analysis/ProjectEvaluator.cs`: SDK 8 prints "An issue was encountered verifying workloads..." on stdout before the JSON of `msbuild -getProperty`, so parsing failed and every package field stayed null, silently. The parser starts at the JSON; `project-evaluation-failed` and `package-identity-not-evaluated` diagnostics in `RepositoryAnalyzer` | fixture f24 (SDK 8, Version from Directory.Build.props, PackageId opt-in); lab-core 5/5, lab-pricing 3/3, lab-leasing 8/8 projects with package identity |
| `schemaVersion` 1.2, kind schemas | `EngineInfo`, `GenerationVerifier`, embedded `schema/v1/kinds` | every fixture generation passes `verify` with the kind schemas; `site` must be one of the evidence |
| `site` on every observation | `GraphBuilder.AddObservation` (first evidence an adapter passes; ordinal smaller site on merge), `Locations` | `siteAt` in f25 and f26 `expected.yml`; 0 observations without `site` in the five reference generations |
| In-process dispatch (C8) | `Adapters/MediatRAdapter.cs`, `Artifacts/Linker.LinkDispatch` | fixture f25: exact single handler, `requestTypeArguments`, open generic handler, two handlers ambiguous, no handler unresolved, candidate send |
| Call-site destinations (C7) | `SignalRAdapter` (`destinationParameter`), `Adapters/CallArgumentAdapter.cs`, `analyze --input-generation` | fixture f26 (basis source: constant, literal, nameof, unknown, secret); lab-leasing with lab-core as input: `SendToAll("flowUpdated")` and `SendToSpecifiedUsers("taskAssigned")`, basis generation, exact |
| Federation findings | `AnalyzeRunner.RepositoryScopeProject`, `AdapterRunner.AddMissingMetadataEntities`, `GenerationVerifier` (context `projectId`) | lab-leasing: the repository-scope context names a project record; eShopOnAbp: `EntityDeletedEto`1` has an entity record; 0 missing entity ids in the five generations |
| Index and query (C5) | `IndexWriter.Schema` in the order of `schema/index/v1/index.sql` | `Index_schema_matches_the_vendored_contract`; `Json_rows_match_the_contract_query_schemas` validates the `--json` rows of 14 verbs against `schema/query/v1` |

## Results

- Unit tests 130 of 130; integration tests 55 of 55 (dev container, SDKs 10, 9, 8), including fixtures f24, f25, f26.
- lab-leasing analyzed twice with the same input generation: byte-identical except `run.json`.
- lab-leasing in-process links: 19 exact (every `Send(new Command)` from the controllers and consumers to its handler), 11 candidate (the test helper `Send<T>(IRequest<T>)` reaches every handler of an `IRequest<T>` request); before: 19 exact and 1 unresolved.

Reference generations regenerated with the image of commit `3c751d9` (each passes `verify`; lab-leasing with `--input-generation` lab-core):

| Generation | Commit | Status | Projects with package identity | `call.argument` |
|---|---|---|---|---|
| eshoponabp-dotnet | `7bfed7a` | partial (as before) | 88 / 88 | 0 |
| lab-core-dotnet | `cdc080f` | complete | 5 / 5 (Lab.Core.* 1.0.0, packable) | 0 |
| lab-intake-dotnet | `1e3322f` | complete | 3 / 3 | 0 |
| lab-pricing-dotnet | `0ee03cc` | complete | 3 / 3 (Pricing.Client 1.1.0, packable) | 0 |
| lab-leasing-dotnet | `c2dbb16` | complete | 8 / 8 | 2 |

## Not done or not verified

- C7 basis `configured` is not implemented; only SignalR method names are templates (messaging destinations and HTTP routes are not).
- No link inside one repository uses `call.argument` (this engine emits no `realtime.subscribe`); the federation substitutes the values.
- The open generic extension of D27 (an exact send links as candidate to a constrained open generic handler) goes beyond rule 1 of C8; it is stated with a reason on each link.
- Cross-architecture determinism of the new fixtures is checked by CI only.
