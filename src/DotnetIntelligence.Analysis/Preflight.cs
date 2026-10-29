using System.Text.Json;

namespace DotnetIntelligence.Analysis;

public sealed record SolutionPreflight(string SolutionPath, string? GlobalJsonPath, string? RequestedSdk, string? ResolvedSdk, string? CompilerBuild)
{
    public bool Blocked => ResolvedSdk is null;
}

/// <summary>
/// Stage 3 (4.2.3): resolves each solution's SDK from its global.json with the SDKs in the image, using the
/// dotnet host's own resolution ("dotnet --version" in the solution directory). An unsatisfiable global.json
/// blocks only that solution.
/// </summary>
public static class Preflight
{
    public static async Task<(IReadOnlyList<SolutionPreflight> Solutions, IReadOnlyList<AnalysisDiagnostic> Diagnostics)> RunAsync(
        string workRoot,
        IReadOnlyList<DiscoveredSolution> solutions,
        IReadOnlyList<DiscoveredProject> projectsWithoutSolution,
        SdkInventoryResult inventory,
        IReadOnlyDictionary<string, string?> toolEnvironment,
        CancellationToken cancellationToken)
    {
        var results = new List<SolutionPreflight>();
        var diagnostics = new List<AnalysisDiagnostic>();
        var targets = solutions.Select(s => s.Path).ToList();
        if (targets.Count == 0)
        {
            // Projects without a solution are resolved from their own directories.
            targets.AddRange(projectsWithoutSolution.Select(p => p.Path));
        }
        foreach (var target in targets)
        {
            var directory = Path.GetDirectoryName(Path.Combine(workRoot, target))!;
            var globalJson = FindGlobalJson(workRoot, directory);
            var requested = globalJson is null ? null : ReadRequestedSdk(Path.Combine(workRoot, globalJson));
            var result = await ProcessRunner.RunAsync("dotnet", ["--version"], directory, toolEnvironment, cancellationToken).ConfigureAwait(false);
            var resolved = result.ExitCode == 0 ? result.StandardOutputText.Trim() : null;
            if (resolved is not null && inventory.Sdks.All(s => s.Version != resolved))
            {
                resolved = null;
            }
            var compiler = inventory.Sdks.FirstOrDefault(s => s.Version == resolved)?.CompilerVersion;
            results.Add(new SolutionPreflight(target, globalJson, requested, resolved, compiler));
            if (resolved is null)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Error, "unsupported-sdk", target, DiagnosticState.Unsupported,
                    $"'{globalJson ?? "global.json"}' requests SDK {requested ?? "unknown"}, which the image cannot satisfy (image SDKs: {string.Join(", ", inventory.Sdks.Select(s => s.Version))}). '{target}' is not analyzed."));
            }
        }
        return (results, diagnostics);
    }

    /// <summary>The nearest global.json from the directory up to the repository root, repository-relative.</summary>
    internal static string? FindGlobalJson(string workRoot, string directory)
    {
        var root = Path.GetFullPath(workRoot).TrimEnd('/');
        for (var current = Path.GetFullPath(directory); current.Length >= root.Length; current = Path.GetDirectoryName(current)!)
        {
            var candidate = Path.Combine(current, "global.json");
            if (File.Exists(candidate))
            {
                return Path.GetRelativePath(root, candidate).Replace('\\', '/');
            }
            if (current == root)
            {
                break;
            }
        }
        return null;
    }

    private static string? ReadRequestedSdk(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
