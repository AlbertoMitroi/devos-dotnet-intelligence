using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Extraction;

namespace DotnetIntelligence.Adapters;

/// <summary>Project references among the loaded projects, both directions, transitively.</summary>
public sealed class ProjectGraph
{
    private readonly Dictionary<string, SortedSet<string>> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<string>> _referencedBy = new(StringComparer.Ordinal);

    public ProjectGraph(IReadOnlyList<LoadedProject> projects, FileMap files)
    {
        foreach (var project in projects.Where(p => p.Project is not null))
        {
            foreach (var reference in project.Project!.ProjectReferences)
            {
                if (project.Project.Solution.GetProject(reference.ProjectId)?.FilePath is { } path)
                {
                    var target = files.Relative(Path.GetFullPath(path));
                    Add(_references, project.ProjectPath, target);
                    Add(_referencedBy, target, project.ProjectPath);
                }
            }
        }
    }

    /// <summary>The project itself first, then every project that references it (the projects that run its code), ordinal.</summary>
    public IReadOnlyList<string> Runners(string project) => [project, .. Closure(_referencedBy, project)];

    /// <summary>The project itself first, then every project it references, ordinal.</summary>
    public IReadOnlyList<string> Dependencies(string project) => [project, .. Closure(_references, project)];

    private static IEnumerable<string> Closure(Dictionary<string, SortedSet<string>> edges, string start)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([start]);
        while (stack.Count > 0)
        {
            foreach (var next in edges.GetValueOrDefault(stack.Pop()) ?? [])
            {
                if (next != start && seen.Add(next))
                {
                    stack.Push(next);
                }
            }
        }
        return seen;
    }

    private static void Add(Dictionary<string, SortedSet<string>> edges, string from, string to)
    {
        if (!edges.TryGetValue(from, out var set))
        {
            edges[from] = set = new SortedSet<string>(StringComparer.Ordinal);
        }
        set.Add(to);
    }
}
