using System.Text;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Deterministic wiki (5.4): an index page, one page per service and one per contract, with Mermaid diagrams
/// generated from the graph (service map, message flows) and a legend for resolution. Each page records the commit,
/// the engine version and the queries that rebuild its content. No LLM text.
/// </summary>
public static class WikiWriter
{
    public const int ListLimit = 200;

    private const string Legend =
        "## Legend\n\n" +
        "| Resolution | Meaning | Diagram |\n|---|---|---|\n" +
        "| exact | both sides resolved by symbols and normalized fields | solid arrow `-->` |\n" +
        "| ambiguous | several targets match; all are kept | dotted arrow `-.->` labelled ambiguous |\n" +
        "| candidate | a match that relies on a heuristic or a configured convention | dotted arrow `-.->` labelled candidate |\n" +
        "| unresolved | no target found; the reason is in the link | not drawn, listed under gaps |\n";

    public static IReadOnlyDictionary<string, string> Render(ServiceModel model)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var services = model.ServiceProjects();
        var contracts = model.Contracts();
        files["wiki/index.md"] = Index(model, services, contracts);
        foreach (var project in services)
        {
            files[$"wiki/services/{ContextPackWriter.CardName((string)project["path"]!)}.md"] = ServicePage(model, project);
        }
        foreach (var contract in contracts)
        {
            files[$"wiki/contracts/{contract.FileName}.md"] = ContractPage(model, contract);
        }
        return files;
    }

    private static string Index(ServiceModel model, IReadOnlyList<JsonObject> services, IReadOnlyList<ServiceModel.Contract> contracts)
    {
        var b = new StringBuilder();
        b.Append("# Repository wiki\n\n");
        Header(b, model);
        b.Append("\n## Service map\n\n");
        b.Append(ServiceMap(model, services.Select(p => (string)p["path"]!).ToList(), focus: null));
        b.Append('\n').Append(ContextPackWriter.ServiceMapList(model, ListLimit));
        b.Append("\n## Services\n\n");
        ContextPackWriter.AppendList(b, services.Select(p => $"- [{ServiceModel.ProjectName((string)p["path"]!)}](services/{ContextPackWriter.CardName((string)p["path"]!)}.md) ({model.KindOf(p)}), `{p["path"]}`"),
            "services", "context/overview.md", ListLimit);
        b.Append("\n## Contracts\n\n");
        ContextPackWriter.AppendList(b, contracts.Select(c => $"- [{c.Name}](contracts/{c.FileName}.md) ({c.Kind}, {c.Members.Count} observations)"),
            "contracts", "`query <generation> messages`", ListLimit);
        b.Append('\n').Append(Legend);
        Queries(b, ["routes", "http", "messages", "dump"]);
        return b.ToString();
    }

    private static string ServicePage(ServiceModel model, JsonObject project)
    {
        var path = (string)project["path"]!;
        var b = new StringBuilder();
        b.Append($"# {ServiceModel.ProjectName(path)}\n\n");
        Header(b, model);
        b.Append($"- Project: `{path}`\n- Kind: {model.KindOf(project)}\n- Context card: [context/projects/{ContextPackWriter.CardName(path)}.md](../../context/projects/{ContextPackWriter.CardName(path)}.md)\n");
        var neighbours = model.ServiceEdges().Where(e => e.From == path || e.To == path).SelectMany(e => new[] { e.From, e.To }).Append(path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        b.Append("\n## Neighbours\n\n");
        b.Append(neighbours.Count > 1 ? ServiceMap(model, neighbours, focus: path) : "No links to or from other projects.\n");
        ContextPackWriter.AppendServiceSections(b, model, path, ListLimit, "##");
        b.Append('\n').Append(Legend);
        Queries(b, [$"routes", "http", "messages", $"search {ServiceModel.ProjectName(path)} --kind entity"]);
        return b.ToString();
    }

    private static string ContractPage(ServiceModel model, ServiceModel.Contract contract)
    {
        var b = new StringBuilder();
        b.Append($"# {(contract.Kind == "grpc" ? "gRPC service" : "Message")} `{contract.Name}`\n\n");
        Header(b, model);
        b.Append("\n## Flow\n\n");
        b.Append(Flow(model, contract));
        ContextPackWriter.AppendContractSections(b, model, contract, ListLimit, "##");
        b.Append("\n## What could not be determined\n\n");
        var gaps = ContextPackWriter.ContractGaps(model, contract).ToList();
        b.Append(gaps.Count == 0 ? "Every observation of this contract is linked.\n" : string.Concat(gaps.Select(g => g + "\n")));
        b.Append('\n').Append(Legend);
        Queries(b, contract.Kind == "grpc" ? [$"calls-to {contract.Name}", "trace <caller> --to <implementation>"] : [$"messages {contract.Name}"]);
        return b.ToString();
    }

    private static void Header(StringBuilder b, ServiceModel model)
    {
        b.Append($"- Commit: `{model.Manifest["commit"]}`{((bool?)model.Manifest["dirty"] == true ? " (dirty working tree)" : string.Empty)}\n");
        b.Append($"- Engine: {model.Manifest["engine"]!["name"]} {model.Manifest["engine"]!["version"]}\n");
        b.Append($"- Status: {model.Manifest["status"]}\n");
    }

    private static void Queries(StringBuilder b, IEnumerable<string> commands)
    {
        b.Append("\n## Queries\n\nThis page is generated from `graph/*.ndjson`; the same facts come from:\n\n");
        foreach (var command in commands)
        {
            b.Append($"- `dotnet-intelligence query <generation> {command}`\n");
        }
    }

    /// <summary>Mermaid flowchart of the links between the given projects; unresolved links are not drawn.</summary>
    private static string ServiceMap(ServiceModel model, IReadOnlyList<string> projects, string? focus)
    {
        var set = projects.ToHashSet(StringComparer.Ordinal);
        var edges = model.ServiceEdges().Where(e => set.Contains(e.From) && set.Contains(e.To)).ToList();
        var nodes = edges.SelectMany(e => new[] { e.From, e.To }).Concat(focus is null ? [] : [focus]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (nodes.Count == 0)
        {
            return "No links between services were found.\n";
        }
        var b = new StringBuilder("```mermaid\nflowchart LR\n");
        foreach (var node in nodes)
        {
            var label = ServiceModel.Label(ServiceModel.ProjectName(node));
            b.Append(node == focus ? $"  {ServiceModel.NodeId(node)}[[\"{label}\"]]\n" : $"  {ServiceModel.NodeId(node)}[\"{label}\"]\n");
        }
        foreach (var e in edges)
        {
            b.Append(Arrow(ServiceModel.NodeId(e.From), ServiceModel.NodeId(e.To), $"{e.Via} ×{e.Count}", e.Resolution));
        }
        b.Append("```\n");
        return b.ToString();
    }

    /// <summary>Mermaid flow of one contract: publishers or callers on the left, the contract, handlers on the right.</summary>
    private static string Flow(ServiceModel model, ServiceModel.Contract contract)
    {
        var b = new StringBuilder("```mermaid\nflowchart LR\n");
        var contractNode = ServiceModel.NodeId("contract|" + contract.Key);
        b.Append($"  {contractNode}{{{{\"{ServiceModel.Label(contract.Name)}\"}}}}\n");
        var senders = contract.Members.Where(o => (string)o["kind"]! is "messaging.publish" or "grpc.call").ToList();
        var receivers = contract.Members.Where(o => (string)o["kind"]! is "messaging.subscribe" or "grpc.service").ToList();
        foreach (var project in senders.Concat(receivers).Select(o => (string)o["project"]!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            b.Append($"  {ServiceModel.NodeId(project)}[\"{ServiceModel.Label(ServiceModel.ProjectName(project))}\"]\n");
        }
        foreach (var group in senders.GroupBy(o => (string)o["project"]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            b.Append(Arrow(ServiceModel.NodeId(group.Key), contractNode, contract.Kind == "grpc" ? "calls" : "publishes",
                group.Select(o => (string?)o["resolution"] ?? "exact").OrderByDescending(ServiceModel.Rank).First()));
        }
        foreach (var group in receivers.GroupBy(o => (string)o["project"]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            b.Append(Arrow(contractNode, ServiceModel.NodeId(group.Key), contract.Kind == "grpc" ? "implemented by" : "consumed by",
                group.Select(o => (string?)o["resolution"] ?? "exact").OrderByDescending(ServiceModel.Rank).First()));
        }
        b.Append("```\n");
        return b.ToString();
    }

    private static string Arrow(string from, string to, string label, string resolution) => resolution switch
    {
        "exact" => $"  {from} -->|\"{ServiceModel.Label(label)}\"| {to}\n",
        "unresolved" => string.Empty,
        _ => $"  {from} -.->|\"{ServiceModel.Label(label)}, {resolution}\"| {to}\n",
    };
}
