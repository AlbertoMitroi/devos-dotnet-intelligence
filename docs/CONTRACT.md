# Contract v1

The format of a generation. Technology-neutral fields carry everything a consumer needs; .NET detail travels under `ext.dotnet.*`. Schemas: [`schema/v1/`](../schema/v1/). `schemaVersion` in the manifest: `1.0`.

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
- Observations: `di.registers`, `di.consumes`, `http.endpoint`, `http.call`, `grpc.proto`, `grpc.service`, `grpc.call`, `messaging.publish`, `messaging.subscribe`, `config.key` (never the value), `topology.resource`, `topology.reference`, `data.maps_to`. Every observation has `project`, `context` and at least one evidence id.
- Links: `consumes_contract` (gRPC, messaging by contract identity, DI), `configured_route` (HTTP through base address and topology), `possible_communication` (messaging contracts that only share a name). `via` says which: `http`, `grpc`, `messaging`, `di`. There is no "observed" kind.
- Resolution: `exact`, `ambiguous`, `candidate`, `unresolved` (unresolved links have `to: null` and a `reason`). A link takes the weakest resolution of its parts. Several request/response targets are `ambiguous`; several message subscribers are fan-out, each keeps its own resolution (D18).
- Routes: `canonicalRoute` has one leading `/`, lower-case literals, parameters as `{}` (optional `{?}`, catch-all `{**}`), no query.
- Diagnostic states: `failed`, `partial`, `unsupported`, `unknown`. Load modes: `semantic`, `syntax`, `failed`; coverage adds `not-analyzed`.

## index.sqlite

Index schema 1 (`meta.indexSchema = 1`). Derived data: built from the NDJSON files of the generation, which stay the source of truth; listed in the manifest like every other file. The tables up to `projects` and the `search` table have the same names and columns in angular-intelligence (shared definition, proposed for the contract); `relation_evidence`, `observation_evidence`, `link_evidence` and `evidence_snippet` are additions of this engine, proposed for the shared definition.

<!-- index-schema:begin -->
```sql
create table meta(key text primary key, value text not null);
create table entities(id text primary key, kind text, name text, container text, signature text, project text, origin text, json text);
create table declarations(id text primary key, entity text, context text, path text, line integer, col integer);
create table evidence(id text primary key, path text, line integer, col integer, end_line integer, end_col integer, method text, context text, text text);
create table relations(id text primary key, kind text, from_id text, to_id text, usage text, resolution text, context text, path text, line integer, declared_target text);
create table observations(id text primary key, kind text, project text, path text, line integer, json text);
create table links(id text primary key, kind text, via text, from_obs text, to_obs text, resolution text, reason text);
create table diagnostics(id text primary key, severity text, code text, scope text, state text, message text);
create table projects(id text primary key, record_type text, path text, json text);
create table relation_evidence(relation text, evidence text, primary key (relation, evidence));
create table observation_evidence(observation text, evidence text, primary key (observation, evidence));
create table link_evidence(link text, evidence text, primary key (link, evidence));
create table evidence_snippet(evidence text primary key, snippet text);
create index relations_from on relations(from_id, kind);
create index relations_to on relations(to_id, kind);
create index declarations_entity on declarations(entity);
create index declarations_location on declarations(path, line);
create index entities_name on entities(name);
create index observations_kind on observations(kind);
create index links_from on links(from_obs);
create index links_to on links(to_obs);
create index evidence_location on evidence(path, line);
create index relation_evidence_evidence on relation_evidence(evidence);
create index observation_evidence_evidence on observation_evidence(evidence);
create index evidence_snippet_snippet on evidence_snippet(snippet);
create virtual table search using fts5(ref unindexed, kind unindexed, text);
```
<!-- index-schema:end -->

| Table | One row per | Notes |
|---|---|---|
| `meta` | key | `schemaVersion`, `indexSchema`, `commit`, `engine` (`name@version`), `status`, `coverage` (canonical JSON of the manifest coverage summary) |
| `entities` | entity | `json` is the canonical record |
| `declarations` | declaration | `line`, `col`: start of the declaration span |
| `evidence` | evidence | span; `text` is the redacted snippet line, when there is one |
| `relations` | relation | `path`, `line`: the first evidence by path, line and column; every evidence in `relation_evidence` |
| `observations` | observation | `path`, `line`: the site (evidence inside a declaration of the entity the observation names, else the last evidence in source code, else the first); `json` is the canonical record; every evidence in `observation_evidence` |
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
