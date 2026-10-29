using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotnetIntelligence.Analysis;

public enum LoadMode { Semantic, Syntax, Failed }

/// <summary>One analysis context: a project at one target framework and configuration (3.2).</summary>
public sealed record LoadedProject(
    string ProjectPath,
    string TargetFramework,
    string Configuration,
    Project? Project,
    Compilation? Compilation,
    LoadMode Mode,
    string Reason,
    int CompilationErrorCount,
    IReadOnlyList<string> AllTargetFrameworks);

/// <summary>
/// Stage 6 (4.2.6): loads restored projects with MSBuildWorkspace, one workspace per solution, each project once
/// (primary target framework: the first one MSBuild evaluates). Collects WorkspaceFailed diagnostics, analyzer and
/// generator load failures (CS8032 equivalent), generator references that produce no documents and error counts.
/// </summary>
public static partial class WorkspaceLoader
{
    public const string Configuration = "Debug";

    public static async Task<(IReadOnlyList<LoadedProject> Loaded, IReadOnlyList<AnalysisDiagnostic> Diagnostics)> LoadAsync(
        string workRoot,
        IReadOnlyList<(string Target, IReadOnlyList<string> Projects)> loadTargets,
        IReadOnlySet<string> projectsToLoad,
        IReadOnlyDictionary<string, ProjectEvaluation> evaluations,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workRoot).TrimEnd('/') + "/";
        string Relative(string path) => path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
        var loaded = new Dictionary<string, LoadedProject>(StringComparer.Ordinal);
        var diagnostics = new List<AnalysisDiagnostic>();

        foreach (var (target, members) in loadTargets)
        {
            if (members.Where(projectsToLoad.Contains).All(loaded.ContainsKey))
            {
                continue;
            }
            var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = Configuration });
            var failures = new List<WorkspaceDiagnostic>();
            workspace.RegisterWorkspaceFailedHandler(e => failures.Add(e.Diagnostic));
            Solution solution;
            try
            {
                solution = await workspace.OpenSolutionAsync(Path.Combine(workRoot, target), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Error, "solution-load-failed", target, DiagnosticState.Failed,
                    $"MSBuildWorkspace could not open '{target}': {ex.Message.Replace(root, string.Empty, StringComparison.Ordinal)}"));
                workspace.Dispose();
                continue;
            }

            foreach (var failure in failures)
            {
                var message = failure.Message.Replace(root, string.Empty, StringComparison.Ordinal);
                var scope = ProjectInMessage().Match(message) is { Success: true } m ? m.Value : target;
                if (!projectsToLoad.Contains(scope) && scope != target)
                {
                    continue;
                }
                diagnostics.Add(new AnalysisDiagnostic(
                    failure.Kind == WorkspaceDiagnosticKind.Failure ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                    failure.Kind == WorkspaceDiagnosticKind.Failure ? "workspace-failure" : "workspace-warning",
                    scope,
                    DiagnosticState.Partial,
                    message));
            }

            var byPath = solution.Projects
                .Where(p => p.FilePath is not null && p.Language == LanguageNames.CSharp)
                .GroupBy(p => Relative(Path.GetFullPath(p.FilePath!)), StringComparer.Ordinal);
            foreach (var group in byPath.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (!projectsToLoad.Contains(group.Key) || loaded.ContainsKey(group.Key))
                {
                    continue;
                }
                var instances = group.ToList();
                var evaluated = evaluations.TryGetValue(group.Key, out var evaluation) ? evaluation.TargetFrameworks : [];
                var frameworks = instances.Select(i => TargetFrameworkOf(i, evaluated)).ToList();
                var primary = instances[0];
                var compilation = await primary.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    loaded[group.Key] = new LoadedProject(group.Key, frameworks[0], Configuration, null, null, LoadMode.Syntax,
                        "MSBuildWorkspace returned no compilation.", 0, frameworks);
                    continue;
                }
                var errors = compilation.GetDiagnostics(cancellationToken).Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
                diagnostics.AddRange(await CheckGeneratorsAsync(group.Key, primary, cancellationToken).ConfigureAwait(false));
                if (errors > 0)
                {
                    diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "compilation-errors", group.Key, DiagnosticState.Partial,
                        $"The compilation of '{group.Key}' ({frameworks[0]}) has {errors} errors; they may belong to the project or to the load, and affected symbols can be unresolved."));
                }
                if (frameworks.Count > 1)
                {
                    diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "secondary-target-frameworks-not-analyzed", group.Key, DiagnosticState.Partial,
                        $"'{group.Key}' targets {string.Join(", ", frameworks)}; only {frameworks[0]} is analyzed (primary target framework)."));
                }
                loaded[group.Key] = new LoadedProject(group.Key, frameworks[0], Configuration, primary, compilation, LoadMode.Semantic,
                    errors == 0 ? "loaded" : $"loaded with {errors} compilation errors", errors, frameworks);
            }
            // The workspace stays alive through the loaded projects' compilations; it is released with them.
        }
        return (loaded.Values.OrderBy(p => p.ProjectPath, StringComparer.Ordinal).ToList(), diagnostics);
    }

    /// <summary>"net10.0" from "Name(net10.0)" (multi-targeting), the evaluated single target framework, or the output path.</summary>
    internal static string TargetFrameworkOf(Project project, IReadOnlyList<string> evaluated)
    {
        var name = NameSuffix().Match(project.Name);
        if (name.Success)
        {
            return name.Groups[1].Value;
        }
        if (evaluated.Count == 1)
        {
            return evaluated[0];
        }
        var output = project.OutputFilePath?.Replace('\\', '/');
        var segments = output?.Split('/') ?? [];
        var tfm = segments.LastOrDefault(s => TfmSegment().IsMatch(s));
        return tfm ?? "unknown";
    }

    private static async Task<IReadOnlyList<AnalysisDiagnostic>> CheckGeneratorsAsync(string projectPath, Project project, CancellationToken cancellationToken)
    {
        var diagnostics = new List<AnalysisDiagnostic>();
        var generatorCount = 0;
        foreach (var reference in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
        {
            var failures = new List<string>();
            void OnFailed(object? sender, AnalyzerLoadFailureEventArgs e) =>
                failures.Add($"{e.ErrorCode}: {e.Message}");
            reference.AnalyzerLoadFailed += OnFailed;
            try
            {
                generatorCount += reference.GetGenerators(LanguageNames.CSharp).Length;
                reference.GetAnalyzers(LanguageNames.CSharp);
            }
            finally
            {
                reference.AnalyzerLoadFailed -= OnFailed;
            }
            foreach (var failure in failures.Distinct(StringComparer.Ordinal))
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "analyzer-load-failed", projectPath, DiagnosticState.Partial,
                    $"Analyzer or generator '{Path.GetFileName(reference.FullPath)}' could not be loaded (compiler equivalent: CS8032): {failure}"));
            }
        }
        if (generatorCount > 0)
        {
            var generated = await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
            if (!generated.Any())
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "generators-produced-nothing", projectPath, DiagnosticState.Unknown,
                    $"'{projectPath}' references {generatorCount} source generators but no generated document was produced."));
            }
        }
        return diagnostics;
    }

    [GeneratedRegex(@"[^\s'""]+\.csproj")]
    private static partial Regex ProjectInMessage();

    [GeneratedRegex(@"\(([A-Za-z0-9.\-]+)\)$")]
    private static partial Regex NameSuffix();

    [GeneratedRegex(@"^net\d|^netstandard\d|^netcoreapp\d")]
    private static partial Regex TfmSegment();
}
