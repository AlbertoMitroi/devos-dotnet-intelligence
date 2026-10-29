using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis.Graph;
using DotnetIntelligence.Contracts;
using Microsoft.CodeAnalysis;

namespace DotnetIntelligence.Analysis.Extraction;

public sealed record StageTiming(string Name, long ElapsedMilliseconds);

public sealed record RepositoryAnalysis(
    GraphBuilder Graph,
    IReadOnlyList<JsonObject> Projects,
    IReadOnlyList<JsonObject> Contexts,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics,
    IReadOnlyList<SolutionPreflight> Preflight,
    bool AllBlocked);

/// <summary>Stages 3 to 9 of 4.2 on an exported working copy: preflight, restore, compatibility, load, extraction, fallback.</summary>
public static class RepositoryAnalyzer
{
    public static async Task<RepositoryAnalysis> RunAsync(
        WorkingCopy workingCopy,
        DiscoveryResult discovery,
        SdkInventoryResult inventory,
        string nugetPackages,
        string extractor,
        SecretScrubber scrubber,
        Action<string> stageStart,
        Action<StageTiming> stageEnd,
        CancellationToken cancellationToken)
    {
        var root = workingCopy.Root;
        var diagnostics = new List<AnalysisDiagnostic>();
        var toolEnvironment = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = nugetPackages,
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        };
        // NuGet and the dotnet CLI need a writable HOME; with "docker run --user <uid>" it is "/" and read-only.
        if (!IsWritableDirectory(Environment.GetEnvironmentVariable("HOME")))
        {
            var home = Path.Combine(Path.GetDirectoryName(root.TrimEnd('/'))!, Path.GetFileName(root.TrimEnd('/')) + "-home");
            Directory.CreateDirectory(home);
            toolEnvironment["HOME"] = home;
        }
        // The out-of-process build host of MSBuildWorkspace inherits the process environment.
        foreach (var (key, value) in toolEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        var stopwatch = Stopwatch.StartNew();
        stageStart("preflight");
        var projectsWithoutSolution = discovery.Solutions.Count == 0 ? discovery.Projects : [];
        var (preflight, preflightDiagnostics) = await Preflight.RunAsync(root, discovery.Solutions, projectsWithoutSolution, inventory, toolEnvironment, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(preflightDiagnostics);
        var resolvedSdk = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new List<(string Target, IReadOnlyList<string> Projects)>();
        foreach (var solution in preflight.Where(p => !p.Blocked))
        {
            var members = discovery.Solutions.FirstOrDefault(s => s.Path == solution.SolutionPath)?.ProjectPaths ?? [solution.SolutionPath];
            targets.Add((solution.SolutionPath, members));
            foreach (var member in members)
            {
                resolvedSdk.TryAdd(member, solution.ResolvedSdk!);
            }
        }
        targets = targets.OrderByDescending(t => t.Projects.Count).ThenBy(t => t.Target, StringComparer.Ordinal).ToList();
        stageEnd(new StageTiming("preflight", stopwatch.ElapsedMilliseconds));

        stopwatch.Restart();
        stageStart("restore");
        var evaluations = await ProjectEvaluator.EvaluateAsync(root, targets.SelectMany(t => t.Projects), toolEnvironment, scrubber, cancellationToken).ConfigureAwait(false);
        var (restores, restoreDiagnostics) = await RestoreRunner.RunAsync(root, targets, evaluations, toolEnvironment, scrubber, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(restoreDiagnostics);
        var assets = new Dictionary<string, AssetsFile>(StringComparer.Ordinal);
        var engineRoslyn = SdkInventory.EngineRoslyn().AssemblyVersion;
        foreach (var (project, restore) in restores)
        {
            if (restore.Restored && AssetsFile.TryLoad(RestoreRunner.AssetsPath(project, evaluations, root)) is { } file)
            {
                assets[project] = file;
                diagnostics.AddRange(AnalyzerCompatibility.Check(project, file, engineRoslyn));
            }
        }
        stageEnd(new StageTiming("restore", stopwatch.ElapsedMilliseconds));

        stopwatch.Restart();
        stageStart("load");
        var toLoad = restores.Values.Where(r => r.Restored).Select(r => r.ProjectPath).ToHashSet(StringComparer.Ordinal);
        var (loaded, loadDiagnostics) = await WorkspaceLoader.LoadAsync(root, targets, toLoad, evaluations, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(loadDiagnostics);
        var loadedByPath = loaded.ToDictionary(l => l.ProjectPath, StringComparer.Ordinal);
        stageEnd(new StageTiming("load", stopwatch.ElapsedMilliseconds));

        stopwatch.Restart();
        stageStart("extract");
        var graph = new GraphBuilder(extractor, scrubber);
        var files = new FileMap(root, workingCopy.ObjectFormat, workingCopy.Files, graph);
        var projectByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in loaded.Where(l => l.Compilation is not null))
        {
            if (!projectByAssembly.TryAdd(project.Compilation!.AssemblyName!, project.ProjectPath))
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "duplicate-assembly-name", project.ProjectPath, DiagnosticState.Partial,
                    $"Assembly name '{project.Compilation.AssemblyName}' is also produced by '{projectByAssembly[project.Compilation.AssemblyName!]}'; symbols are attributed to the first project."));
            }
        }
        var casing = assets.Values.SelectMany(a => a.Libraries.Values).Where(l => l.Type == "package")
            .GroupBy(l => l.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Name).Order(StringComparer.Ordinal).First(), StringComparer.Ordinal);
        var identity = new SymbolIdentity(projectByAssembly, nugetPackages, casing);

        var projectRecords = new List<JsonObject>();
        var contextRecords = new List<JsonObject>();
        var dispatchSites = new ConcurrentBag<DispatchSite>();
        var syntaxProjects = new List<(DiscoveredProject Project, string Context, string ProjectId, string Reason)>();
        foreach (var project in discovery.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectId = Hashing.RecordId("prj", new JsonObject { ["path"] = project.Path });
            loadedByPath.TryGetValue(project.Path, out var loadedProject);
            var tfm = loadedProject?.TargetFramework ?? "unknown";
            var contextId = Hashing.RecordId("ctx", new JsonObject { ["configuration"] = WorkspaceLoader.Configuration, ["project"] = project.Path, ["targetFramework"] = tfm });
            string mode;
            string reason;
            if (loadedProject is { Mode: LoadMode.Semantic })
            {
                await new ProjectExtractor(loadedProject, contextId, projectId, graph, files, identity, dispatchSites).ExtractAsync(cancellationToken).ConfigureAwait(false);
                mode = "semantic";
                reason = loadedProject.Reason;
            }
            else
            {
                reason = ReasonNotLoaded(project.Path, preflight, discovery, restores, loadedProject);
                syntaxProjects.Add((project, contextId, projectId, reason));
                mode = "syntax";
            }
            projectRecords.Add(ProjectRecord(project, projectId, loadedProject, resolvedSdk.GetValueOrDefault(project.Path), assets.GetValueOrDefault(project.Path), files));
            contextRecords.Add(new JsonObject
            {
                ["id"] = contextId,
                ["recordType"] = "analysisContext",
                ["project"] = project.Path,
                ["projectId"] = projectId,
                ["targetFramework"] = tfm,
                ["configuration"] = WorkspaceLoader.Configuration,
                ["preprocessorSymbols"] = new JsonArray((loadedProject?.Project?.ParseOptions?.PreprocessorSymbolNames ?? []).Order(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
                ["loadMode"] = mode,
                ["reason"] = reason,
                ["compilationErrors"] = loadedProject?.CompilationErrorCount ?? 0,
            });
        }
        var possible = DispatchResolver.Resolve(graph, dispatchSites);
        stageEnd(new StageTiming("extract", stopwatch.ElapsedMilliseconds));

        stopwatch.Restart();
        stageStart("syntax-fallback");
        foreach (var (project, contextId, projectId, _) in syntaxProjects)
        {
            var directory = Path.GetDirectoryName(project.Path)!.Replace('\\', '/');
            var prefix = directory.Length == 0 ? string.Empty : directory + "/";
            var nested = discovery.Projects.Select(p => Path.GetDirectoryName(p.Path)!.Replace('\\', '/'))
                .Where(d => d != directory && (prefix.Length == 0 || d.StartsWith(prefix, StringComparison.Ordinal))).Select(d => d + "/").ToList();
            var sources = workingCopy.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !nested.Any(n => f.Path.StartsWith(n, StringComparison.Ordinal))
                && !f.Path[prefix.Length..].StartsWith("bin/", StringComparison.Ordinal) && !f.Path[prefix.Length..].StartsWith("obj/", StringComparison.Ordinal)).ToList();
            var parsed = new SyntaxFallbackExtractor(project.Path, contextId, projectId, graph, files).Extract(sources, []);
            var context = contextRecords.First(c => (string)c["id"]! == contextId);
            if (parsed == 0)
            {
                context["loadMode"] = "failed";
                context["reason"] = (string)context["reason"]! + " No C# source file was found for the syntax fallback.";
            }
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "syntax-only", project.Path, DiagnosticState.Partial,
                $"'{project.Path}' was analyzed with the Roslyn parser only ({parsed} files): declarations and containment, no references, calls or types from other projects. Reason: {context["reason"]}"));
        }
        stageEnd(new StageTiming("syntax-fallback", stopwatch.ElapsedMilliseconds));

        if (possible > 0)
        {
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "possible-runtime-targets", "repository", DiagnosticState.Partial,
                $"{possible} possible runtime targets were inferred from interface and virtual calls to implementations known in this repository; implementations outside the repository are not known."));
        }

        return new RepositoryAnalysis(graph, projectRecords, contextRecords, diagnostics, preflight, targets.Count == 0 && discovery.Projects.Count > 0);
    }

    private static bool IsWritableDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return false;
        }
        try
        {
            var probe = Path.Combine(path, ".di-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReasonNotLoaded(string project, IReadOnlyList<SolutionPreflight> preflight, DiscoveryResult discovery,
        IReadOnlyDictionary<string, ProjectRestore> restores, LoadedProject? loaded)
    {
        var solutions = discovery.Projects.First(p => p.Path == project).Solutions;
        if (preflight.Where(p => solutions.Contains(p.SolutionPath) || p.SolutionPath == project).All(p => p.Blocked))
        {
            return "The SDK requested by global.json is not in the image.";
        }
        if (restores.TryGetValue(project, out var restore) && !restore.Restored)
        {
            return restore.Errors.Any(e => e.StartsWith("NETSDK1147", StringComparison.Ordinal))
                ? "Restore needs a workload the image does not contain."
                : "Restore failed.";
        }
        return loaded?.Reason ?? "MSBuildWorkspace did not load the project.";
    }

    private static JsonObject ProjectRecord(DiscoveredProject project, string projectId, LoadedProject? loaded, string? sdk, AssetsFile? assets, FileMap files)
    {
        var references = loaded?.Project is { } p
            ? p.ProjectReferences.Select(r => p.Solution.GetProject(r.ProjectId)?.FilePath).Where(f => f is not null)
                .Select(f => files.Relative(Path.GetFullPath(f!))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : [];
        var packages = assets is null || loaded is null ? [] : assets.PackagesFor(loaded.TargetFramework);
        return new JsonObject
        {
            ["id"] = projectId,
            ["recordType"] = "project",
            ["path"] = project.Path,
            ["solutions"] = new JsonArray(project.Solutions.Select(s => (JsonNode)s).ToArray()),
            ["assemblyName"] = loaded?.Compilation?.AssemblyName,
            ["targetFrameworks"] = new JsonArray((loaded?.AllTargetFrameworks ?? []).Select(t => (JsonNode)t).ToArray()),
            ["resolvedSdk"] = sdk,
            ["projectReferences"] = new JsonArray(references.Select(r => (JsonNode)r).ToArray()),
            ["packages"] = new JsonArray(packages.Select(pkg => (JsonNode)new JsonObject { ["id"] = pkg.Name, ["version"] = pkg.Version, ["direct"] = pkg.Direct }).ToArray()),
        };
    }
}
