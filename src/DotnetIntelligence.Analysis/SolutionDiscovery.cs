using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotnetIntelligence.Analysis;

public sealed record DiscoveredSolution(string Path, string Format, IReadOnlyList<string> ProjectPaths);

public sealed record DiscoveredProject(string Path, IReadOnlyList<string> Solutions);

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredSolution> Solutions,
    IReadOnlyList<DiscoveredProject> Projects,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics);

/// <summary>
/// Stage 2 (4.2.2): finds .slnx, .sln and .slnf files in the working copy and the projects they contain.
/// Without any solution file, every C# project file becomes a project without a solution.
/// All paths are repository-relative with forward slashes.
/// </summary>
public static partial class SolutionDiscovery
{
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    public static DiscoveryResult Discover(string workRoot, IReadOnlyList<WorkingCopyFile> files, string? solutionFilter)
    {
        var diagnostics = new List<AnalysisDiagnostic>();
        var existing = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var solutionPaths = files.Select(f => f.Path)
            .Where(p => p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (solutionFilter is not null)
        {
            var normalized = NormalizeRelative(solutionFilter);
            if (!existing.Contains(normalized))
            {
                throw new FileNotFoundException($"Solution '{solutionFilter}' does not exist in the commit.");
            }
            solutionPaths = [normalized];
        }

        var solutions = new List<DiscoveredSolution>();
        foreach (var solutionPath in solutionPaths)
        {
            var text = File.ReadAllText(Path.Combine(workRoot, solutionPath));
            IReadOnlyList<string> projects;
            string format;
            try
            {
                (format, projects) = Path.GetExtension(solutionPath).ToLowerInvariant() switch
                {
                    ".slnx" => ("slnx", ParseSlnx(text, solutionPath)),
                    ".sln" => ("sln", ParseSln(text, solutionPath)),
                    _ => ("slnf", ParseSlnf(text, solutionPath)),
                };
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or JsonException or FormatException)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Error, "solution-unreadable", solutionPath, DiagnosticState.Failed,
                    $"Solution '{solutionPath}' could not be parsed: {ex.Message}"));
                continue;
            }

            var kept = new List<string>();
            foreach (var project in projects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "unsupported-project-type", project, DiagnosticState.Unsupported,
                        $"Project '{project}' in '{solutionPath}' is not a C# project and is not analyzed."));
                    continue;
                }
                if (!existing.Contains(project))
                {
                    diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Error, "project-file-missing", project, DiagnosticState.Failed,
                        $"Project '{project}' referenced by '{solutionPath}' is not present in the commit."));
                    continue;
                }
                kept.Add(project);
            }
            solutions.Add(new DiscoveredSolution(solutionPath, format, kept));
        }

        List<DiscoveredProject> discovered;
        if (solutionPaths.Count == 0)
        {
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "no-solution", "repository", DiagnosticState.Unknown,
                "No solution file was found; every C# project file is treated as a project without a solution."));
            discovered = files.Select(f => f.Path)
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Select(p => new DiscoveredProject(p, []))
                .ToList();
        }
        else
        {
            discovered = solutions
                .SelectMany(s => s.ProjectPaths.Select(p => (Project: p, Solution: s.Path)))
                .GroupBy(x => x.Project, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new DiscoveredProject(g.Key, g.Select(x => x.Solution).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
                .ToList();
        }

        return new DiscoveryResult(solutions, discovered, diagnostics);
    }

    internal static IReadOnlyList<string> ParseSlnx(string text, string solutionPath)
    {
        var document = XDocument.Parse(text);
        return document.Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ResolveRelative(solutionPath, p!))
            .ToList();
    }

    internal static IReadOnlyList<string> ParseSln(string text, string solutionPath)
    {
        var projects = new List<string>();
        foreach (Match match in SlnProjectLine().Matches(text))
        {
            if (string.Equals(match.Groups["type"].Value, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            projects.Add(ResolveRelative(solutionPath, match.Groups["path"].Value));
        }
        return projects;
    }

    internal static IReadOnlyList<string> ParseSlnf(string text, string solutionFilterPath)
    {
        using var document = JsonDocument.Parse(text);
        var solution = document.RootElement.GetProperty("solution");
        var solutionPath = ResolveRelative(solutionFilterPath, solution.GetProperty("path").GetString()
            ?? throw new FormatException("Missing solution.path."));
        return solution.GetProperty("projects").EnumerateArray()
            .Select(p => p.GetString() ?? throw new FormatException("Null project path."))
            .Select(p => ResolveRelative(solutionPath, p))
            .ToList();
    }

    /// <summary>Resolves a path written relative to a file's directory into a normalized repository-relative path.</summary>
    internal static string ResolveRelative(string fromFile, string relative)
    {
        var directory = Path.GetDirectoryName(fromFile.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
        var combined = directory.Length == 0 ? relative.Replace('\\', '/') : directory + "/" + relative.Replace('\\', '/');
        return NormalizeRelative(combined);
    }

    internal static string NormalizeRelative(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new FormatException($"Path '{path}' points outside the repository.");
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    [GeneratedRegex("""^Project\("\{(?<type>[0-9A-Fa-f-]+)\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();
}
