namespace DotnetIntelligence.Adapters;

/// <summary>Reverse index of calls_declared_target relations, for registration paths from composition roots.</summary>
public sealed class CallGraph
{
    private readonly Dictionary<string, List<string>> _callers = new(StringComparer.Ordinal);

    public CallGraph(IEnumerable<(string From, string To)> calls)
    {
        foreach (var (from, to) in calls)
        {
            if (!_callers.TryGetValue(to, out var list))
            {
                _callers[to] = list = [];
            }
            if (!list.Contains(from))
            {
                list.Add(from);
            }
        }
        foreach (var list in _callers.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Shortest caller chain ending at <paramref name="member"/>, starting at a member nobody calls (the composition
    /// root, typically the entry point). Ties are broken by ordinal order, so the path is deterministic.
    /// </summary>
    public IReadOnlyList<string> PathFromRoot(string member)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal) { [member] = null };
        var queue = new Queue<string>();
        queue.Enqueue(member);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!_callers.TryGetValue(current, out var callers) || callers.All(previous.ContainsKey))
            {
                if (!_callers.ContainsKey(current))
                {
                    var path = new List<string>();
                    for (string? step = current; step is not null; step = previous[step])
                    {
                        path.Add(step);
                    }
                    return path;
                }
                continue;
            }
            foreach (var caller in callers.Where(c => !previous.ContainsKey(c)))
            {
                previous[caller] = current;
                queue.Enqueue(caller);
            }
        }
        return [member];
    }
}
