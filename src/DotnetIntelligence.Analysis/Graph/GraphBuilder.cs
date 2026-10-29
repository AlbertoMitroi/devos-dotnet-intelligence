using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Analysis.Graph;

public sealed record SourceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public JsonObject ToJson() => new()
    {
        ["startLine"] = StartLine,
        ["startColumn"] = StartColumn,
        ["endLine"] = EndLine,
        ["endColumn"] = EndColumn,
    };
}

/// <summary>Where a piece of evidence lives: a committed file, or a generated file stored in the generation.</summary>
public sealed record SourceLocation(string Path, string BlobId, SourceSpan Span, string Store, string? Generator);

/// <summary>
/// Thread-safe accumulator for graph records. Every record has a content-derived id (D7); output order is the
/// ordinal order of ids, so parallel extraction produces byte-identical files.
/// </summary>
public sealed class GraphBuilder(string extractor, SecretScrubber scrubber)
{
    private readonly ConcurrentDictionary<string, JsonObject> _entities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _declarations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _evidence = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _snippets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RelationAccumulator> _relations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _observations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _generatedFiles = new(StringComparer.Ordinal);

    public string Extractor { get; } = extractor;

    public bool HasEntity(string id) => _entities.ContainsKey(id);

    public void AddEntity(string id, Func<JsonObject> create) => _entities.GetOrAdd(id, _ => { var e = create(); e["id"] = id; return e; });

    public IEnumerable<JsonObject> Entities => _entities.Values;

    public IEnumerable<JsonObject> Relations => _relations.Values.Select(r => r.ToJson());

    public IEnumerable<JsonObject> Declarations => _declarations.Values;

    public IEnumerable<JsonObject> Evidence => _evidence.Values;

    public IEnumerable<JsonObject> Snippets => _snippets.Values;

    public IEnumerable<JsonObject> Observations => _observations.Values;

    public IReadOnlyDictionary<string, byte[]> GeneratedFiles => _generatedFiles;

    public void AddGeneratedFile(string path, byte[] content) => _generatedFiles.TryAdd(path, content);

    /// <summary>Records evidence and its redacted snippet line; returns the evidence id.</summary>
    public string AddEvidence(SourceLocation location, string method, string context, string? lineText)
    {
        var identity = new JsonObject
        {
            ["blobId"] = location.BlobId,
            ["context"] = context,
            ["extractor"] = Extractor,
            ["method"] = method,
            ["path"] = location.Path,
            ["span"] = location.Span.ToJson(),
            ["store"] = location.Store,
        };
        var id = Hashing.RecordId("ev", identity);
        _evidence.GetOrAdd(id, _ =>
        {
            var record = (JsonObject)identity.DeepClone();
            record["id"] = id;
            if (location.Generator is not null)
            {
                record["generator"] = location.Generator;
            }
            if (lineText is not null)
            {
                var snippetId = Hashing.RecordId("snip", new JsonObject { ["blobId"] = location.BlobId, ["line"] = location.Span.StartLine });
                record["snippet"] = snippetId;
                _snippets.TryAdd(snippetId, new JsonObject
                {
                    ["id"] = snippetId,
                    ["blobId"] = location.BlobId,
                    ["line"] = location.Span.StartLine,
                    ["text"] = Truncate(scrubber.RedactSnippet(lineText.TrimEnd()), 300),
                });
            }
            return record;
        });
        return id;
    }

    public void AddDeclaration(string entity, string context, SourceLocation location, SourceSpan extent)
    {
        var identity = new JsonObject
        {
            ["context"] = context,
            ["entity"] = entity,
            ["path"] = location.Path,
            ["span"] = location.Span.ToJson(),
        };
        var id = Hashing.RecordId("decl", identity);
        _declarations.GetOrAdd(id, _ =>
        {
            var record = (JsonObject)identity.DeepClone();
            record["id"] = id;
            record["blobId"] = location.BlobId;
            record["extent"] = extent.ToJson();
            record["store"] = location.Store;
            if (location.Generator is not null)
            {
                record["generator"] = location.Generator;
            }
            return record;
        });
    }

    /// <summary>Adds a relation or merges evidence into an existing one with the same identity.</summary>
    public void AddRelation(string kind, string from, string to, string context, string resolution, string evidenceId, JsonObject? fields = null)
    {
        var identity = new JsonObject
        {
            ["context"] = context,
            ["from"] = from,
            ["kind"] = kind,
            ["resolution"] = resolution,
            ["to"] = to,
        };
        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                identity[key] = value?.DeepClone();
            }
        }
        var id = Hashing.RecordId("rel", identity);
        var accumulator = _relations.GetOrAdd(id, _ => new RelationAccumulator(id, identity));
        accumulator.Add(evidenceId);
    }

    public void AddObservation(string kind, JsonObject fields, IEnumerable<string> evidenceIds)
    {
        var identity = (JsonObject)fields.DeepClone();
        identity["kind"] = kind;
        var id = Hashing.RecordId("obs", identity);
        _observations.AddOrUpdate(id,
            _ =>
            {
                var record = (JsonObject)identity.DeepClone();
                record["id"] = id;
                record["evidence"] = new JsonArray(evidenceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(e => (JsonNode)e).ToArray());
                return record;
            },
            (_, existing) =>
            {
                var merged = existing["evidence"]!.AsArray().Select(e => (string)e!).Concat(evidenceIds)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(e => (JsonNode)e).ToArray();
                var record = (JsonObject)existing.DeepClone();
                record["evidence"] = new JsonArray(merged);
                return record;
            });
    }

    /// <summary>Secret redaction for short texts the engine stores (for example BPMN conditions).</summary>
    public string Redact(string text) => scrubber.RedactSnippet(text);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private sealed class RelationAccumulator(string id, JsonObject identity)
    {
        private readonly ConcurrentDictionary<string, byte> _evidence = new(StringComparer.Ordinal);

        public void Add(string evidenceId) => _evidence.TryAdd(evidenceId, 0);

        public JsonObject ToJson()
        {
            var record = (JsonObject)identity.DeepClone();
            record["id"] = id;
            record["evidence"] = new JsonArray(_evidence.Keys.Order(StringComparer.Ordinal).Select(e => (JsonNode)e).ToArray());
            return record;
        }
    }
}
