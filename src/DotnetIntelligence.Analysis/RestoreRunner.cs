using System.Text.RegularExpressions;

namespace DotnetIntelligence.Analysis;

public sealed record ProjectRestore(string ProjectPath, bool Restored, IReadOnlyList<string> Errors);

/// <summary>
/// Stage 4 (4.2.4): "dotnet restore" per solution into NUGET_PACKAGES. A project that fails restore is reported
/// with the errors MSBuild attributed to it and the run continues. Missing workloads are "unsupported".
/// </summary>
public static partial class RestoreRunner
{
    public static async Task<(IReadOnlyDictionary<string, ProjectRestore> Projects, IReadOnlyList<AnalysisDiagnostic> Diagnostics)> RunAsync(
        string workRoot,
        IReadOnlyList<(string Target, IReadOnlyList<string> Projects)> restoreTargets,
        IReadOnlyDictionary<string, ProjectEvaluation> evaluations,
        IReadOnlyDictionary<string, string?> toolEnvironment,
        SecretScrubber scrubber,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workRoot).TrimEnd('/') + "/";
        var errorsByProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<AnalysisDiagnostic>();

        async Task RestoreAsync(string target, IEnumerable<string> projects)
        {
            var result = await ProcessRunner.RunAsync(
                "dotnet",
                ["restore", Path.Combine(workRoot, target), "-nologo", "-v:minimal", "-nodeReuse:false", "-p:NuGetAudit=false"],
                Path.GetDirectoryName(Path.Combine(workRoot, target)),
                toolEnvironment,
                cancellationToken).ConfigureAwait(false);
            foreach (var project in projects)
            {
                attempted.Add(project);
            }
            var output = scrubber.Scrub(result.StandardOutputText + "\n" + result.StandardError);
            foreach (Match match in MsBuildError().Matches(output))
            {
                // NuGet reports against the project file in front of the message; the bracket names the entry point (often the solution).
                var file = match.Groups["file"].Value.Trim();
                var projectFile = file.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || !match.Groups["project"].Success ? file : match.Groups["project"].Value;
                var relative = projectFile.StartsWith(root, StringComparison.Ordinal) ? projectFile[root.Length..] : projectFile;
                var message = $"{match.Groups["code"].Value}: {match.Groups["message"].Value.Replace(root, string.Empty, StringComparison.Ordinal).Trim()}";
                if (!errorsByProject.TryGetValue(relative, out var list))
                {
                    errorsByProject[relative] = list = [];
                }
                if (!list.Contains(message))
                {
                    list.Add(message);
                }
            }
        }

        bool HasAssets(string project) => AssetsPath(project, evaluations, workRoot) is { } path && File.Exists(path);

        foreach (var (target, projects) in restoreTargets)
        {
            if (projects.All(p => attempted.Contains(p) && HasAssets(p)))
            {
                continue;
            }
            await RestoreAsync(target, projects).ConfigureAwait(false);
        }

        // A solution restore stops at evaluation errors in one project (for example a missing workload, NETSDK1147),
        // leaving every other project without assets. Restore those projects one by one, so only the failing ones fail.
        var individually = attempted.Where(p => !HasAssets(p)).Order(StringComparer.Ordinal).ToList();
        if (individually.Count > 0 && restoreTargets.Count > 0)
        {
            var failedBefore = errorsByProject.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var project in individually)
            {
                if (HasAssets(project))
                {
                    continue;
                }
                // Errors reported against other projects during the solution restore are re-evaluated by the project restore.
                if (!failedBefore.Contains(project))
                {
                    errorsByProject.Remove(project);
                }
                await RestoreAsync(project, [project]).ConfigureAwait(false);
            }
        }

        var results = new Dictionary<string, ProjectRestore>(StringComparer.Ordinal);
        foreach (var project in attempted.Order(StringComparer.Ordinal))
        {
            var restored = HasAssets(project);
            var errors = errorsByProject.TryGetValue(project, out var e) ? e : [];
            // MSBuild reports "error" restore results against the project even when assets were written (for example NU1301 on a single feed).
            var failed = !restored || errors.Count > 0 && errors.Any(x => x.StartsWith("NETSDK1147", StringComparison.Ordinal));
            results[project] = new ProjectRestore(project, !failed, errors);
            if (failed)
            {
                var workload = errors.Any(x => x.StartsWith("NETSDK1147", StringComparison.Ordinal));
                diagnostics.Add(new AnalysisDiagnostic(
                    workload ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    workload ? "missing-workload" : "restore-failed",
                    project,
                    workload ? DiagnosticState.Unsupported : DiagnosticState.Failed,
                    errors.Count > 0 ? string.Join(" | ", errors) : "Restore did not produce the project's assets file (ProjectAssetsFile)."));
            }
            else if (errors.Count > 0)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "restore-errors", project, DiagnosticState.Partial,
                    string.Join(" | ", errors)));
            }
        }
        return (results, diagnostics);
    }

    /// <summary>The evaluated ProjectAssetsFile, or obj/project.assets.json when MSBuild could not evaluate the project.</summary>
    public static string? AssetsPath(string project, IReadOnlyDictionary<string, ProjectEvaluation> evaluations, string workRoot) =>
        evaluations.TryGetValue(project, out var evaluation) && evaluation.AssetsFile is { } evaluated
            ? evaluated
            : Path.Combine(workRoot, Path.GetDirectoryName(project)!, "obj", "project.assets.json");

    // "<file>(line,col): error CODE: message [<project>]" or "<file> : error CODE: message [<project>]"
    [GeneratedRegex(@"^\s*(?<file>[^\r\n:]+?)(?:\(\d+(?:,\d+)*\))?\s*:\s*error\s+(?<code>[A-Z]+\d+)\s*:\s*(?<message>[^\r\n]*?)(?:\s+\[(?<project>[^\]\r\n]+?)(?:::[^\]]*)?\])?\s*$", RegexOptions.Multiline)]
    private static partial Regex MsBuildError();
}
