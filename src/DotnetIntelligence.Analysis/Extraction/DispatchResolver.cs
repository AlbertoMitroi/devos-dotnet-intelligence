using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis.Graph;

namespace DotnetIntelligence.Analysis.Extraction;

/// <summary>
/// After the pass (4.2.7): possible_runtime_target from interface or virtual calls to the known implementations,
/// following implements and overrides edges transitively. These are possible, not proven, targets (rule 1.4.5).
/// </summary>
public static class DispatchResolver
{
    public static int Resolve(GraphBuilder graph, IEnumerable<DispatchSite> sites)
    {
        // Reverse index: declared member id -> members that implement or override it directly.
        var implementers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var relation in graph.Relations)
        {
            var kind = (string)relation["kind"]!;
            if (kind is not ("implements" or "overrides"))
            {
                continue;
            }
            var from = (string)relation["from"]!;
            var to = (string)relation["to"]!;
            if (!IsMember(from))
            {
                continue;
            }
            if (!implementers.TryGetValue(to, out var set))
            {
                implementers[to] = set = new HashSet<string>(StringComparer.Ordinal);
            }
            set.Add(from);
        }

        var count = 0;
        foreach (var site in sites)
        {
            foreach (var target in Closure(site.DeclaredTarget, implementers))
            {
                graph.AddRelation("possible_runtime_target", site.From, target, site.Context, "candidate", site.EvidenceId,
                    new JsonObject { ["declaredTarget"] = site.DeclaredTarget });
                count++;
            }
        }
        return count;
    }

    private static bool IsMember(string id) =>
        id.Contains(":M:", StringComparison.Ordinal) || id.Contains(":P:", StringComparison.Ordinal) || id.Contains(":E:", StringComparison.Ordinal);

    private static IEnumerable<string> Closure(string start, Dictionary<string, HashSet<string>> implementers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!implementers.TryGetValue(current, out var next))
            {
                continue;
            }
            foreach (var item in next)
            {
                if (seen.Add(item))
                {
                    queue.Enqueue(item);
                }
            }
        }
        return seen.Order(StringComparer.Ordinal);
    }
}
