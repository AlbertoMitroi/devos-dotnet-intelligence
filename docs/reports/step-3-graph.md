# Report: graph extraction, adapters, links and contract (vertical slice)

Date: 2026-10-29. Branch `step-3/extractor`. Order agreed with the owner: a vertical slice on dotnet/eShop first (Step 3 and the linking of Step 4, with the contract of Step 2), fixtures and the remaining artifacts after.

## Result on dotnet/eShop

Target: `913acea66708a59595351174cee9b29f37179d29`, SDK 10.0.403, local macOS host (Docker Desktop, linux/arm64), warm NuGet cache.

```bash
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD/src:/src:ro" -v "$PWD/out:/out" -v "$PWD/cache:/cache" \
  -v "$ENGINE/tests/eshop:/config:ro" dotnet-intelligence:dev analyze --src /src --out /out --config /config/conventions.yml
docker run --rm -v "$PWD/out:/g:ro" dotnet-intelligence:dev verify /g
```

| Measure | Value |
|---|---|
| Exit code, status | 10, `partial` (3 MAUI projects need workloads the image does not have) |
| Coverage | 28 projects: 25 `semantic`, 3 `syntax` (ClientApp, HybridApp, ClientApp.UnitTests: `missing-workload`, NETSDK1147) |
| Entities, declarations | 8,953 and 5,739 |
| Relations | 31,907: references 18,079, calls_declared_target 5,679, contains 5,179, declares 829, implements 721, overrides 710, possible_runtime_target 422, inherits 176 |
| Evidence, snippets | 35,401 and 18,221 |
| Observations | 704: di.consumes 317, http.call 64, config.key 61, di.registers 52, http.endpoint 51, messaging.subscribe 43, messaging.publish 42, topology.resource 34, topology.reference 17, data.maps_to 9, grpc.call 9, grpc.service 3, grpc.proto 2 |
| Links | 221: DI 93 exact, 4 ambiguous, 4 unresolved; messaging 17 exact, 3 candidate, 27 unresolved; gRPC 9 exact; HTTP 8 exact, 1 candidate, 55 unresolved |
| `verify` | `ok: schema, integrity and checksums verified` |
| Determinism | three consecutive runs byte-identical except `run.json` (`diff -rq -x run.json`) |
| Time | 27.0 s total: restore 7.2, load 8.5, extract 4.1, adapters 3.0, artifacts 3.1 (seconds); cold cache restore was 135 s |
| Peak working set | 986 MB |
| Generation size | 48 MB |

Spot checks, read against the eShop source:

- Partial types: 20 types with declarations in more than one file, for example EF Core migrations (`20231009153249_Initial.cs` and `.Designer.cs`) and JSON source generator contexts (generated parts with `System.Text.Json.SourceGeneration.JsonSourceGenerator`). Razor components are declared by `Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator`.
- `IBasketRepository` is implemented by `RedisBasketRepository`; the call in `src/Basket.API/Grpc/BasketService.cs:26` has it as `possible_runtime_target`.
- Extension methods resolve to their definitions (`MapCatalogApi`, `AddApplicationServices`).
- gRPC: 9 calls from WebApp reach `BasketApi.Basket` in Basket.API (exact).
- HTTP: WebApp → Ordering.API `GET` and `POST /api/orders`; WebAppComponents → Catalog.API `catalogbrands`, `catalogtypes`, `items/by`, `items/withsemanticrelevance` (exact) and `items/{}` → `items/{id:int}` (candidate); WebhookClient → Webhooks.API `GET`/`POST /api/webhooks`. The base address comes from `AddHttpClient<T>(o => o.BaseAddress = new("https+http://catalog-api"))`, the project from the Aspire resource `AddDotnetProject("catalog-api", "../Catalog.API")`.
- Messaging with `tests/eshop/conventions.yml` (written from source; RabbitMQ routes by type name, `src/EventBusRabbitMQ/RabbitMQEventBus.cs:33`): for example `OrderStartedIntegrationEvent` Ordering.API → Basket.API, `OrderStatusChangedToPaidIntegrationEvent` Ordering.API → Catalog.API, WebApp, Webhooks.API (exact); `OrderStockConfirmed/RejectedIntegrationEvent` Catalog.API → Ordering.API (candidate: chosen by a conditional). Without the file: 0 exact, 10 candidate `possible_communication` links.

## Unresolved, and why

- HTTP 55 unresolved: 49 calls have no typed-client registration with a base address (mostly test projects using `HttpClient` from a test factory); 7 build the URI at run time (fields, properties, parameters); each link has its reason.
- Messaging 27 unresolved: publishes with no subscriber in the repository (for example events only consumed by tests or not consumed).
- DI 4 unresolved: services registered only in composition roots that do not include the consumer's project.
- `generators-produced-nothing` (5): projects that reference generators which emitted nothing; reported as `unknown`, not as a failure.

## Bugs found and fixed through eShop

1. `docker run --user <uid>` leaves `HOME=/`; NuGet crashed on its migrations mutex. The engine gives child processes a writable `HOME` when the inherited one is not writable.
2. A missing workload in one project stopped the restore of the whole solution (NETSDK1147 at evaluation). Restore falls back to project by project for projects without assets.
3. eShop sets `UseArtifactsOutput`; assets are not in `obj/`. The engine asks MSBuild for `ProjectAssetsFile`, `TargetFramework(s)` instead of assuming paths.
4. Generator names came from the wrong path segment; they now come from the path before the hint name.
5. Snippets of generated lines contained the work directory; they are normalized like the stored files.
6. Members of anonymous types share documentation ids (`P:.Id`) and made runs differ; they are out of scope (D6).
7. gRPC service names are `static readonly` fields, not constants; Aspire projects are often added with a path string; HTTP routes needed ASP.NET-style precedence; observation fields `project`/`context` are now reserved.

## Tests

Image test stage: unit `total: 76, failed: 0`; integration `total: 28, failed: 0` (includes the contract fixtures: valid, dangling reference, duplicate id, checksum mismatch, unknown required capability, newer minor with unknown kind). CI on the branch: runs 40885588114 and 40889031059 `success` (amd64 and arm64, byte comparison of fixture outputs).

## Not done in this slice (still required by the prompt)

- Fixtures 1 to 15 with hand-written `expected.yml` and full precision and recall (4.5); adapters are verified on eShop only.
- SymbolFinder cross-check, SIGTERM handling (exit 40), `--repo-url`, `preflight` command, workflow annotations beyond diagnostics.
- C# types generated from the schema (D19).
- `index.sqlite`, `query`, wiki, contract cards (Step 4); the context packs are the minimal overview and project cards.
- eShop ground truth, evaluator, CI workflow and amd64/arm64 comparison on eShop (Step 5).
- SDK 9 (D15).
