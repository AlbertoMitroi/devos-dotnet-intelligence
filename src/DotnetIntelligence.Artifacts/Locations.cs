using System.Text.Json.Nodes;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// The one location shown for a record that has several pieces of evidence. Relations: the first usage by path,
/// line and column. Observations: their evidence also holds context (route groups, base-address configuration,
/// registrations), so the site is chosen as (1) evidence inside a declaration of the entity the observation names
/// (caller, sender, publisher, consumer, handler, implementation), else (2) the last evidence in source code (not
/// configuration or contract files) by path and line, which is the call after its route groups, else (3) the first.
/// </summary>
public sealed class Locations
{
    private static readonly string[] EntityFields = ["caller", "sender", "publisher", "consumer", "handler", "implementation"];

    private static readonly HashSet<string> NonCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".yml", ".yaml", ".proto", ".xml", ".config", ".bpmn", ".tf", ".props", ".targets", ".csproj", ".sln", ".slnx", ".env",
    };

    private readonly IReadOnlyDictionary<string, JsonObject> _evidence;
    private readonly IReadOnlyDictionary<string, List<JsonObject>> _declarations;

    public Locations(IReadOnlyDictionary<string, JsonObject> evidence, IReadOnlyDictionary<string, List<JsonObject>> declarationsByEntity)
    {
        _evidence = evidence;
        _declarations = declarationsByEntity;
    }

    public JsonObject? FirstOf(JsonObject record) => Ordered(record).FirstOrDefault();

    public JsonObject? SiteOf(JsonObject observation)
    {
        var all = Ordered(observation).ToList();
        if (all.Count <= 1)
        {
            return all.FirstOrDefault();
        }
        foreach (var field in EntityFields)
        {
            if ((string?)(observation[field] as JsonValue) is not { } entity || !_declarations.TryGetValue(entity, out var declarations))
            {
                continue;
            }
            var inside = all.FirstOrDefault(e => declarations.Any(d => Contains(d, e)));
            if (inside is not null)
            {
                return inside;
            }
        }
        return all.LastOrDefault(e => !NonCode.Contains(Path.GetExtension((string)e["path"]!))) ?? all[0];
    }

    private IEnumerable<JsonObject> Ordered(JsonObject record) =>
        (record["evidence"]?.AsArray() ?? []).Select(e => _evidence.GetValueOrDefault((string)e!)).Where(e => e is not null).Select(e => e!)
            .OrderBy(e => (string)e["path"]!, StringComparer.Ordinal)
            .ThenBy(e => (int)e["span"]!["startLine"]!)
            .ThenBy(e => (int)e["span"]!["startColumn"]!)
            .ThenBy(e => (string)e["id"]!, StringComparer.Ordinal);

    private static bool Contains(JsonObject declaration, JsonObject evidence)
    {
        if ((string?)declaration["path"] != (string?)evidence["path"])
        {
            return false;
        }
        var extent = declaration["extent"] ?? declaration["span"]!;
        var line = (int)evidence["span"]!["startLine"]!;
        return line >= (int)extent["startLine"]! && line <= (int)extent["endLine"]!;
    }

    public static string Format(JsonObject? evidence) =>
        evidence is null ? "no evidence" : $"{evidence["path"]}:{((int)evidence["span"]!["startLine"]!).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
