# Report: fixtures 1 to 15

Date: 2026-10-29. Branch `step-3/fixtures`. Continues [step-3-graph.md](step-3-graph.md).

## What was built

- Fifteen fixtures under `tests/fixtures/`, each a small repository with its own `global.json` (SDK 10 with `latestFeature`, except fixture 15), a `.slnx` and a hand-written `expected.yml`.
- `FixtureTests` turns each fixture into a Git repository with fixed author and committer dates, analyzes it in-process and compares it with `expected.yml` through `FixtureComparer`. Every declared scope is compared as a set, in both directions (full precision and recall):
  - exit code and status;
  - load mode of every project;
  - diagnostics: the listed ones must exist, and any warning or error not listed fails the test;
  - declarations of the listed entities, entities that must exist or must be absent;
  - relations of the listed kinds and id prefixes, with their evidence lines;
  - observations of the listed kinds (one-to-one on the listed fields);
  - all links;
  - secret values that must not appear in any file of the generation.
- `scripts/verify.sh` and `verify.ps1` read the expected exit code of each fixture from its `expected.yml`, run every fixture twice, compare the runs byte for byte and run `verify` on the result.

| Fixture | Covers | Expected exit |
|---|---|---|
| f01-partial-types | one type in three files plus a RegexGenerator part; partial method definition and implementation | 0 |
| f02-overloads-extensions | overloads and an extension method resolved to the exact target; usage kinds | 0 |
| f03-same-name | `Shared.Widget` in two projects, two entities | 0 |
| f04-multitarget | `net10.0;net9.0` with `#if`; secondary framework reported, other branch absent | 0 |
| f05-dispatch | interface with two implementations, virtual and override, possible runtime targets | 0 |
| f06-di | every DI form of 4.3, registration through an extension method in another project, consumers, DI links | 0 |
| f07-endpoints | nested `MapGroup` through locals, folded constant route, `MapMethods`, attribute and conventional controllers | 0 |
| f08-http-aspire | typed client, base address, Aspire resource and reference (stub, D20), route precedence; candidate, exact and unresolved links | 0 |
| f09-grpc | `.proto` server and client roles, service implementation, generated client call, real Grpc 2.84.0 packages | 0 |
| f10-messaging | custom bus abstraction; without conventions one candidate, with `conventions.yml` exact publish and subscriptions and a cross-service link | 0 |
| f11-config-secrets | appsettings keys with secret flags; no secret value in the generation | 0 |
| f12-source-generator | generator as a project reference, not built by the engine: reported as compilation errors | 10 |
| f13-restore-failure | missing package reported; the other project analyzed | 10 |
| f14-missing-workload | `net10.0-android`: unsupported, syntax fallback | 10 |
| f15-unsupported-sdk | `global.json` 9.0.100 with patch roll-forward: blocked at preflight, syntax fallback | 20 |

## Results

```bash
docker run --rm -v "$PWD":/repo -w /repo -v di-nuget:/root/.nuget/packages ... mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a... \
  dotnet test --project tests/integration/DotnetIntelligence.IntegrationTests -c Release --filter-class "*FixtureTests"
```

- First run: `total: 15, failed: 4` (f06, f07, f09, f13).
- After the fixes below: `total: 15, failed: 0`.
- `scripts/verify.sh` on the macOS host (Docker Desktop, arm64): exit 0. Test stage unit `total: 76`, integration `total: 43` (28 earlier tests plus 15 fixtures), all passed; every fixture: `two runs identical` and `ok: schema, integrity and checksums verified`.
- CI run 40898513731 on commit 74e217c: **success**. `verify (amd64)` 6 min 35 s, `verify (arm64)` 9 min 45 s (both run the test stage, `verify.sh` and `verify.ps1`); `compare amd64 and arm64` printed `amd64 and arm64 generations are byte-identical` over the outputs of all 16 fixtures (f00 to f15).

## Mismatches and decisions

| Fixture | Difference | Wrong side | Resolution |
|---|---|---|---|
| f07 | conventional route `/Home/Index/{id?}` canonicalized as `/home/index/{id` | engine | `?` inside a route parameter no longer starts a query (`Routes.QueryStart`) |
| f09 | two extra `grpc.call` from inside the generated client (its overloads call each other) | engine | calls whose caller is the client type itself are not application calls |
| f13 | `restore-errors` missing | engine | NuGet errors name the project in front of the message; the bracket is the entry point (the solution) |
| f13 | extra `workspace-failure` for the missing package | expectation | MSBuildWorkspace reports the same missing package while loading; a real report of the fixture's failure, added to `expected.yml` |
| f06 | entry point written as `M:Program.<Main>$(System.String[])` | expectation | Roslyn writes `<>` as `{}` in documentation ids: `M:Program.{Main}$(System.String[])`. The same wrong assumption was in the context pack writer (entry points never listed); fixed in the engine |

Engine behaviour asserted on purpose:

- f04: only the primary target framework is analyzed (`secondary-target-frameworks-not-analyzed`); `Legacy()` under the `#else` branch is absent.
- f12: the engine does not build projects, so a generator referenced as a project is not loaded; the missing generated type surfaces as `compilation-errors` (partial), never silently.
- f13: NuGet writes the assets file even when a package is missing, so the project is still loaded semantically, with `restore-errors` and `workspace-failure`.

## Not validated

- An SDK 9 fixture (D15); fixture 15 uses an SDK 9 `global.json` as the unsupported case.
- `analyzer-requires-newer-roslyn` (R3.11), `data.maps_to` (R3.26) and package usage (R3.27) have no fixture; they are checked on eShop only.
- `suggested-conventions.yml` content, usage kinds `write`, `read`, `nameof`, `typeof`, `attribute`, API versions: not asserted by a fixture.
- `SymbolFinder` cross-check, SIGTERM (exit 40), `--repo-url`, `preflight` command: not implemented yet.
