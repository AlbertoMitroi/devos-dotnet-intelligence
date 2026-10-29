# Report: step 3b, SDK 8/9, contract v1.1 and generic adapters

Date: 2026-10-30. Branch `step-3b/adapters`. Continues [step-3-fixtures.md](step-3-fixtures.md).

## What was built

- **SDK 8 and 9 in the image:** `sdk/8.0.427` and `sdk/9.0.320` with their runtimes, packs and workload manifests copied from the pinned SDK images (listed in `DEPENDENCIES.md`). Fixtures f16 to f18. Spike S1 part 2 passed (D4); Roslyn rule measured per SDK (D3); D15 closed.
- **User entry for `--user <uid>` (D24):** SDK 8 restore failed under a uid without an `/etc/passwd` entry; nss_wrapper gives the engine's child processes one.
- **Contract v1.1:** schemas vendored from `intelligence-contract` `ea4975a` with checksums; the drift test fails on any file not recorded.
- **Configuration as destinations (D21):** keys from code, values from the `appsettings*.json` of the projects that run the code, secret-looking values never stored.
- **Files outside C# (D22):** BPMN 2.0 with `*.ProcessMetaData.json`, Terraform blocks, ABP proxy descriptions; repository scope for files outside every project.
- **Adapters by resolved symbol (D23):** MassTransit 8, MediatR, Marten 8 and 9, SignalR, Elasticsearch, EF Core tables, workflow handlers from conventions, ABP (distributed and local events, aggregate events, auto entity events, auto API controllers, static client proxies).
- **Linker:** `dispatches_to` for in-process dispatch (fan-out for notifications) and BPMN node to workflow handler; ABP proxy calls routed to repository endpoints.

| Fixture | Covers | Expected exit |
|---|---|---|
| f16-sdk8 | `global.json` 8.0.100 with `latestFeature`, `net8.0`, resolved to 8.0.427 | 0 |
| f17-sdk9 | `global.json` 9.0.100 with `latestFeature`, `net9.0`, resolved to 9.0.320 | 0 |
| f18-mixed-sdks | two solutions under different `global.json` files; each project evaluated by its own SDK | 0 |
| f19-masstransit | publish, send, Kafka produce; two bus transports by configuration; named and conventional endpoints; Kafka topic from configuration | 0 |
| f20-inprocess-stores | MediatR request and notification fan-out; Marten append, projection, snapshot, document tables; SignalR hub and sends; Elasticsearch literal and configured index | 0 |
| f21-efcore-tables | fluent, attribute, convention and static-prefix tables | 0 |
| f22-workflow-terraform | BPMN process, nodes, flows, metadata; plugins by property, attribute, type argument, type name; Terraform at repository scope | 0 |
| f23-abp | ABP stub with ABP 9 names: named distributed event, auto API endpoints, static proxy linked to its endpoint | 0 |

f11 (configuration secrets) now also checks a `DefaultPassPhrase` key and two plain values, none of which may appear in the generation.

## Results

`scripts/verify.sh` on the 3-SDK image (local macOS host, Docker Desktop 29.7.2, linux/arm64), last run at commit `b07df50`:

- Test stage: unit **88 of 88**, integration **52 of 52** (includes every fixture, the Roslyn rule per SDK and spike S1).
- Fixture analyses in the runtime image: **24 of 24** with the expected exit code, each run twice, **byte-identical** except `run.json`, `verify` passes on each.
- GitHub Action mode: passes.
- `version --json`: SDKs `10.0.403`, `9.0.320`, `8.0.427`; runtimes 10.0.14, 9.0.22, 8.0.33; `roslynCompatible: true`.

## eShopOnAbp

Target `abpframework/eShopOnAbp` at `7bfed7a` (archived; ABP 9.0.0, `global.json` 9.0.0 `latestFeature`). Ground truth: [tests/eshoponabp/ground-truth.md](../../tests/eshoponabp/ground-truth.md), committed (`bd529d8`) before the first engine run.

```bash
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD/eshoponabp:/src:ro" -v "$PWD/out:/out" -v "$PWD/cache:/cache" \
  dotnet-intelligence:verify analyze --src /src --out /out
docker run --rm -v "$PWD/out:/g:ro" dotnet-intelligence:verify verify /g
```

| Measure | Value |
|---|---|
| Exit code, status | 10, `partial`: one `workspace-failure` warning on `EShopOnAbp.BasketService.csproj` (MSBuildWorkspace reports the project's Grpc.Net.ClientFactory version check as a failure); the project still loads semantically |
| Coverage | 88 of 88 projects `semantic`; 9 solutions, all resolved to SDK 9.0.320 |
| Time | 242 s with an empty NuGet cache, 52 s warm |
| `verify` | schema, integrity and checksums verified |

| Ground truth item | Expected | Engine | Precision | Recall |
|---|---|---|---|---|
| Distributed event publishers (P1 to P9; P9 is three entity events) | 11 | 11, right event names, transport `rabbitmq`, exchange `EShopOnAbp` from configuration | 11/11 | 11/11 |
| Distributed event handlers (H1 to H9) | 9 | 9, queue from `RabbitMQ:EventBus:ClientName` of each host | 9/9 | 9/9 |
| Publish to handler links | 8 | 8 `consumes_contract` exact | 8/8 | 8/8 |
| Publishers without a handler (P8, P9 Created, P9 Deleted) | 3 | 3 unresolved with reason, no false candidate | | |
| Static client proxy calls | 32 | 32, verb and URL from the proxy descriptions | 32/32 | 32/32 |
| Proxy calls to repository services | 25 (catalog, ordering, basket, payment) | 25 linked: 20 candidate (auto API), 5 exact (payment attribute routes) | 25/25 | 25/25 |
| Proxy calls to the CmsKit public API (endpoints in an ABP package) | 7 | 7 unresolved, "no endpoint of the repository" | | |
| Auto API endpoints | 20 real routes | 23: the 20 plus 3 `api/app/basket*` | 20/23 | 20/20 |
| gRPC call, basket to catalog | 1 | 1 linked exact | 1/1 | 1/1 |

The 20 real auto API routes are the targets of the 20 candidate proxy links (one each), i.e. the routes the generated proxy descriptions name for catalog, ordering and basket; ground truth listed the registrations, not each route, so this row was checked against the proxy descriptions after the run.

Errors found during the run, each fixed in code and in a fixture, then rerun:

1. ABP 9 declares `ConventionalControllers.Create` on `AbpConventionalControllerOptions`; the adapter matched the setting list (taken from my stub), so no auto API endpoint was found. The f23 stub now follows ABP 9.
2. Entity events (`EntityCreatedEto<T>`) share one contract entity (the generic definition), so the same-name fallback paired `ProductEto.Created` with the handlers of `UserEto` and `IdentityRoleEto` (2 false candidates). Generic contracts now carry their type arguments in `name`.
3. **Secret leak:** the snippets of `config.key` evidence carried the whole JSON line, value included; five `StringEncryption:DefaultPassPhrase` values reached `snippets.ndjson` (the redaction patterns did not know `passphrase`). The behaviour dates from step 3; f11 did not catch it because its secret keys matched the patterns. Config key snippets now stop at the key (rule 1.4.6), destination snippets keep the value only when it is not secret-looking (D21), and `passphrase` is a secret name. After the fix, none of the 23 secret-looking values of the repository's `appsettings*.json` is in the generation. The check still matches `localhost`, `rabbitmq` and `master`: a non-secret `RemoteServices` base URL used as a destination, the transport name and C# source.

Known limits:

- Basket calls `ConventionalControllers.Create` twice for the same assembly (root path `basket`, then the default `app`); ABP uses the first setting, the engine does not model the order and reports both route sets as candidates (the 3 extra endpoints above, never targeted by a call).
- The run is `partial` because of the MSBuildWorkspace failure entry above, although every project is semantic. Whether that entry should count as a warning only was not decided here.
- The CmsKit proxy calls stay unresolved: their endpoints live in an ABP package, outside the repository.

## Findings and decisions

- MassTransit 9 and MediatR 13+ are commercially licensed: fixtures use 8.5.12 and 12.5.0 (D23).
- Marten 7.0.0 to 9.12.0 has the critical advisory GHSA-rfx3-98h7-v3xp (SQL injection in the LINQ provider), which includes Marten 8. The fixture uses 9.55.0; the adapter reads both lines (D23).
- In eShopOnAbp's source, `PaymentRequestFailedEto` carries the same event name as `PaymentRequestCompletedEto` (`EShopOnAbp.Payment.Completed`), so a failed payment reaches the completed handler.
- Evidence uses the existing `method` values (`syntax`, `config`) for BPMN, Terraform and proxy descriptions; new values were rejected by the integrity check (D22).
- SDK 8 under `--user <uid>` needs a passwd entry (D24).
