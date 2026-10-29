# dotnet-intelligence

A context engine for .NET: given a repository at one commit, it produces a deterministic, verifiable set of context artifacts (semantic graph with evidence, queryable index, context packs, wiki). It runs as one Docker image and one GitHub Action.

**Status: graph slice.** `analyze` produces the semantic graph (entities, declarations, references, calls, hierarchy, possible runtime targets) with evidence, framework observations (DI, endpoints, HTTP, gRPC, messaging, configuration keys, Aspire topology, EF Core), links between services, minimal context packs and a manifest validated against [contract v1](docs/CONTRACT.md); `verify` checks any generation. Not yet: `index.sqlite`, `query`, wiki, fixtures 1 to 15. See [docs/reports/step-3-graph.md](docs/reports/step-3-graph.md) for the results on dotnet/eShop.

## Develop

Only Docker is needed.

```bash
scripts/verify.sh
```

`scripts/verify.ps1` runs the same steps from PowerShell.

## Documents

- Requirements: [docs/IMPLEMENTATION-PROMPT.md](docs/IMPLEMENTATION-PROMPT.md)
- Plan and traceability: [docs/PLAN.md](docs/PLAN.md)
- Decisions: [docs/DECISIONS.md](docs/DECISIONS.md)
- Open questions: [docs/OPEN-QUESTIONS.md](docs/OPEN-QUESTIONS.md)
- Contract: [docs/CONTRACT.md](docs/CONTRACT.md)
- Usage: [docs/USAGE.md](docs/USAGE.md)
- Dependencies and licenses: [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)
- Reports: [docs/reports/](docs/reports/)
