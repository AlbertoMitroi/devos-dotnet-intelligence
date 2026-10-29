# dotnet-intelligence

A context engine for .NET: given a repository at one commit, it produces a deterministic, verifiable set of context artifacts (semantic graph with evidence, queryable index, context packs, wiki). It runs as one Docker image and one GitHub Action.

**Status: Step 1 (infrastructure).** The image, the GitHub Action, CI and the release workflow exist. `analyze` exports the committed state and discovers solutions and projects; semantic extraction arrives in Step 3. Every generation is therefore `partial`. See [docs/USAGE.md](docs/USAGE.md) for what works today.

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
- Usage: [docs/USAGE.md](docs/USAGE.md)
- Dependencies and licenses: [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)
- Reports: [docs/reports/](docs/reports/)
