using System.Globalization;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// The records of a generation as the context packs and the wiki read them (5.3, 5.4): loaded from the NDJSON files,
/// with the location of every record (its first evidence by path, line and column) and a service view per project.
/// Nothing here interprets source code; every fact comes from a record and keeps its evidence.
/// </summary>
public sealed class ServiceModel
{
    public required JsonObject Manifest { get; init; }
    public required IReadOnlyList<JsonObject> Projects { get; init; }
    public required IReadOnlyDictionary<string, JsonObject> ContextByProject { get; init; }
    public required IReadOnlyDictionary<string, JsonObject> Entities { get; init; }
    public required IReadOnlyDictionary<string, List<JsonObject>> DeclarationsByEntity { get; init; }
    public required IReadOnlyList<JsonObject> Relations { get; init; }
    public required IReadOnlyDictionary<string, JsonObject> Evidence { get; init; }
    public required IReadOnlyList<JsonObject> Observations { get; init; }
    public required IReadOnlyDictionary<string, JsonObject> ObservationById { get; init; }
    public required IReadOnlyList<JsonObject> Links { get; init; }
    public required IReadOnlyList<JsonObject> Diagnostics { get; init; }

    public static ServiceModel Load(string root, JsonObject manifest)
    {
        var projectRecords = IndexWriter.Read(root, "graph/projects.ndjson");
        var observations = IndexWriter.Read(root, "graph/observations.ndjson");
        return new ServiceModel
        {
            Manifest = manifest,
            Projects = projectRecords.Where(p => (string?)p["recordType"] == "project").OrderBy(p => (string)p["path"]!, StringComparer.Ordinal).ToList(),
            ContextByProject = projectRecords.Where(p => (string?)p["recordType"] == "analysisContext" && p["project"] is not null)
                .GroupBy(p => (string)p["project"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(c => (string)c["id"]!, StringComparer.Ordinal).First(), StringComparer.Ordinal),
            Entities = IndexWriter.Read(root, "graph/entities.ndjson").ToDictionary(e => (string)e["id"]!, StringComparer.Ordinal),
            DeclarationsByEntity = IndexWriter.Read(root, "graph/declarations.ndjson")
                .GroupBy(d => (string)d["entity"]!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(d => (string)d["path"]!, StringComparer.Ordinal).ThenBy(d => (int)d["span"]!["startLine"]!).ToList(), StringComparer.Ordinal),
            Relations = IndexWriter.Read(root, "graph/relations.ndjson"),
            Evidence = IndexWriter.Read(root, "graph/evidence.ndjson").ToDictionary(e => (string)e["id"]!, StringComparer.Ordinal),
            Observations = observations,
            ObservationById = observations.ToDictionary(o => (string)o["id"]!, StringComparer.Ordinal),
            Links = IndexWriter.Read(root, "graph/links.ndjson"),
            Diagnostics = IndexWriter.Read(root, "graph/diagnostics.ndjson"),
        };
    }

    private Locations? _locations;

    /// <summary>"path:line" of a record: an observation's site, otherwise its first evidence (see <see cref="Locations"/>).</summary>
    public string At(JsonObject record)
    {
        _locations ??= new Locations(Evidence, DeclarationsByEntity);
        return Locations.Format(((string?)record["id"])?.StartsWith("obs:", StringComparison.Ordinal) == true ? _locations.SiteOf(record) : _locations.FirstOf(record));
    }

    /// <summary>"path:line" of an entity's first declaration, or null.</summary>
    public string? DeclaredAt(string entityId) =>
        DeclarationsByEntity.TryGetValue(entityId, out var list) && list.Count > 0
            ? $"{list[0]["path"]}:{((int)list[0]["span"]!["startLine"]!).ToString(CultureInfo.InvariantCulture)}"
            : null;

    public IEnumerable<JsonObject> Of(string kind, string? project = null) =>
        Observations.Where(o => (string)o["kind"]! == kind && (project is null || (string?)o["project"] == project))
            .OrderBy(At, StringComparer.Ordinal).ThenBy(o => (string)o["id"]!, StringComparer.Ordinal);

    public IEnumerable<JsonObject> LinksFrom(string observationId) =>
        Links.Where(l => (string?)l["from"]!["observation"] == observationId).OrderBy(l => (string?)l["to"]?["observation"] ?? "~", StringComparer.Ordinal);

    public IEnumerable<JsonObject> LinksTo(string observationId) =>
        Links.Where(l => (string?)l["to"]?["observation"] == observationId).OrderBy(l => (string)l["from"]!["observation"]!, StringComparer.Ordinal);

    /// <summary>
    /// Project kind from records only: test (references Microsoft.NET.Test.Sdk), service (serves HTTP or gRPC
    /// endpoints, realtime hubs or message subscriptions), host (an entry point or an Aspire project resource), library.
    /// </summary>
    private readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);

    public string KindOf(JsonObject project)
    {
        var key = (string)project["path"]!;
        if (!_kinds.TryGetValue(key, out var kind))
        {
            _kinds[key] = kind = ComputeKind(project);
        }
        return kind;
    }

    private string ComputeKind(JsonObject project)
    {
        var path = (string)project["path"]!;
        if ((project["packages"]?.AsArray() ?? []).Any(p => (string?)p!["id"] == "Microsoft.NET.Test.Sdk"))
        {
            return "test";
        }
        if (Observations.Any(o => (string?)o["project"] == path && (string)o["kind"]! is "http.endpoint" or "grpc.service" or "realtime.hub" or "messaging.subscribe"))
        {
            return "service";
        }
        if (EntryPoints(path).Any() || Observations.Any(o => (string)o["kind"]! == "topology.resource" && (string?)o["targetProject"] == path))
        {
            return "host";
        }
        return "library";
    }

    public IEnumerable<JsonObject> EntryPoints(string projectPath) =>
        Entities.Values.Where(e => (string?)e["project"] == projectPath && (string?)e["kind"] == "method"
                && (((string?)e["documentationId"])?.Contains("{Main}$", StringComparison.Ordinal) == true
                    || (string?)e["name"] == "Main" && (e["modifiers"]?.AsArray() ?? []).Any(m => (string?)m == "static")))
            .OrderBy(e => (string)e["id"]!, StringComparer.Ordinal);

    /// <summary>Projects that a service page is written for: services, hosts and every project on either side of a link between projects.</summary>
    public IReadOnlyList<JsonObject> ServiceProjects()
    {
        var linked = ServiceEdges().SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        return Projects.Where(p => KindOf(p) is "service" or "host" || linked.Contains((string)p["path"]!)).ToList();
    }

    /// <summary>Edges of the service map: one per (from project, to project, via), with the strongest resolution among its links.</summary>
    public IReadOnlyList<(string From, string To, string Via, string Resolution, int Count)> ServiceEdges() =>
        Links.Where(l => l["to"] is JsonObject && (string?)l["from"]!["project"] != (string?)l["to"]!["project"])
            .GroupBy(l => ((string)l["from"]!["project"]!, (string)l["to"]!["project"]!, (string)l["via"]!))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Select(l => (string)l["resolution"]!).OrderByDescending(Rank).First(), g.Count()))
            .OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal).ThenBy(e => e.Item3, StringComparer.Ordinal)
            .ToList();

    /// <summary>Message and gRPC contracts: key, display name, kind and the observations that take part.</summary>
    public IReadOnlyList<Contract> Contracts()
    {
        var contracts = new List<Contract>();
        foreach (var group in Observations.Where(o => (string)o["kind"]! is "messaging.publish" or "messaging.subscribe" && o["contract"]?["identity"] is not null)
                     .GroupBy(o => (string)o["contract"]!["identity"]!, StringComparer.Ordinal))
        {
            var name = group.Select(o => (string?)o["contract"]!["fullName"] ?? (string?)o["contract"]!["name"]).Where(n => n is not null).Order(StringComparer.Ordinal).FirstOrDefault() ?? group.Key;
            contracts.Add(new Contract(group.Key, name, "message", Sort(group)));
        }
        foreach (var group in Observations.Where(o => (string)o["kind"]! is "grpc.service" or "grpc.call" or "grpc.proto" && o["service"] is not null)
                     .GroupBy(o => (string)o["service"]!, StringComparer.Ordinal))
        {
            contracts.Add(new Contract("grpc:" + group.Key, group.Key, "grpc", Sort(group)));
        }
        return contracts.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();

        List<JsonObject> Sort(IEnumerable<JsonObject> items) =>
            items.OrderBy(o => (string)o["kind"]!, StringComparer.Ordinal).ThenBy(At, StringComparer.Ordinal).ThenBy(o => (string)o["id"]!, StringComparer.Ordinal).ToList();
    }

    public sealed record Contract(string Key, string Name, string Kind, IReadOnlyList<JsonObject> Members)
    {
        public string FileName => Slug(Name) + "-" + DotnetIntelligence.Contracts.Hashing.Sha256Hex(Key)[..8];
    }

    public static int Rank(string resolution) => resolution switch
    {
        "exact" => 3,
        "ambiguous" => 2,
        "candidate" => 1,
        _ => 0,
    };

    /// <summary>Entity ids reduced to their documentation id ("T:A.B"); other values unchanged.</summary>
    public static string Short(string? id) => id is null ? "unknown" : IndexWriter.ShortId(id);

    public static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);

    public static string Slug(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length > 60 ? slug[..60] : slug.Length == 0 ? "contract" : slug;
    }

    public static string NodeId(string key) => "n" + DotnetIntelligence.Contracts.Hashing.Sha256Hex(key)[..10];

    /// <summary>Mermaid label text: quotes and angle brackets replaced by entities.</summary>
    public static string Label(string text) => text.Replace("\"", "#quot;", StringComparison.Ordinal).Replace("<", "#lt;", StringComparison.Ordinal).Replace(">", "#gt;", StringComparison.Ordinal);
}
