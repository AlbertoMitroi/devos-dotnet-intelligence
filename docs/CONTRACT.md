# Contract v1

The format of a generation. Technology-neutral fields carry everything a consumer needs; .NET detail travels under `ext.dotnet.*`. Schemas: [`schema/v1/`](../schema/v1/). `schemaVersion` in the manifest: `1.2` (contract [v1.2.0](https://github.com/AlbertoMitroi/devos-intelligence-contract/blob/v1.2.0/docs/CONTRACT-v1.2.md), vendored under `schema/`).

## Layout

| Path | Content |
|---|---|
| `manifest.json` | Written last. Version, engine, Roslyn, SDK set, repository, commit, dirty, configuration hash, contexts, coverage, every file with SHA-256 |
| `graph/projects.ndjson` | `recordType: project` and `recordType: analysisContext` records |
| `graph/entities.ndjson` | Logical symbols |
| `graph/declarations.ndjson` | Where entities are declared, one record per part (partial types have several) |
| `graph/relations.ndjson` | Typed edges between entities (and `declares` from a project) |
| `graph/evidence.ndjson` | Path, blob id, span, extractor, method, context, store |
| `graph/observations.ndjson` | Normalized facts from adapters |
| `graph/links.ndjson` | Relationships between services inside the repository |
| `graph/diagnostics.ndjson` | What failed, is partial, unsupported or unknown, with reasons |
| `snippets.ndjson` | Redacted source lines, keyed by blob id and line |
| `generated/` | Sources that exist only in the build (source generators, MSBuild outputs such as gRPC code), work directory replaced by `/_/` |
| `index.sqlite` | Queryable index (SQLite with FTS5), derived from the NDJSON files; schema below |
| `context/` | Deterministic Markdown context packs: `overview.md`, `projects/` (one service card per project), `contracts/` (one card per message or gRPC contract) |
| `wiki/` | Deterministic wiki with Mermaid diagrams: `index.md`, `services/`, `contracts/` |
| `suggested-conventions.yml` | Messaging candidates for a human to confirm (present when there are candidates) |
| `run.json` | Timings, host, counts; excluded from determinism checks; not listed in the manifest |

Every NDJSON file: one canonical JSON object per line (keys sorted ordinally, no whitespace, UTF-8 without BOM, `\n`), sorted by `id`.

## Identity

- Entity: `dotnet:{projectPath}:{documentationCommentId}`; package and framework symbols: `dotnet:pkg:{packageId}@{version}:{assemblyName}:{documentationCommentId}` (framework reference assemblies use the targeting pack as package, for example `Microsoft.NETCore.App.Ref@10.0.14`).
- Symbols without a documentation comment id (locals, lambdas, local functions, anonymous types) are not entities; code inside them is attributed to the containing member (D6).
- Syntax fallback ids: types, fields, properties and events use documentation comment ids built from the syntax; methods add `#syntax({parameter types as written})` and carry `ext.dotnet.syntaxSignature: true`.
- Other records: `{prefix}:{first 32 hex of SHA-256 of the canonical identifying fields}` with prefixes `prj`, `ctx`, `decl`, `rel`, `ev`, `obs`, `lnk`, `diag`, `snip` (D7).
- Ids contain no absolute paths, host data or random parts.

## Locations

- `path`: repository-relative, forward slashes. `store: repository` means the committed file at `blobId`; `store: generation` means the file at that path inside the generation, whose Git blob id is `blobId`.
- Code generated with `#line` mappings (Razor) is reported against the mapped committed file, with the `generator` named.
- `span`: 1-based `startLine`, `startColumn`, `endLine`, `endColumn`; columns in UTF-16 code units (D9). Declarations also have an `extent` (the whole declaration).

## Kinds (v1)

- Relations: `declares`, `contains`, `references` (with `usage`: `read`, `write`, `invocation`, `nameof`, `typeof`, `attribute`, `other`), `calls_declared_target`, `overrides`, `implements`, `inherits`, `possible_runtime_target` (with `declaredTarget`; always `candidate`).
- Observations: `di.registers`, `di.consumes`, `http.endpoint`, `http.call`, `grpc.proto`, `grpc.service`, `grpc.call`, `messaging.publish`, `messaging.subscribe`, `config.key` (never the value), `topology.resource`, `topology.reference`, `data.maps_to`, plus the v1.1 kinds and `call.argument` (v1.2). Every observation has `project`, `context`, at least one evidence id and a `site` (v1.2).
- Links: `consumes_contract` (gRPC, messaging by contract identity, DI), `configured_route` (HTTP through base address and topology), `possible_communication` (messaging contracts that only share a name). `via` says which: `http`, `grpc`, `messaging`, `di`. There is no "observed" kind.
- Resolution: `exact`, `ambiguous`, `candidate`, `unresolved` (unresolved links have `to: null` and a `reason`). A link takes the weakest resolution of its parts. Several request/response targets are `ambiguous`; several message subscribers are fan-out, each keeps its own resolution (D18).
- Routes: `canonicalRoute` has one leading `/`, lower-case literals, parameters as `{}` (optional `{?}`, catch-all `{**}`), no query.
- Diagnostic states: `failed`, `partial`, `unsupported`, `unknown`. Load modes: `semantic`, `syntax`, `failed`; coverage adds `not-analyzed`.

## Contract v1.2 in this engine

- `site` on every observation: the first evidence an adapter passes (the call, the mapping call, the registration, the declaration, the key); when two adapters observe the same fact, the ordinal smaller site is kept. Indexes and cards locate an observation at its site.
- Observations are also validated against `schema/v1/kinds/{kind}.schema.json` when that file exists; `site` must be one of the observation's evidence.
- In-process dispatch (MediatR, C8): `inprocess.send` has the static type of the argument as `request` (definition plus `requestTypeArguments` for a constructed generic), `pattern` and `resolution` (`candidate` for an interface, abstract class, type parameter or `object`). `inprocess.handler`: one per implemented handler interface, `handler` the class, `handlerMethod` the implementing `Handle`; an open generic handler is `candidate` on its constraint, or a diagnostic `open-generic-handler-without-constraint` (unsupported) without one. `dispatches_to` follows the contract's rules 1 to 6; in addition, an exact send without a handler of its own type links as `candidate` to the open generic handlers whose constraint it satisfies (with a reason).
- Call-site arguments (C7): a SignalR send whose method name is a parameter of the containing member gets `destinationParameter`; each call of that member (or of an interface member it implements) gives a `call.argument` with basis `source`. With `--input-generation <dir>`, the templates of another repository's generation apply to calls of its packages (basis `generation`, matched by package id and documentation id). Basis `configured` is not implemented. Inside one repository no link uses `call.argument` (this engine emits no `realtime.subscribe`); a federation does.
- Every context's `projectId` names a project record: files outside every project (D22) belong to the project record of path `.` (`ext.dotnet.scope: repository`). Every entity id an observation names has an entity record (package symbols named only by adapters get theirs from the compilation).
- Package identity: `packageId`, `packageVersion`, `isPackable` come from `msbuild -getProperty`; when evaluation fails a `project-evaluation-failed` diagnostic says why, and a packable project without `PackageId` or `Version` gives `package-identity-not-evaluated`.

## index.sqlite

Index schema 1 (`meta.indexSchema = 1`), normative in the contract since v1.2: [`schema/index/v1/index.sql`](../schema/index/v1/index.sql) (vendored). This engine creates the "base" and "evidence" sections statement for statement (`IndexWriter.Schema`; `Index_schema_matches_the_vendored_contract` compares them). Derived data: built from the NDJSON files of the generation, which stay the source of truth; listed in the manifest like every other file.

| Table | One row per | Notes |
|---|---|---|
| `meta` | key | `schemaVersion`, `indexSchema`, `commit`, `engine` (`name@version`), `status`, `coverage` (canonical JSON of the manifest coverage summary) |
| `entities` | entity | `json` is the canonical record |
| `declarations` | declaration | `line`, `col`: start of the declaration span |
| `evidence` | evidence | span; `text` is the redacted snippet line, when there is one |
| `relations` | relation | `path`, `line`: the first evidence by path, line and column; every evidence in `relation_evidence` |
| `observations` | observation | `path`, `line`: the observation's `site` evidence, else its first evidence by path, line and column; `json` is the canonical record; every evidence in `observation_evidence` |
| `links` | link | `to_obs` is null for unresolved links, which carry `reason`; every evidence in `link_evidence` |
| `diagnostics` | diagnostic | |
| `projects` | project or analysis context | `path` is the project path for both; `json` is the canonical record |
| `search` | entity, snippet or observation (FTS5) | `ref` is the record id, `kind` is `entity`, `snippet` or `observation`; `text` is name, signature and kind (entities), the snippet line (snippets) or the kind and normalized fields (observations) |

Rows are inserted in id order, so two runs write the same file; the comparison that counts is the canonical dump (`query <generation> dump`): every table except the FTS5 shadow tables, by name, each row a canonical JSON array of its column values, rows sorted ordinally, one `table<TAB>row` line each. `verify` checks that `meta.indexSchema` is 1 and that the mirrored tables have one row per record.

## Compatibility rules

1. A consumer accepts major 1 and any minor.
2. An unknown entry in `requiredCapabilities`: refuse the generation.
3. Unknown fields and `ext.*` extensions: preserve without interpreting.
4. Unknown kinds: keep, exclude from traversal and linking, report them (`verify` prints them as notes).
5. Minors only add; existing fields keep their meaning.

## Validation

`analyze` validates its own output before writing the manifest (schema, sorted unique ids, every referenced id, contexts, snippets, generation-stored files against their blob ids, repository evidence against the analysed commit); a failure means no manifest and exit code 50. `verify <generation>` repeats the checks for consumers and adds the manifest schema, capabilities, checksums and unlisted files. Without the repository, `verify` cannot check repository blob ids.
