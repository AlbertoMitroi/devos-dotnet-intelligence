using System.Text.Json.Nodes;
using DotnetIntelligence.Artifacts;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.UnitTests;

/// <summary>
/// A small hand-written generation (graph files only) for the artifact builder: project A calls project B over HTTP.
/// Client.Get (A/Client.cs) calls Api.Handle(int) twice and issues GET /api/items/{}; Api.Handle(int) serves it
/// (B/Api.cs), registered under the route group in B/Program.cs. Api has two Handle overloads.
/// </summary>
internal sealed class SyntheticGeneration : IDisposable
{
    public const string A = "src/A/A.csproj";
    public const string B = "src/B/B.csproj";
    public const string Client = "dotnet:src/A/A.csproj:T:A.Client";
    public const string Get = "dotnet:src/A/A.csproj:M:A.Client.Get";
    public const string Api = "dotnet:src/B/B.csproj:T:B.Api";
    public const string Handle = "dotnet:src/B/B.csproj:M:B.Api.Handle(System.Int32)";
    public const string HandleAll = "dotnet:src/B/B.csproj:M:B.Api.Handle";

    public string Root { get; } = Directory.CreateTempSubdirectory("di-synthetic-").FullName;

    public JsonObject Manifest { get; }

    public SyntheticGeneration(string status = "complete", int endpointCount = 1)
    {
        var evidence = new List<JsonObject>
        {
            Evidence("ev:01", "src/A/Client.cs", 5, "snip:01"),
            Evidence("ev:02", "src/A/Client.cs", 7, "snip:02"),
            Evidence("ev:03", "src/A/Client.cs", 6, "snip:03"),
            Evidence("ev:04", "src/A/appsettings.json", 2, null),
            Evidence("ev:05", "src/B/Api.cs", 9, "snip:05"),
            Evidence("ev:06", "src/B/Program.cs", 3, "snip:06"),
            Evidence("ev:07", "src/B/Program.cs", 4, "snip:07"),
        };
        var snippets = new List<JsonObject>
        {
            Snippet("snip:01", 5, "        var first = api.Handle(1);"),
            Snippet("snip:02", 7, "        return api.Handle(2);"),
            Snippet("snip:03", 6, "        await http.GetAsync($\"api/items/{id}\");"),
            Snippet("snip:05", 9, "    public int Handle(int id) => id;"),
            Snippet("snip:06", 3, "var items = app.MapGroup(\"/api/items\");"),
            Snippet("snip:07", 4, "items.MapGet(\"{id}\", Api.Handle);"),
        };
        var entities = new List<JsonObject>
        {
            Entity(Client, "class", "Client", null, A, "A.Client"),
            Entity(Get, "method", "Get", Client, A, "System.Threading.Tasks.Task<int> A.Client.Get()"),
            Entity(Api, "class", "Api", null, B, "B.Api"),
            Entity(Handle, "method", "Handle", Api, B, "int B.Api.Handle(int id)"),
            Entity(HandleAll, "method", "Handle", Api, B, "int B.Api.Handle()"),
        };
        var declarations = new List<JsonObject>
        {
            Declaration("decl:01", Client, "ctx:a", "src/A/Client.cs", 3, 12),
            Declaration("decl:02", Get, "ctx:a", "src/A/Client.cs", 4, 8),
            Declaration("decl:03", Api, "ctx:b", "src/B/Api.cs", 7, 12),
            Declaration("decl:04", Handle, "ctx:b", "src/B/Api.cs", 9, 9),
            Declaration("decl:05", HandleAll, "ctx:b", "src/B/Api.cs", 10, 10),
        };
        var relations = new List<JsonObject>
        {
            Relation("rel:01", "calls_declared_target", Get, Handle, "ctx:a", ["ev:01", "ev:02"]),
            Relation("rel:02", "references", Get, Api, "ctx:a", ["ev:02", "ev:01"], usage: "read"),
            Relation("rel:03", "contains", Client, Get, "ctx:a", ["ev:01"]),
            Relation("rel:04", "overrides", Handle, HandleAll, "ctx:b", ["ev:05"]),
        };
        var observations = new List<JsonObject>
        {
            new()
            {
                ["id"] = "obs:01", ["kind"] = "http.call", ["project"] = A, ["context"] = "ctx:a", ["method"] = "GET",
                ["canonicalRoute"] = "/api/items/{}", ["urlTemplate"] = "api/items/{}", ["caller"] = Get, ["client"] = null,
                ["resolution"] = "exact", ["evidence"] = Ids("ev:03", "ev:04"), ["site"] = "ev:03",
            },
        };
        observations.Add(new JsonObject
        {
            ["id"] = "obs:d01", ["kind"] = "di.registers", ["project"] = B, ["context"] = "ctx:b", ["service"] = Api, ["implementation"] = Api,
            ["lifetime"] = "scoped", ["conditional"] = false, ["evidence"] = Ids("ev:06"), ["site"] = "ev:06",
        });
        observations.Add(new JsonObject
        {
            ["id"] = "obs:m01", ["kind"] = "messaging.publish", ["project"] = A, ["context"] = "ctx:a", ["publisher"] = Get, ["resolution"] = "exact",
            ["contract"] = new JsonObject { ["identity"] = "name:OrderPlaced", ["name"] = "OrderPlaced" }, ["evidence"] = Ids("ev:01"), ["site"] = "ev:01",
        });
        for (var i = 0; i < endpointCount; i++)
        {
            observations.Add(new JsonObject
            {
                ["id"] = $"obs:e{i:D3}", ["kind"] = "http.endpoint", ["project"] = B, ["context"] = "ctx:b", ["method"] = "GET",
                ["canonicalRoute"] = i == 0 ? "/api/items/{}" : $"/api/other{i:D3}", ["routeTemplate"] = "{id}", ["handler"] = i == 0 ? Handle : null,
                ["resolution"] = "exact", ["evidence"] = Ids("ev:06", "ev:07"), ["site"] = "ev:07",
            });
        }
        var links = new List<JsonObject>
        {
            new()
            {
                ["id"] = "lnk:01", ["kind"] = "configured_route", ["via"] = "http", ["resolution"] = "candidate",
                ["from"] = new JsonObject { ["observation"] = "obs:01", ["kind"] = "http.call", ["project"] = A },
                ["to"] = new JsonObject { ["observation"] = "obs:e000", ["kind"] = "http.endpoint", ["project"] = B },
                ["evidence"] = Ids("ev:03", "ev:04", "ev:06", "ev:07"),
            },
        };
        var projects = new List<JsonObject>
        {
            Project("prj:a", A, [B]),
            Project("prj:b", B, []),
            Context("ctx:a", A),
            Context("ctx:b", B),
        };
        var diagnostics = new List<JsonObject>
        {
            new() { ["id"] = "diag:01", ["severity"] = "warning", ["code"] = "example-gap", ["scope"] = B, ["state"] = "partial", ["message"] = "Something in B could not be read." },
        };
        Write("graph/projects.ndjson", projects);
        Write("graph/entities.ndjson", entities);
        Write("graph/declarations.ndjson", declarations);
        Write("graph/relations.ndjson", relations);
        Write("graph/evidence.ndjson", evidence);
        Write("graph/observations.ndjson", observations);
        Write("graph/links.ndjson", links);
        Write("graph/diagnostics.ndjson", diagnostics);
        Write("snippets.ndjson", snippets);
        Manifest = new JsonObject
        {
            ["schemaVersion"] = "1.1",
            ["status"] = status,
            ["commit"] = "0123456789abcdef0123456789abcdef01234567",
            ["dirty"] = false,
            ["engine"] = new JsonObject { ["name"] = "dotnet-intelligence", ["version"] = "0.0.0-test" },
            ["roslyn"] = new JsonObject { ["apiVersion"] = "5.9.0" },
            ["solutions"] = new JsonArray(new JsonObject { ["path"] = "All.slnx", ["format"] = "slnx" }),
            ["coverage"] = new JsonObject { ["summary"] = new JsonObject { ["total"] = 2, ["semantic"] = 1, ["syntax"] = 1, ["failed"] = 0, ["notAnalyzed"] = 0 } },
        };
    }

    /// <summary>Builds index.sqlite in the generation (or at <paramref name="path"/>) and returns its path.</summary>
    public string BuildIndex(string? path = null)
    {
        path ??= Path.Combine(Root, IndexWriter.FileName);
        IndexWriter.Write(Root, path, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "1.1",
            ["indexSchema"] = IndexWriter.SchemaVersion,
            ["commit"] = (string)Manifest["commit"]!,
            ["engine"] = "dotnet-intelligence@0.0.0-test",
            ["status"] = (string)Manifest["status"]!,
            ["coverage"] = CanonicalJson.Serialize(Manifest["coverage"]!["summary"]),
        });
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private void Write(string file, List<JsonObject> records)
    {
        var path = Path.Combine(Root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        CanonicalJson.WriteNdjson(stream, records.Select(r => (JsonObject)ContractIds(r)!).OrderBy(r => (string)r["id"]!, StringComparer.Ordinal));
    }

    /// <summary>Short record ids ("obs:01") become contract-shaped ids ("obs:" and 32 hex), the same everywhere they occur.</summary>
    private static JsonNode? ContractIds(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, ContractIds(p.Value)))),
        JsonArray array => new JsonArray(array.Select(ContractIds).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) && System.Text.RegularExpressions.Regex.IsMatch(text, "^(ev|obs|lnk|ctx|prj|snip|rel|decl|diag):[0-9a-z]{1,8}$")
            => JsonValue.Create(text[..(text.IndexOf(':') + 1)] + Hashing.Sha256Hex(text)[..32]),
        _ => node?.DeepClone(),
    };

    private static JsonArray Ids(params string[] ids) => new(ids.Select(i => (JsonNode)i).ToArray());

    private static JsonObject Span(int line) => new() { ["startLine"] = line, ["startColumn"] = 1, ["endLine"] = line, ["endColumn"] = 20 };

    private static JsonObject Evidence(string id, string path, int line, string? snippet) => new()
    {
        ["id"] = id, ["path"] = path, ["blobId"] = "0000000000000000000000000000000000000000", ["span"] = Span(line), ["extractor"] = "test@0",
        ["method"] = "semantic", ["context"] = path.StartsWith("src/A", StringComparison.Ordinal) ? "ctx:a" : "ctx:b", ["store"] = "repository", ["snippet"] = snippet,
    };

    private static JsonObject Snippet(string id, int line, string text) => new() { ["id"] = id, ["blobId"] = "0000000000000000000000000000000000000000", ["line"] = line, ["text"] = text };

    private static JsonObject Entity(string id, string kind, string name, string? container, string project, string signature) => new()
    {
        ["id"] = id, ["kind"] = kind, ["name"] = name, ["container"] = container, ["project"] = project, ["signature"] = signature,
        ["accessibility"] = "public", ["modifiers"] = new JsonArray(), ["origin"] = "source", ["documentationId"] = id[(id.IndexOf(".csproj:", StringComparison.Ordinal) + 8)..],
    };

    private static JsonObject Declaration(string id, string entity, string context, string path, int start, int end) => new()
    {
        ["id"] = id, ["entity"] = entity, ["context"] = context, ["path"] = path, ["blobId"] = "0000000000000000000000000000000000000000",
        ["span"] = Span(start), ["extent"] = new JsonObject { ["startLine"] = start, ["startColumn"] = 1, ["endLine"] = end, ["endColumn"] = 2 },
    };

    private static JsonObject Relation(string id, string kind, string from, string to, string context, string[] evidence, string? usage = null)
    {
        var relation = new JsonObject { ["id"] = id, ["kind"] = kind, ["from"] = from, ["to"] = to, ["context"] = context, ["resolution"] = "exact", ["evidence"] = Ids(evidence) };
        if (usage is not null)
        {
            relation["usage"] = usage;
        }
        return relation;
    }

    private static JsonObject Project(string id, string path, string[] references) => new()
    {
        ["id"] = id, ["recordType"] = "project", ["path"] = path, ["assemblyName"] = Path.GetFileNameWithoutExtension(path),
        ["targetFrameworks"] = new JsonArray("net10.0"), ["projectReferences"] = new JsonArray(references.Select(r => (JsonNode)r).ToArray()),
        ["packages"] = new JsonArray(), ["solutions"] = new JsonArray("All.slnx"),
    };

    private static JsonObject Context(string id, string project) => new()
    {
        ["id"] = id, ["recordType"] = "analysisContext", ["project"] = project, ["loadMode"] = "semantic", ["reason"] = "loaded", ["targetFramework"] = "net10.0",
    };
}
