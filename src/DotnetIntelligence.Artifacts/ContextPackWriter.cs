using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Deterministic Markdown context packs (5.3) built only from graph records: a repository overview and one card
/// per project. Every statement cites evidence (path:line); every card ends with what could not be determined;
/// cut lists say how many items were omitted and where the rest is.
/// </summary>
public static class ContextPackWriter
{
    public const int ListLimit = 40;

    public sealed record Input(
        JsonObject Manifest,
        IReadOnlyList<JsonObject> Projects,
        IReadOnlyList<JsonObject> Contexts,
        IReadOnlyList<JsonObject> Entities,
        IReadOnlyList<JsonObject> Declarations,
        IReadOnlyList<JsonObject> Relations,
        IReadOnlyList<JsonObject> Diagnostics);

    public static IReadOnlyDictionary<string, string> Render(Input input)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var declarationsByEntity = input.Declarations
            .GroupBy(d => (string)d["entity"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => (string)d["path"]!, StringComparer.Ordinal).ThenBy(d => (int)d["span"]!["startLine"]!).ToList(), StringComparer.Ordinal);
        var entities = input.Entities.ToDictionary(e => (string)e["id"]!, StringComparer.Ordinal);
        var contextsByProject = input.Contexts.ToDictionary(c => (string)c["project"]!, StringComparer.Ordinal);
        var projectById = input.Projects.ToDictionary(p => (string)p["id"]!, StringComparer.Ordinal);
        var projectByPath = input.Projects.ToDictionary(p => (string)p["path"]!, StringComparer.Ordinal);

        files["context/overview.md"] = Overview(input, entities, contextsByProject);
        foreach (var project in input.Projects)
        {
            files[$"context/projects/{CardName((string)project["path"]!)}.md"] =
                ProjectCard(input, project, entities, declarationsByEntity, contextsByProject, projectById, projectByPath);
        }
        return files;
    }

    public static string CardName(string projectPath) =>
        Path.GetFileNameWithoutExtension(projectPath) + "-" + Contracts.Hashing.Sha256Hex(projectPath)[..8];

    private static string Overview(Input input, Dictionary<string, JsonObject> entities, Dictionary<string, JsonObject> contexts)
    {
        var manifest = input.Manifest;
        var b = new StringBuilder();
        b.Append("# Repository overview\n\n");
        b.Append($"- Commit: `{manifest["commit"]}`{((bool?)manifest["dirty"] == true ? " (dirty working tree)" : string.Empty)}\n");
        b.Append($"- Engine: {manifest["engine"]!["name"]} {manifest["engine"]!["version"]}, Roslyn {manifest["roslyn"]!["apiVersion"]}\n");
        b.Append($"- Status: {manifest["status"]}\n\n");

        b.Append("## Solutions\n\n");
        foreach (var solution in manifest["solutions"]!.AsArray())
        {
            b.Append($"- `{solution!["path"]}` ({solution["format"]})\n");
        }

        b.Append("\n## Projects\n\n| Project | Load mode | Target framework | Types | Card |\n|---|---|---|---|---|\n");
        var typeCounts = input.Relations.Where(r => (string)r["kind"]! == "declares")
            .GroupBy(r => (string)r["from"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var project in input.Projects)
        {
            var path = (string)project["path"]!;
            var context = contexts.GetValueOrDefault(path);
            b.Append($"| `{path}` | {context?["loadMode"] ?? "not-analyzed"} | {context?["targetFramework"] ?? "unknown"} | {typeCounts.GetValueOrDefault((string)project["id"]!)} | [card](projects/{CardName(path)}.md) |\n");
        }

        b.Append("\n## Project references\n\n");
        var edges = input.Projects.SelectMany(p => (p["projectReferences"]?.AsArray() ?? []).Select(r => ((string)p["path"]!, (string)r!))).ToList();
        if (edges.Count == 0)
        {
            b.Append("None found.\n");
        }
        else
        {
            b.Append("```mermaid\nflowchart LR\n");
            var nodes = edges.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            foreach (var node in nodes)
            {
                b.Append($"  {NodeId(node)}[\"{Path.GetFileNameWithoutExtension(node)}\"]\n");
            }
            foreach (var (from, to) in edges.OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal))
            {
                b.Append($"  {NodeId(from)} --> {NodeId(to)}\n");
            }
            b.Append("```\n");
        }

        AppendGaps(b, input.Diagnostics, scope: null);
        return b.ToString();
    }

    private static string ProjectCard(
        Input input,
        JsonObject project,
        Dictionary<string, JsonObject> entities,
        Dictionary<string, List<JsonObject>> declarations,
        Dictionary<string, JsonObject> contexts,
        Dictionary<string, JsonObject> projectById,
        Dictionary<string, JsonObject> projectByPath)
    {
        var path = (string)project["path"]!;
        var projectId = (string)project["id"]!;
        var context = contexts.GetValueOrDefault(path);
        var b = new StringBuilder();
        b.Append($"# {Path.GetFileNameWithoutExtension(path)}\n\n");
        b.Append($"- Project: `{path}`\n");
        b.Append($"- Assembly: {project["assemblyName"] ?? "unknown"}\n");
        b.Append($"- Load mode: {context?["loadMode"] ?? "not-analyzed"} ({context?["reason"] ?? "no analysis context"})\n");
        b.Append($"- Target frameworks: {string.Join(", ", (project["targetFrameworks"]?.AsArray() ?? []).Select(t => (string)t!))}; analyzed: {context?["targetFramework"] ?? "none"}\n");
        b.Append($"- SDK: {project["resolvedSdk"] ?? "unknown"}\n");
        b.Append($"- Solutions: {string.Join(", ", (project["solutions"]?.AsArray() ?? []).Select(s => $"`{s}`"))}\n");

        var references = (project["projectReferences"]?.AsArray() ?? []).Select(r => (string)r!).ToList();
        b.Append("\n## Project references\n\n");
        b.Append(references.Count == 0 ? "None.\n" : string.Concat(references.Select(r => $"- `{r}`\n")));

        var packages = (project["packages"]?.AsArray() ?? []).Select(p => p!.AsObject()).Where(p => (bool?)p["direct"] == true).ToList();
        b.Append("\n## Direct packages\n\n");
        AppendList(b, packages.Select(p => $"- {p["id"]} {p["version"]}"), "packages", $"graph/projects.ndjson, record `{projectId}`, field `packages`");

        var declared = input.Relations.Where(r => (string)r["kind"]! == "declares" && (string)r["from"]! == projectId)
            .Select(r => entities.GetValueOrDefault((string)r["to"]!)).Where(e => e is not null).Select(e => e!)
            .OrderBy(e => (string)e["accessibility"]! == "public" ? 0 : 1).ThenBy(e => (string)e["signature"]!, StringComparer.Ordinal).ToList();
        b.Append($"\n## Types ({declared.Count})\n\n");
        AppendList(b, declared.Select(e => $"- {e["kind"]} `{e["signature"]}` ({e["accessibility"]}){Where(declarations, (string)e["id"]!)}"),
            "types", $"graph/relations.ndjson, kind `declares`, from `{projectId}`");

        var entryPoints = entities.Values.Where(e => (string?)e["project"] == path && ((string?)e["documentationId"])?.Contains("<Main>$", StringComparison.Ordinal) == true).ToList();
        if (entryPoints.Count > 0)
        {
            b.Append("\n## Entry points\n\n");
            foreach (var entry in entryPoints)
            {
                b.Append($"- top-level statements{Where(declarations, (string)entry["id"]!)}\n");
            }
        }

        var projectEntityIds = entities.Values.Where(e => (string?)e["project"] == path).Select(e => (string)e["id"]!).ToHashSet(StringComparer.Ordinal);
        var incoming = input.Relations
            .Where(r => (string)r["kind"]! is "references" or "calls_declared_target" && projectEntityIds.Contains((string)r["to"]!)
                && entities.TryGetValue((string)r["from"]!, out var fromEntity) && (string?)fromEntity["project"] is { } fromProject && fromProject != path)
            .GroupBy(r => (string)r["to"]!, StringComparer.Ordinal)
            .Select(g => (Entity: entities[g.Key], Projects: g.Select(r => (string)entities[(string)r["from"]!]["project"]!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), Count: g.Sum(r => r["evidence"]!.AsArray().Count)))
            .OrderByDescending(x => x.Count).ThenBy(x => (string)x.Entity["id"]!, StringComparer.Ordinal)
            .ToList();
        b.Append("\n## Used by other projects\n\n");
        AppendList(b, incoming.Select(x => $"- `{x.Entity["signature"]}`: {x.Count} usages from {string.Join(", ", x.Projects.Select(p => $"`{p}`"))}"),
            "symbols", "graph/relations.ndjson, kinds `references` and `calls_declared_target`");

        AppendGaps(b, input.Diagnostics, scope: path);
        return b.ToString();
    }

    private static string Where(Dictionary<string, List<JsonObject>> declarations, string entityId)
    {
        if (!declarations.TryGetValue(entityId, out var list) || list.Count == 0)
        {
            return string.Empty;
        }
        var parts = list.Take(3).Select(d => $"{d["path"]}:{((int)d["span"]!["startLine"]!).ToString(CultureInfo.InvariantCulture)}{(d["generator"] is { } g ? $" (generated by {g})" : string.Empty)}");
        var more = list.Count > 3 ? $" and {list.Count - 3} more declarations" : string.Empty;
        return ", " + string.Join("; ", parts) + more;
    }

    private static void AppendList(StringBuilder b, IEnumerable<string> lines, string noun, string source)
    {
        var all = lines.ToList();
        if (all.Count == 0)
        {
            b.Append("None found.\n");
            return;
        }
        foreach (var line in all.Take(ListLimit))
        {
            b.Append(line).Append('\n');
        }
        if (all.Count > ListLimit)
        {
            b.Append($"\n{all.Count - ListLimit} more {noun} omitted; see {source}.\n");
        }
    }

    private static void AppendGaps(StringBuilder b, IReadOnlyList<JsonObject> diagnostics, string? scope)
    {
        b.Append("\n## What could not be determined\n\n");
        var relevant = diagnostics
            .Where(d => scope is null || (string)d["scope"]! == scope || (string)d["scope"]! == "repository")
            .Where(d => (string)d["severity"]! != "info" || (string)d["state"]! is "unsupported" or "failed" or "partial")
            .GroupBy(d => (string)d["code"]!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (relevant.Count == 0)
        {
            b.Append("No gaps reported by the analysis for this scope.\n");
            return;
        }
        foreach (var group in relevant)
        {
            var first = group.OrderBy(d => (string)d["scope"]!, StringComparer.Ordinal).First();
            var message = ((string)first["message"]!).Replace("\n", " ", StringComparison.Ordinal);
            if (message.Length > 300)
            {
                message = message[..300] + "…";
            }
            b.Append(scope is null
                ? $"- `{group.Key}` ({first["state"]}): {group.Count()} occurrences, for example `{first["scope"]}`: {message}\n"
                : $"- `{group.Key}` ({first["state"]}): {message}\n");
        }
    }

    private static string NodeId(string path) => "p" + Contracts.Hashing.Sha256Hex(path)[..10];
}
