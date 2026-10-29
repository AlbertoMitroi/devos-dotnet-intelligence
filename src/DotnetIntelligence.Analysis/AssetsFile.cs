using System.Text.Json;

namespace DotnetIntelligence.Analysis;

public sealed record AssetsLibrary(string Name, string Version, string Type, IReadOnlyList<string> Files, string? Path);

public sealed record AssetsPackageUse(string Name, string Version, bool Direct);

/// <summary>Reader for obj/project.assets.json (4.2.5, 4.4).</summary>
public sealed class AssetsFile
{
    private AssetsFile(
        IReadOnlyDictionary<string, AssetsLibrary> libraries,
        IReadOnlyDictionary<string, IReadOnlyList<string>> targets,
        IReadOnlyDictionary<string, IReadOnlySet<string>> directDependencies,
        IReadOnlyList<string> packageFolders)
    {
        Libraries = libraries;
        Targets = targets;
        DirectDependencies = directDependencies;
        PackageFolders = packageFolders;
    }

    /// <summary>Libraries keyed by "Name/Version".</summary>
    public IReadOnlyDictionary<string, AssetsLibrary> Libraries { get; }

    /// <summary>Target framework (as written in the assets file, for example "net10.0") to "Name/Version" keys.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Targets { get; }

    /// <summary>Target framework alias to direct dependency names.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> DirectDependencies { get; }

    public IReadOnlyList<string> PackageFolders { get; }

    public static AssetsFile? TryLoad(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        var libraries = new Dictionary<string, AssetsLibrary>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("libraries", out var libs))
        {
            foreach (var library in libs.EnumerateObject())
            {
                var slash = library.Name.IndexOf('/');
                var files = library.Value.TryGetProperty("files", out var f) ? f.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
                libraries[library.Name] = new AssetsLibrary(
                    library.Name[..slash],
                    library.Name[(slash + 1)..],
                    library.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown",
                    files,
                    library.Value.TryGetProperty("path", out var p) ? p.GetString() : null);
            }
        }

        var targets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("targets", out var targetsElement))
        {
            foreach (var target in targetsElement.EnumerateObject())
            {
                targets[target.Name] = target.Value.EnumerateObject().Select(x => x.Name).ToList();
            }
        }

        var direct = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks))
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (framework.Value.TryGetProperty("dependencies", out var dependencies))
                {
                    foreach (var dependency in dependencies.EnumerateObject())
                    {
                        names.Add(dependency.Name);
                    }
                }
                direct[framework.Name] = names;
            }
        }

        var folders = root.TryGetProperty("packageFolders", out var pf) ? pf.EnumerateObject().Select(x => x.Name).ToList() : [];
        return new AssetsFile(libraries, targets, direct, folders);
    }

    /// <summary>Packages used by one target framework: direct ones marked, transitive ones included.</summary>
    public IReadOnlyList<AssetsPackageUse> PackagesFor(string targetFramework)
    {
        var targetKey = Targets.Keys.FirstOrDefault(k => k.Split('/')[0].Equals(targetFramework, StringComparison.OrdinalIgnoreCase))
            ?? Targets.Keys.FirstOrDefault();
        if (targetKey is null)
        {
            return [];
        }
        var direct = DirectDependencies.TryGetValue(targetFramework, out var d) ? d : DirectDependencies.Values.FirstOrDefault() ?? new HashSet<string>();
        return Targets[targetKey]
            .Select(key => Libraries.TryGetValue(key, out var library) ? library : null)
            .Where(l => l is not null && l.Type == "package")
            .Select(l => new AssetsPackageUse(l!.Name, l.Version, direct.Contains(l.Name)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Absolute path of a package file, or null when no package folder contains it.</summary>
    public string? ResolvePackageFile(AssetsLibrary library, string file)
    {
        var relative = library.Path ?? $"{library.Name.ToLowerInvariant()}/{library.Version.ToLowerInvariant()}";
        return PackageFolders.Select(folder => System.IO.Path.Combine(folder, relative, file)).FirstOrDefault(File.Exists);
    }
}
