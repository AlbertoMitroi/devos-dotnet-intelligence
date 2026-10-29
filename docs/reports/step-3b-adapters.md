# Report: step 3b, SDK 8/9, contract v1.1 and generic adapters

Date: 2026-10-29. Branch `step-3b/adapters`. Continues [step-3-fixtures.md](step-3-fixtures.md).

Status: **draft**. Sections marked *pending* need the 3-SDK image build and the eShopOnAbp run (Docker paused while the disk was full).

## What was built

- **SDK 8 and 9 in the image:** `sdk/8.0.427` and `sdk/9.0.320` with their runtimes, packs and workload manifests copied from the pinned SDK images (listed in `DEPENDENCIES.md`). Fixtures f16 to f18. *Pending: image build, spike S1 part 2, Roslyn rule per SDK.*
- **Contract v1.1:** schemas vendored from `intelligence-contract` `ea4975a` with checksums; the drift test fails on any file not recorded.
- **Configuration as destinations (D21):** keys from code, values from the `appsettings*.json` of the projects that run the code, secret-looking values never stored.
- **Files outside C# (D22):** BPMN 2.0 with `*.ProcessMetaData.json`, Terraform blocks, ABP proxy descriptions; repository scope for files outside every project.
- **Adapters by resolved symbol (D23):** MassTransit 8, MediatR, Marten 8 and 9, SignalR, Elasticsearch, EF Core tables, workflow handlers from conventions, ABP (distributed and local events, aggregate events, auto entity events, auto API controllers, static client proxies).
- **Linker:** `dispatches_to` for in-process dispatch (fan-out for notifications) and BPMN node to workflow handler; ABP proxy calls routed to repository endpoints.

| Fixture | Covers | Expected exit |
|---|---|---|
| f19-masstransit | publish, send, Kafka produce; two bus transports by configuration; named and conventional endpoints; Kafka topic from configuration | 0 |
| f20-inprocess-stores | MediatR request and notification fan-out; Marten append, projection, snapshot, document tables; SignalR hub and sends; Elasticsearch literal and configured index | 0 |
| f21-efcore-tables | fluent, attribute, convention and static-prefix tables | 0 |
| f22-workflow-terraform | BPMN process, nodes, flows, metadata; plugins by property, attribute, type argument, type name; Terraform at repository scope | 0 |
| f23-abp | ABP stub: named distributed event, auto API endpoints, static proxy linked to its endpoint | 0 |

## Results

Measured in the pinned SDK 10 container (`mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a...`), not yet in the 3-SDK image:

- Fixtures f01 to f15: **15 of 15 pass** (no regression).
- Fixtures f19 to f23: **5 of 5 pass**.
- Unit tests: **88 of 88 pass**, including the vendored-schema drift test and the naming-rule tests.

*Pending:* full test stage in the image, f16 to f18, `scripts/verify.sh` (two runs, byte comparison).

## eShopOnAbp

Pin and ground truth: [tests/eshoponabp/ground-truth.md](../../tests/eshoponabp/ground-truth.md), written before the first run. *Pending:* run, precision and recall per item against the ground truth.

Found while writing the ground truth: `PaymentRequestFailedEto` carries the same event name as `PaymentRequestCompletedEto` (`EShopOnAbp.Payment.Completed`), so a failed payment reaches the completed handler.

## Findings and decisions

- MassTransit 9 and MediatR 13+ are commercially licensed: fixtures use 8.5.12 and 12.5.0 (D23).
- Marten 7.0.0 to 9.12.0 has the critical advisory GHSA-rfx3-98h7-v3xp (SQL injection in the LINQ provider), which includes Marten 8. The fixture uses 9.55.0; the adapter reads both lines (D23).
- Evidence uses the existing `method` values (`syntax`, `config`) for BPMN, Terraform and proxy descriptions; new values were rejected by the integrity check (D22).
