using System.Text.Json.Nodes;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// The one location shown for a record that has several pieces of evidence (contract v1.2): an observation's
/// <c>site</c> when it has one; otherwise, and for relations and links, the first evidence by path, line and column.
/// </summary>
public sealed class Locations
{
    private readonly IReadOnlyDictionary<string, JsonObject> _evidence;

    public Locations(IReadOnlyDictionary<string, JsonObject> evidence) => _evidence = evidence;

    public JsonObject? FirstOf(JsonObject record) => Ordered(record).FirstOrDefault();

    public JsonObject? SiteOf(JsonObject observation) =>
        (string?)observation["site"] is { } site && _evidence.TryGetValue(site, out var evidence) ? evidence : FirstOf(observation);

    private IEnumerable<JsonObject> Ordered(JsonObject record) =>
        (record["evidence"]?.AsArray() ?? []).Select(e => _evidence.GetValueOrDefault((string)e!)).Where(e => e is not null).Select(e => e!)
            .OrderBy(e => (string)e["path"]!, StringComparer.Ordinal)
            .ThenBy(e => (int)e["span"]!["startLine"]!)
            .ThenBy(e => (int)e["span"]!["startColumn"]!)
            .ThenBy(e => (string)e["id"]!, StringComparer.Ordinal);

    public static string Format(JsonObject? evidence) =>
        evidence is null ? "no evidence" : $"{evidence["path"]}:{((int)evidence["span"]!["startLine"]!).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
