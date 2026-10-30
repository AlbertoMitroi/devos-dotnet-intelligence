using System.Globalization;
using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;
using Microsoft.Data.Sqlite;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// index.sqlite (5.2): tables mirror the contract records, with indexes in both directions (entity to usages,
/// location to entity, callers and callees, link endpoints) and FTS5 over names, signatures, snippets and normalized
/// observation fields. Derived data: it is built from the NDJSON files of the generation, which stay the source of
/// truth. The tables and columns shared with the other engines are documented in docs/CONTRACT.md (index schema 1).
/// </summary>
public static class IndexWriter
{
    public const string FileName = "index.sqlite";

    /// <summary>Version of the index schema, stored in meta.indexSchema; bumped when a table or column changes.</summary>
    public const string SchemaVersion = "1";

    /// <summary>
    /// The DDL in creation order: the "base" and "evidence" sections of the contract's schema/index/v1/index.sql
    /// (index schema 1, normative since contract v1.2), statement for statement.
    /// </summary>
    public static readonly IReadOnlyList<string> Schema =
    [
        "create table meta(key text primary key, value text not null)",
        "create table entities(id text primary key, kind text, name text, container text, signature text, project text, origin text, json text)",
        "create table declarations(id text primary key, entity text, context text, path text, line integer, col integer)",
        "create table evidence(id text primary key, path text, line integer, col integer, end_line integer, end_col integer, method text, context text, text text)",
        "create table relations(id text primary key, kind text, from_id text, to_id text, usage text, resolution text, context text, path text, line integer, declared_target text)",
        "create table observations(id text primary key, kind text, project text, path text, line integer, json text)",
        "create table links(id text primary key, kind text, via text, from_obs text, to_obs text, resolution text, reason text)",
        "create table diagnostics(id text primary key, severity text, code text, scope text, state text, message text)",
        "create table projects(id text primary key, record_type text, path text, json text)",
        "create index relations_from on relations(from_id, kind)",
        "create index relations_to on relations(to_id, kind)",
        "create index declarations_entity on declarations(entity)",
        "create index declarations_location on declarations(path, line)",
        "create index entities_name on entities(name)",
        "create index observations_kind on observations(kind)",
        "create index links_from on links(from_obs)",
        "create index links_to on links(to_obs)",
        "create virtual table search using fts5(ref unindexed, kind unindexed, text)",
        "create table relation_evidence(relation text, evidence text, primary key (relation, evidence))",
        "create table observation_evidence(observation text, evidence text, primary key (observation, evidence))",
        "create table link_evidence(link text, evidence text, primary key (link, evidence))",
        "create table evidence_snippet(evidence text primary key, snippet text)",
        "create index evidence_location on evidence(path, line)",
        "create index relation_evidence_evidence on relation_evidence(evidence)",
        "create index observation_evidence_evidence on observation_evidence(evidence)",
        "create index evidence_snippet_snippet on evidence_snippet(snippet)",
    ];

    /// <summary>SQLite version and FTS5 support of the bundled library (checked at startup, printed by version --json).</summary>
    public static (string Version, bool Fts5) SqliteInfo()
    {
        using var connection = Open(":memory:", readOnly: false);
        var version = (string)Scalar(connection, "select sqlite_version()")!;
        bool fts5;
        try
        {
            Execute(connection, "create virtual table probe using fts5(x)");
            fts5 = true;
        }
        catch (SqliteException)
        {
            fts5 = false;
        }
        return (version, fts5);
    }

    /// <summary>Builds index.sqlite at <paramref name="indexPath"/> from the NDJSON records of the generation at <paramref name="root"/>.</summary>
    public static void Write(string root, string indexPath, IReadOnlyDictionary<string, string> meta)
    {
        if (File.Exists(indexPath))
        {
            throw new IOException($"'{indexPath}' already exists.");
        }
        var entities = Read(root, "graph/entities.ndjson");
        var declarations = Read(root, "graph/declarations.ndjson");
        var evidence = Read(root, "graph/evidence.ndjson");
        var relations = Read(root, "graph/relations.ndjson");
        var observations = Read(root, "graph/observations.ndjson");
        var links = Read(root, "graph/links.ndjson");
        var diagnostics = Read(root, "graph/diagnostics.ndjson");
        var projects = Read(root, "graph/projects.ndjson");
        var snippets = Read(root, "snippets.ndjson");

        var evidenceById = evidence.ToDictionary(e => (string)e["id"]!, StringComparer.Ordinal);
        var locations = new Locations(evidenceById);
        var snippetText = snippets.ToDictionary(s => (string)s["id"]!, s => (string?)s["text"], StringComparer.Ordinal);

        using var connection = Open(indexPath, readOnly: false);
        Execute(connection, "pragma page_size = 4096");
        Execute(connection, "pragma journal_mode = off");
        Execute(connection, "pragma synchronous = off");
        foreach (var statement in Schema)
        {
            Execute(connection, statement);
        }
        using var transaction = connection.BeginTransaction();

        var insertMeta = Prepare(connection, transaction, "insert into meta values ($1, $2)", 2);
        foreach (var (key, value) in meta.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            Run(insertMeta, key, value);
        }

        var insertEntity = Prepare(connection, transaction, "insert into entities values ($1, $2, $3, $4, $5, $6, $7, $8)", 8);
        var insertSearch = Prepare(connection, transaction, "insert into search values ($1, $2, $3)", 3);
        foreach (var e in entities)
        {
            Run(insertEntity, e["id"], e["kind"], e["name"], e["container"], e["signature"], e["project"], e["origin"], CanonicalJson.Serialize(e));
            Run(insertSearch, e["id"], "entity", $"{e["name"]} {e["signature"]} {e["kind"]}");
        }

        var insertDeclaration = Prepare(connection, transaction, "insert into declarations values ($1, $2, $3, $4, $5, $6)", 6);
        foreach (var d in declarations)
        {
            Run(insertDeclaration, d["id"], d["entity"], d["context"], d["path"], d["span"]!["startLine"], d["span"]!["startColumn"]);
        }

        var insertEvidence = Prepare(connection, transaction, "insert into evidence values ($1, $2, $3, $4, $5, $6, $7, $8, $9)", 9);
        var insertEvidenceSnippet = Prepare(connection, transaction, "insert into evidence_snippet values ($1, $2)", 2);
        foreach (var e in evidence)
        {
            var span = e["span"]!;
            var snippet = (string?)e["snippet"];
            Run(insertEvidence, e["id"], e["path"], span["startLine"], span["startColumn"], span["endLine"], span["endColumn"], e["method"], e["context"],
                snippet is null ? null : snippetText.GetValueOrDefault(snippet));
            if (snippet is not null)
            {
                Run(insertEvidenceSnippet, e["id"], snippet);
            }
        }
        foreach (var s in snippets)
        {
            Run(insertSearch, s["id"], "snippet", s["text"]);
        }

        var insertRelation = Prepare(connection, transaction, "insert into relations values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)", 10);
        var insertRelationEvidence = Prepare(connection, transaction, "insert into relation_evidence values ($1, $2)", 2);
        foreach (var r in relations)
        {
            var ids = EvidenceIds(r);
            var first = locations.FirstOf(r);
            Run(insertRelation, r["id"], r["kind"], r["from"], r["to"], r["usage"], r["resolution"], r["context"], first?["path"], first?["span"]!["startLine"], r["declaredTarget"]);
            foreach (var id in ids)
            {
                Run(insertRelationEvidence, r["id"], id);
            }
        }

        var insertObservation = Prepare(connection, transaction, "insert into observations values ($1, $2, $3, $4, $5, $6)", 6);
        var insertObservationEvidence = Prepare(connection, transaction, "insert into observation_evidence values ($1, $2)", 2);
        foreach (var o in observations)
        {
            var ids = EvidenceIds(o);
            var first = locations.SiteOf(o);
            Run(insertObservation, o["id"], o["kind"], o["project"], first?["path"], first?["span"]!["startLine"], CanonicalJson.Serialize(o));
            foreach (var id in ids)
            {
                Run(insertObservationEvidence, o["id"], id);
            }
            if (SearchText(o) is { } text)
            {
                Run(insertSearch, o["id"], "observation", text);
            }
        }

        var insertLink = Prepare(connection, transaction, "insert into links values ($1, $2, $3, $4, $5, $6, $7)", 7);
        var insertLinkEvidence = Prepare(connection, transaction, "insert into link_evidence values ($1, $2)", 2);
        foreach (var l in links)
        {
            Run(insertLink, l["id"], l["kind"], l["via"], l["from"]!["observation"], l["to"]?["observation"], l["resolution"], l["reason"]);
            foreach (var id in EvidenceIds(l))
            {
                Run(insertLinkEvidence, l["id"], id);
            }
        }

        var insertDiagnostic = Prepare(connection, transaction, "insert into diagnostics values ($1, $2, $3, $4, $5, $6)", 6);
        foreach (var d in diagnostics)
        {
            Run(insertDiagnostic, d["id"], d["severity"], d["code"], d["scope"], d["state"], d["message"]);
        }

        var insertProject = Prepare(connection, transaction, "insert into projects values ($1, $2, $3, $4)", 4);
        foreach (var p in projects)
        {
            Run(insertProject, p["id"], p["recordType"], p["path"] ?? p["project"], CanonicalJson.Serialize(p));
        }
        transaction.Commit();
    }

    /// <summary>
    /// The text indexed for an observation: its kind and the normalized fields a person searches for (routes, methods,
    /// keys, contract and service names, destinations). Entity ids are reduced to their documentation id.
    /// </summary>
    internal static string? SearchText(JsonObject observation)
    {
        var parts = new List<string> { (string)observation["kind"]! };
        foreach (var field in SearchFields)
        {
            switch (observation[field])
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    parts.Add(ShortId(text));
                    break;
                case JsonObject nested:
                    foreach (var inner in new[] { "name", "fullName", "identity" })
                    {
                        if ((string?)(nested[inner] as JsonValue) is { } text2)
                        {
                            parts.Add(text2);
                        }
                    }
                    break;
            }
        }
        return parts.Count > 1 ? string.Join(' ', parts) : null;
    }

    private static readonly string[] SearchFields =
    [
        "method", "canonicalRoute", "routeTemplate", "urlTemplate", "key", "service", "implementation", "handler", "caller", "client",
        "contract", "destination", "name", "processId", "nodeType", "table", "index", "request", "hub", "proto", "targetProject", "to", "from",
    ];

    /// <summary>"dotnet:{project}:T:A.B" gives "T:A.B"; other values stay as they are.</summary>
    internal static string ShortId(string value)
    {
        if (!value.StartsWith("dotnet:", StringComparison.Ordinal))
        {
            return value;
        }
        var csproj = value.IndexOf(".csproj:", StringComparison.Ordinal);
        if (csproj > 0)
        {
            return value[(csproj + ".csproj:".Length)..];
        }
        var colon = value.LastIndexOf(':');
        return colon >= 1 ? value[(colon - 1)..] : value;
    }

    private static List<string> EvidenceIds(JsonObject record) =>
        (record["evidence"]?.AsArray() ?? []).Select(e => (string)e!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    internal static List<JsonObject> Read(string root, string file)
    {
        var path = Path.Combine(root, file);
        return File.Exists(path)
            ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList()
            : [];
    }

    internal static SqliteConnection Open(string path, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // No pooling: the file must be closed (and complete) when the connection is disposed, before it is hashed.
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SqliteCommand Prepare(SqliteConnection connection, SqliteTransaction transaction, string sql, int parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 1; i <= parameters; i++)
        {
            command.Parameters.Add(new SqliteParameter("$" + i.ToString(CultureInfo.InvariantCulture), null));
        }
        command.Prepare();
        return command;
    }

    private static void Run(SqliteCommand command, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters[i].Value = values[i] switch
            {
                null => DBNull.Value,
                JsonValue v when v.TryGetValue<int>(out var number) => number,
                JsonValue v when v.TryGetValue<long>(out var number) => number,
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                JsonNode node => node.ToJsonString(),
                var other => other,
            };
        }
        command.ExecuteNonQuery();
    }
}
