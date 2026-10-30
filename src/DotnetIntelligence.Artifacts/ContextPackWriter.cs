using System.Text;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Deterministic Markdown context packs (5.3) built only from graph records: a repository overview with the service
/// map, one service card per project and one contract card per message or gRPC contract. Every statement cites
/// evidence (path:line); every card ends with what could not be determined; cut lists say how many items were
/// omitted and how to get them (a query command or the NDJSON file).
/// </summary>
public static class ContextPackWriter
{
    public const int ListLimit = 40;

    public static IReadOnlyDictionary<string, string> Render(ServiceModel model)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["context/overview.md"] = Overview(model),
        };
        foreach (var project in model.Projects)
        {
            files[$"context/projects/{CardName((string)project["path"]!)}.md"] = ServiceCard(model, project);
        }
        foreach (var contract in model.Contracts())
        {
            files[$"context/contracts/{contract.FileName}.md"] = ContractCard(model, contract, ListLimit);
        }
        return files;
    }

    public static string CardName(string projectPath) =>
        Path.GetFileNameWithoutExtension(projectPath) + "-" + Contracts.Hashing.Sha256Hex(projectPath)[..8];

    private static string Overview(ServiceModel model)
    {
        var manifest = model.Manifest;
        var b = new StringBuilder();
        b.Append("# Repository overview\n\n");
        b.Append($"- Commit: `{manifest["commit"]}`{((bool?)manifest["dirty"] == true ? " (dirty working tree)" : string.Empty)}\n");
        b.Append($"- Engine: {manifest["engine"]!["name"]} {manifest["engine"]!["version"]}, Roslyn {manifest["roslyn"]?["apiVersion"]}\n");
        b.Append($"- Status: {manifest["status"]}\n\n");

        b.Append("## Solutions\n\n");
        foreach (var solution in manifest["solutions"]?.AsArray() ?? [])
        {
            b.Append($"- `{solution!["path"]}` ({solution["format"]})\n");
        }

        b.Append("\n## Services\n\n");
        var services = model.ServiceProjects();
        if (services.Count == 0)
        {
            b.Append("None found: no project serves endpoints or subscriptions, has an entry point or is an Aspire project resource.\n");
        }
        else
        {
            b.Append("| Project | Kind | Endpoints | Outgoing calls | Published | Subscribed | Card |\n|---|---|---|---|---|---|---|\n");
            foreach (var project in services)
            {
                var path = (string)project["path"]!;
                b.Append($"| `{path}` | {model.KindOf(project)} | {model.Of("http.endpoint", path).Count() + model.Of("grpc.service", path).Count()} | {model.Of("http.call", path).Count() + model.Of("grpc.call", path).Count()} | {model.Of("messaging.publish", path).Count()} | {model.Of("messaging.subscribe", path).Count()} | [card](projects/{CardName(path)}.md) |\n");
            }
        }

        b.Append("\n## Service map\n\n");
        b.Append(ServiceMapList(model, ListLimit));

        var contracts = model.Contracts();
        b.Append("\n## Contracts\n\n");
        AppendList(b, contracts.Select(c => $"- {c.Kind} `{c.Name}`: {c.Members.Count} observations, [card](contracts/{c.FileName}.md)"),
            "contracts", "`query <generation> messages`, or graph/observations.ndjson");

        b.Append("\n## Projects\n\n| Project | Kind | Load mode | Target framework | Types | Card |\n|---|---|---|---|---|---|\n");
        var typeCounts = model.Relations.Where(r => (string)r["kind"]! == "declares")
            .GroupBy(r => (string)r["from"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var project in model.Projects)
        {
            var path = (string)project["path"]!;
            var context = model.ContextByProject.GetValueOrDefault(path);
            b.Append($"| `{path}` | {model.KindOf(project)} | {context?["loadMode"] ?? "not-analyzed"} | {context?["targetFramework"] ?? "unknown"} | {typeCounts.GetValueOrDefault((string)project["id"]!)} | [card](projects/{CardName(path)}.md) |\n");
        }

        b.Append("\n## Project references\n\n");
        var edges = model.Projects.SelectMany(p => (p["projectReferences"]?.AsArray() ?? []).Select(r => ((string)p["path"]!, (string)r!))).ToList();
        if (edges.Count == 0)
        {
            b.Append("None found.\n");
        }
        else
        {
            b.Append("```mermaid\nflowchart LR\n");
            foreach (var node in edges.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                b.Append($"  {ServiceModel.NodeId(node)}[\"{ServiceModel.Label(ServiceModel.ProjectName(node))}\"]\n");
            }
            foreach (var (from, to) in edges.OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal))
            {
                b.Append($"  {ServiceModel.NodeId(from)} --> {ServiceModel.NodeId(to)}\n");
            }
            b.Append("```\n");
        }

        AppendGaps(b, model, scope: null);
        return b.ToString();
    }

    /// <summary>Service-to-service edges as text, each with one example link's evidence.</summary>
    internal static string ServiceMapList(ServiceModel model, int limit)
    {
        var b = new StringBuilder();
        var edges = model.ServiceEdges();
        var lines = edges.Select(e =>
        {
            var example = model.Links.Where(l => l["to"] is JsonObject && (string?)l["from"]!["project"] == e.From && (string?)l["to"]!["project"] == e.To && (string)l["via"]! == e.Via)
                .OrderByDescending(l => ServiceModel.Rank((string)l["resolution"]!)).ThenBy(l => (string)l["id"]!, StringComparer.Ordinal).First();
            var at = model.ObservationById.TryGetValue((string)example["from"]!["observation"]!, out var from) ? model.At(from) : "no evidence";
            return $"- `{ServiceModel.ProjectName(e.From)}` → `{ServiceModel.ProjectName(e.To)}` via {e.Via}: {e.Count} link(s), strongest {e.Resolution}; for example {at}";
        });
        AppendList(b, lines, "edges", "graph/links.ndjson");
        var unresolved = model.Links.Count(l => (string)l["resolution"]! == "unresolved");
        if (unresolved > 0)
        {
            b.Append($"\n{unresolved} link(s) are unresolved (no target found); each has its reason in graph/links.ndjson and on the card of its project.\n");
        }
        return b.ToString();
    }

    private static string ServiceCard(ServiceModel model, JsonObject project)
    {
        var path = (string)project["path"]!;
        var projectId = (string)project["id"]!;
        var context = model.ContextByProject.GetValueOrDefault(path);
        var b = new StringBuilder();
        b.Append($"# {ServiceModel.ProjectName(path)}\n\n");
        b.Append($"- Project: `{path}`\n");
        b.Append($"- Kind: {model.KindOf(project)}\n");
        b.Append($"- Assembly: {project["assemblyName"] ?? "unknown"}\n");
        b.Append($"- Load mode: {context?["loadMode"] ?? "not-analyzed"} ({context?["reason"] ?? "no analysis context"})\n");
        b.Append($"- Target frameworks: {string.Join(", ", (project["targetFrameworks"]?.AsArray() ?? []).Select(t => (string)t!))}; analyzed: {context?["targetFramework"] ?? "none"}\n");
        b.Append($"- SDK: {project["resolvedSdk"] ?? "unknown"}\n");
        b.Append($"- Solutions: {string.Join(", ", (project["solutions"]?.AsArray() ?? []).Select(s => $"`{s}`"))}\n");

        var entryPoints = model.EntryPoints(path).ToList();
        if (entryPoints.Count > 0)
        {
            b.Append("\n## Entry points\n\n");
            foreach (var entry in entryPoints)
            {
                var id = (string)entry["id"]!;
                var at = model.DeclaredAt(id) ?? model.Relations.Where(r => (string)r["from"]! == id).Select(model.At).Where(a => a != "no evidence").Order(StringComparer.Ordinal).FirstOrDefault();
                b.Append(((string?)entry["documentationId"])?.Contains("{Main}$", StringComparison.Ordinal) == true
                    ? $"- top-level statements, {at ?? "no evidence"}\n"
                    : $"- `{ServiceModel.Short(id)}`, {at ?? "no evidence"}\n");
            }
        }

        AppendServiceSections(b, model, path, ListLimit, "##");

        var references = (project["projectReferences"]?.AsArray() ?? []).Select(r => (string)r!).ToList();
        b.Append("\n## Project references\n\n");
        b.Append(references.Count == 0 ? "None.\n" : string.Concat(references.Select(r => $"- `{r}`\n")));

        var packages = (project["packages"]?.AsArray() ?? []).Select(p => p!.AsObject()).Where(p => (bool?)p["direct"] == true).ToList();
        b.Append("\n## Direct packages\n\n");
        AppendList(b, packages.Select(p => $"- {p["id"]} {p["version"]}"), "packages", $"graph/projects.ndjson, record `{projectId}`, field `packages`");

        var declared = model.Relations.Where(r => (string)r["kind"]! == "declares" && (string)r["from"]! == projectId)
            .Select(r => model.Entities.GetValueOrDefault((string)r["to"]!)).Where(e => e is not null).Select(e => e!)
            .OrderBy(e => (string)e["accessibility"]! == "public" ? 0 : 1).ThenBy(e => (string)e["signature"]!, StringComparer.Ordinal).ToList();
        b.Append($"\n## Types ({declared.Count})\n\n");
        AppendList(b, declared.Select(e => $"- {e["kind"]} `{e["signature"]}` ({e["accessibility"]}){Where(model, (string)e["id"]!)}"),
            "types", $"`query <generation> search <name> --kind entity`, or graph/relations.ndjson, kind `declares`, from `{projectId}`");

        var projectEntityIds = model.Entities.Values.Where(e => (string?)e["project"] == path).Select(e => (string)e["id"]!).ToHashSet(StringComparer.Ordinal);
        var incoming = model.Relations
            .Where(r => (string)r["kind"]! is "references" or "calls_declared_target" && projectEntityIds.Contains((string)r["to"]!)
                && model.Entities.TryGetValue((string)r["from"]!, out var fromEntity) && (string?)fromEntity["project"] is { } fromProject && fromProject != path)
            .GroupBy(r => (string)r["to"]!, StringComparer.Ordinal)
            .Select(g => (Entity: model.Entities[g.Key], Projects: g.Select(r => (string)model.Entities[(string)r["from"]!]["project"]!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), Count: g.Sum(r => r["evidence"]!.AsArray().Count)))
            .OrderByDescending(x => x.Count).ThenBy(x => (string)x.Entity["id"]!, StringComparer.Ordinal)
            .ToList();
        b.Append("\n## Used by other projects\n\n");
        AppendList(b, incoming.Select(x => $"- `{x.Entity["signature"]}`: {x.Count} usages from {string.Join(", ", x.Projects.Select(p => $"`{p}`"))}"),
            "symbols", "`query <generation> refs <symbol>`, or graph/relations.ndjson, kinds `references` and `calls_declared_target`");

        AppendGaps(b, model, scope: path);
        return b.ToString();
    }

    /// <summary>Endpoints, outgoing calls, messages, dispatch, DI, data and configuration of one project, with evidence.</summary>
    internal static void AppendServiceSections(StringBuilder b, ServiceModel model, string path, int limit, string heading)
    {
        var endpoints = model.Of("http.endpoint", path).Select(o => $"- {o["method"]} `{o["canonicalRoute"]}` → `{ServiceModel.Short((string?)o["handler"])}`, {model.At(o)}{Callers(model, o)}")
            .Concat(model.Of("grpc.service", path).Select(o => $"- gRPC `{o["service"]}/{o["method"]}` → `{ServiceModel.Short((string?)o["implementation"])}`, {model.At(o)}{Callers(model, o)}"))
            .Concat(model.Of("realtime.hub", path).Select(o => $"- hub `{ServiceModel.Short((string?)o["hub"])}` at `{o["canonicalRoute"] ?? o["route"]}`, {model.At(o)}"))
            .ToList();
        b.Append($"\n{heading} Endpoints ({endpoints.Count})\n\n");
        AppendList(b, endpoints, "endpoints", $"`query <generation> routes`, or graph/observations.ndjson, kind `http.endpoint`, project `{path}`", limit);

        var calls = model.Of("http.call", path).Select(o => $"- {o["method"]} `{o["canonicalRoute"] ?? o["urlTemplate"] ?? "(unknown route)"}` ({o["resolution"]}), {model.At(o)}{Targets(model, o)}")
            .Concat(model.Of("grpc.call", path).Select(o => $"- gRPC `{o["service"]}/{o["method"]}`, {model.At(o)}{Targets(model, o)}"))
            .ToList();
        b.Append($"\n{heading} Outgoing calls ({calls.Count})\n\n");
        AppendList(b, calls, "calls", $"`query <generation> http`, or graph/observations.ndjson, kind `http.call`, project `{path}`", limit);

        var published = model.Of("messaging.publish", path).Select(o => $"- publishes `{o["contract"]?["identity"]}` ({Transport(o)}, {o["resolution"]}), {model.At(o)}{Targets(model, o)}").ToList();
        var subscribed = model.Of("messaging.subscribe", path).Select(o => $"- subscribes `{o["contract"]?["identity"]}` in `{ServiceModel.Short((string?)o["handler"])}` ({Transport(o)}, {o["resolution"]}), {model.At(o)}").ToList();
        b.Append($"\n{heading} Messages ({published.Count} published, {subscribed.Count} subscribed)\n\n");
        AppendList(b, published.Concat(subscribed), "message observations", $"`query <generation> messages`, or graph/observations.ndjson, kinds `messaging.publish` and `messaging.subscribe`, project `{path}`", limit);

        var dispatch = model.Of("inprocess.send", path).Select(o => $"- sends `{ServiceModel.Short((string?)o["request"])}` ({o["mechanism"]}), {model.At(o)}{Targets(model, o)}")
            .Concat(model.Of("inprocess.handler", path).Select(o => $"- handles `{ServiceModel.Short((string?)o["request"])}` in `{ServiceModel.Short((string?)o["handler"])}` ({o["mechanism"]}), {model.At(o)}"))
            .ToList();
        if (dispatch.Count > 0)
        {
            b.Append($"\n{heading} In-process dispatch ({dispatch.Count})\n\n");
            AppendList(b, dispatch, "dispatch observations", $"graph/observations.ndjson, kinds `inprocess.send` and `inprocess.handler`, project `{path}`", limit);
        }

        var registrations = model.Of("di.registers", path).ToList();
        var consumes = model.Of("di.consumes", path).ToList();
        b.Append($"\n{heading} Dependency injection ({registrations.Count} registrations, {consumes.Count} consumed services)\n\n");
        if (registrations.Count > 0)
        {
            var lifetimes = registrations.GroupBy(r => (string?)r["lifetime"] ?? "unknown", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}");
            b.Append($"Lifetimes: {string.Join(", ", lifetimes)}.\n\n");
        }
        AppendList(b, registrations.Select(r => $"- `{ServiceModel.Short((string?)r["service"])}` → {(r["implementation"] is null ? ((bool?)r["factory"] == true ? "factory" : "unknown") : $"`{ServiceModel.Short((string?)r["implementation"])}`")} ({r["lifetime"]}{((bool?)r["conditional"] == true ? ", conditional" : string.Empty)}), {model.At(r)}"),
            "registrations", $"`query <generation> di <service>`, or graph/observations.ndjson, kind `di.registers`, project `{path}`", limit);

        var data = model.Of("data.table", path).Select(o => $"- table `{(o["schema"] is null ? string.Empty : o["schema"] + ".")}{o["table"]}` ← `{ServiceModel.Short((string?)o["entity"])}` ({o["store"]}), {model.At(o)}")
            .Concat(model.Of("data.maps_to", path).Select(o => $"- `{ServiceModel.Short((string?)o["dbContext"])}` maps `{ServiceModel.Short((string?)o["entity"])}`, {model.At(o)}"))
            .ToList();
        if (data.Count > 0)
        {
            b.Append($"\n{heading} Data ({data.Count})\n\n");
            AppendList(b, data, "data observations", $"graph/observations.ndjson, kinds `data.table` and `data.maps_to`, project `{path}`", limit);
        }

        var keys = model.Of("config.key", path).GroupBy(o => (string)o["key"]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"- `{g.Key}`{(g.Any(o => (bool?)o["secret"] == true) ? " (secret)" : string.Empty)}, {model.At(g.First())}{(g.Count() > 1 ? $" and {g.Count() - 1} more" : string.Empty)}")
            .ToList();
        b.Append($"\n{heading} Configuration keys ({keys.Count}, values never stored)\n\n");
        AppendList(b, keys, "keys", $"`query <generation> search <key> --kind observation`, or graph/observations.ndjson, kind `config.key`, project `{path}`", limit);
    }

    internal static string ContractCard(ServiceModel model, ServiceModel.Contract contract, int limit)
    {
        var b = new StringBuilder();
        b.Append($"# {(contract.Kind == "grpc" ? "gRPC service" : "Message")} `{contract.Name}`\n\n");
        b.Append($"- Identity: `{(contract.Kind == "grpc" ? contract.Name : contract.Key)}`\n");
        b.Append($"- Commit: `{model.Manifest["commit"]}`\n");
        AppendContractSections(b, model, contract, limit, "##");
        b.Append("\n## What could not be determined\n\n");
        var gaps = ContractGaps(model, contract).ToList();
        b.Append(gaps.Count == 0 ? "Every observation of this contract is linked.\n" : string.Concat(gaps.Select(g => g + "\n")));
        return b.ToString();
    }

    internal static void AppendContractSections(StringBuilder b, ServiceModel model, ServiceModel.Contract contract, int limit, string heading)
    {
        if (contract.Kind == "grpc")
        {
            Section("Proto files", contract.Members.Where(o => (string)o["kind"]! == "grpc.proto").Select(o => $"- `{o["proto"]}` ({o["role"]}) in `{o["project"]}`, {model.At(o)}"));
            Section("Implementations", contract.Members.Where(o => (string)o["kind"]! == "grpc.service").Select(o => $"- `{o["method"]}` → `{ServiceModel.Short((string?)o["implementation"])}` in `{o["project"]}`, {model.At(o)}"));
            Section("Callers", contract.Members.Where(o => (string)o["kind"]! == "grpc.call").Select(o => $"- `{o["method"]}` from `{ServiceModel.Short((string?)o["caller"])}` in `{o["project"]}`, {model.At(o)}{Targets(model, o)}"));
            return;
        }
        Section("Publishers", contract.Members.Where(o => (string)o["kind"]! == "messaging.publish").Select(o => $"- `{o["project"]}` ({Transport(o)}{Destination(o)}, {o["resolution"]}), {model.At(o)}{Targets(model, o)}"));
        Section("Subscribers", contract.Members.Where(o => (string)o["kind"]! == "messaging.subscribe").Select(o => $"- `{ServiceModel.Short((string?)o["handler"])}` in `{o["project"]}` ({Transport(o)}{Destination(o)}, {o["resolution"]}), {model.At(o)}"));

        void Section(string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            b.Append($"\n{heading} {title} ({list.Count})\n\n");
            AppendList(b, list, title.ToLowerInvariant(), $"`query <generation> messages {contract.Name}`, or graph/observations.ndjson", limit);
        }
    }

    internal static IEnumerable<string> ContractGaps(ServiceModel model, ServiceModel.Contract contract)
    {
        foreach (var o in contract.Members)
        {
            foreach (var link in model.LinksFrom((string)o["id"]!).Where(l => (string)l["resolution"]! == "unresolved"))
            {
                yield return $"- {o["kind"]} at {model.At(o)}: {link["reason"]}";
            }
            if ((string?)o["resolution"] is "candidate" or "unresolved")
            {
                yield return $"- {o["kind"]} at {model.At(o)} is itself {o["resolution"]}{(o["reason"] is null ? string.Empty : $": {o["reason"]}")}";
            }
        }
    }

    private static string Transport(JsonObject o) => (string?)o["transport"] ?? "transport unknown";

    private static string Destination(JsonObject o) => o["destination"]?["name"] is { } name ? $", {o["destination"]!["kind"]} `{name}`" : string.Empty;

    /// <summary>The link targets of an observation, inline: "→ exact `Project` path:line", or the reason it is unresolved.</summary>
    private static string Targets(ServiceModel model, JsonObject observation)
    {
        var links = model.LinksFrom((string)observation["id"]!).ToList();
        if (links.Count == 0)
        {
            return string.Empty;
        }
        var parts = links.Take(3).Select(l => l["to"] is JsonObject to && model.ObservationById.TryGetValue((string)to["observation"]!, out var target)
            ? $"{l["resolution"]} `{ServiceModel.ProjectName((string)to["project"]!)}` {model.At(target)}"
            : $"unresolved: {l["reason"]}");
        return "; → " + string.Join("; ", parts) + (links.Count > 3 ? $"; and {links.Count - 3} more links" : string.Empty);
    }

    private static string Callers(ServiceModel model, JsonObject endpoint)
    {
        var count = model.LinksTo((string)endpoint["id"]!).Count();
        return count == 0 ? string.Empty : $"; called from {count} linked call(s)";
    }

    private static string Where(ServiceModel model, string entityId)
    {
        if (!model.DeclarationsByEntity.TryGetValue(entityId, out var list) || list.Count == 0)
        {
            return string.Empty;
        }
        var parts = list.Take(3).Select(d => $"{d["path"]}:{(int)d["span"]!["startLine"]!}{(d["generator"] is { } g ? $" (generated by {g})" : string.Empty)}");
        var more = list.Count > 3 ? $" and {list.Count - 3} more declarations" : string.Empty;
        return ", " + string.Join("; ", parts) + more;
    }

    internal static void AppendList(StringBuilder b, IEnumerable<string> lines, string noun, string source, int limit = ListLimit)
    {
        var all = lines.ToList();
        if (all.Count == 0)
        {
            b.Append("None found.\n");
            return;
        }
        foreach (var line in all.Take(limit))
        {
            b.Append(line).Append('\n');
        }
        if (all.Count > limit)
        {
            b.Append($"\n{all.Count - limit} more {noun} omitted; see {source}.\n");
        }
    }

    /// <summary>"What could not be determined": diagnostics of the scope and unresolved links leaving it.</summary>
    internal static void AppendGaps(StringBuilder b, ServiceModel model, string? scope)
    {
        b.Append("\n## What could not be determined\n\n");
        var relevant = model.Diagnostics
            .Where(d => scope is null || (string)d["scope"]! == scope || (string)d["scope"]! == "repository")
            .Where(d => (string)d["severity"]! != "info" || (string)d["state"]! is "unsupported" or "failed" or "partial")
            .GroupBy(d => (string)d["code"]!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var unresolved = model.Links.Where(l => (string)l["resolution"]! == "unresolved" && (scope is null || (string?)l["from"]!["project"] == scope))
            .Select(l => model.ObservationById.TryGetValue((string)l["from"]!["observation"]!, out var from) ? $"- unresolved {l["via"]} link from {from["kind"]} at {model.At(from)}: {l["reason"]}" : null)
            .Where(l => l is not null).Select(l => l!).Order(StringComparer.Ordinal).ToList();
        if (relevant.Count == 0 && unresolved.Count == 0)
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
        if (scope is not null)
        {
            AppendList(b, unresolved, "unresolved links", "`query <generation> http`, or graph/links.ndjson with resolution `unresolved`");
        }
        else if (unresolved.Count > 0)
        {
            b.Append($"- {unresolved.Count} unresolved links; each project card lists its own with the reason.\n");
        }
    }
}
