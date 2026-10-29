using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Artifacts;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Cli;

public sealed record AnalyzeOptions(
    string Source,
    string Output,
    string? ConfigPath,
    string? Solution,
    bool IncludeWorkingTree,
    bool Offline,
    string WorkBase,
    string NuGetPackagesDirectory,
    IReadOnlyCollection<string> ExcludedPaths);

public sealed record AnalyzeOutcome(
    int ExitCode,
    string Status,
    string? ManifestPath,
    int ProjectCount,
    JsonObject Coverage,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics);

/// <summary>
/// The analyze pipeline. Engine 0.1.0 runs stage 1 (working copy) and stage 2 (discovery) of 4.2 and
/// finalizes the generation; semantic extraction is not part of this version, so every generation is
/// "partial" and every project is "not-analyzed".
/// </summary>
public static class AnalyzeRunner
{
    public static async Task<AnalyzeOutcome> RunAsync(
        AnalyzeOptions options,
        EventWriter events,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stages = new JsonArray();
        var total = Stopwatch.StartNew();
        events.Emit("run.start", new JsonObject { ["engineVersion"] = EngineInfo.Version });

        string? configurationHash = null;
        if (options.ConfigPath is not null)
        {
            if (!File.Exists(options.ConfigPath))
            {
                throw new UsageException($"Configuration file '{options.ConfigPath}' does not exist.");
            }
            configurationHash = Hashing.Sha256Hex(await File.ReadAllBytesAsync(options.ConfigPath, cancellationToken).ConfigureAwait(false));
        }

        var generation = new GenerationWriter(options.Output);
        var workRoot = Path.Combine(options.WorkBase, "run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stage = Stopwatch.StartNew();
            events.Emit("stage.start", new JsonObject { ["stage"] = "export" });
            WorkingCopy workingCopy;
            try
            {
                workingCopy = await WorkingCopyExporter.ExportAsync(
                    options.Source, workRoot, options.IncludeWorkingTree, options.ExcludedPaths, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex)
            {
                events.Log($"error: {ex.Message}");
                events.Emit("run.end", new JsonObject { ["status"] = "failed", ["exitCode"] = ExitCodes.NothingLoaded });
                return new AnalyzeOutcome(ExitCodes.NothingLoaded, "failed", null, 0, Coverage([]), [
                    new AnalysisDiagnostic(DiagnosticSeverity.Error, "source-not-a-repository", "repository", DiagnosticState.Failed, ex.Message)]);
            }
            EndStage(events, stages, "export", stage);

            stage.Restart();
            events.Emit("stage.start", new JsonObject { ["stage"] = "discovery" });
            var discovery = SolutionDiscovery.Discover(workingCopy.Root, workingCopy.Files, options.Solution);
            EndStage(events, stages, "discovery", stage);

            var diagnostics = new List<AnalysisDiagnostic>();
            diagnostics.AddRange(workingCopy.Diagnostics);
            diagnostics.AddRange(discovery.Diagnostics);
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "extraction-not-available", "repository", DiagnosticState.Partial,
                $"Engine {EngineInfo.Version} performs working-copy export and solution discovery only; semantic extraction is not part of this version."));
            if (configurationHash is not null)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "configuration-not-applied", "repository", DiagnosticState.Unsupported,
                    $"Engine {EngineInfo.Version} records the configuration hash but does not apply configuration."));
            }
            foreach (var diagnostic in diagnostics)
            {
                var fields = diagnostic.ToJson();
                fields.Remove("id");
                events.Emit("diagnostic", fields);
            }

            stage.Restart();
            events.Emit("stage.start", new JsonObject { ["stage"] = "artifacts" });
            var projectRecords = discovery.Projects.Select(p => new JsonObject
            {
                ["id"] = ProjectId(p.Path),
                ["path"] = p.Path,
                ["solutions"] = new JsonArray(p.Solutions.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            }).ToList();
            generation.WriteNdjson("graph/projects.ndjson", projectRecords);
            generation.WriteNdjson("graph/diagnostics.ndjson", diagnostics.Select(d => d.ToJson()));

            var inventory = await SdkInventory.ReadAsync(cancellationToken).ConfigureAwait(false);
            const string status = "partial";
            var coverage = Coverage(discovery.Projects);
            var manifest = new JsonObject
            {
                ["schemaVersion"] = EngineInfo.SchemaVersion,
                ["requiredCapabilities"] = new JsonArray(),
                ["status"] = status,
                ["engine"] = new JsonObject { ["name"] = EngineInfo.Name, ["version"] = EngineInfo.Version },
                ["imageDigest"] = EngineInfo.ImageDigest(environment),
                ["roslyn"] = VersionCommand.RoslynJson(SdkInventory.EngineRoslyn()),
                ["sdks"] = VersionCommand.SdksJson(inventory.Sdks),
                ["repository"] = new JsonObject { ["remoteUrl"] = workingCopy.RemoteUrl },
                ["commit"] = workingCopy.Commit,
                ["objectFormat"] = workingCopy.ObjectFormat,
                ["dirty"] = workingCopy.Dirty,
                ["configurationHash"] = configurationHash,
                ["solutions"] = new JsonArray(discovery.Solutions.Select(s => (JsonNode)new JsonObject
                {
                    ["path"] = s.Path,
                    ["format"] = s.Format,
                }).ToArray()),
                ["analysisContexts"] = new JsonArray(),
                ["coverage"] = coverage,
            };
            generation.WriteManifest(manifest);
            EndStage(events, stages, "artifacts", stage);

            generation.WriteRun(new JsonObject
            {
                ["engineVersion"] = EngineInfo.Version,
                ["imageDigest"] = EngineInfo.ImageDigest(environment),
                ["host"] = new JsonObject
                {
                    ["os"] = EngineInfo.OperatingSystem,
                    ["architecture"] = EngineInfo.Architecture,
                    ["processorCount"] = Environment.ProcessorCount,
                },
                ["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
                ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["totalMs"] = total.ElapsedMilliseconds,
                ["stages"] = stages,
                ["offline"] = options.Offline,
                ["includeWorkingTree"] = options.IncludeWorkingTree,
                ["shallow"] = workingCopy.Shallow,
                ["nugetPackages"] = options.NuGetPackagesDirectory,
            });

            var manifestPath = Path.Combine(generation.Root, GenerationWriter.ManifestFileName);
            events.Emit("run.end", new JsonObject
            {
                ["status"] = status,
                ["exitCode"] = ExitCodes.Partial,
                ["manifest"] = manifestPath,
            });
            return new AnalyzeOutcome(ExitCodes.Partial, status, manifestPath, discovery.Projects.Count, coverage, diagnostics);
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    internal static string ProjectId(string path) => Hashing.RecordId("prj", new JsonObject { ["path"] = path });

    /// <summary>Coverage per project; "not-analyzed" is distinct from semantic, syntax and failed (rule 1.4.3).</summary>
    private static JsonObject Coverage(IReadOnlyList<DiscoveredProject> projects) => new()
    {
        ["projects"] = new JsonArray(projects.Select(p => (JsonNode)new JsonObject
        {
            ["project"] = ProjectId(p.Path),
            ["path"] = p.Path,
            ["state"] = "not-analyzed",
        }).ToArray()),
        ["summary"] = new JsonObject
        {
            ["total"] = projects.Count,
            ["semantic"] = 0,
            ["syntax"] = 0,
            ["failed"] = 0,
            ["notAnalyzed"] = projects.Count,
        },
    };

    private static void EndStage(EventWriter events, JsonArray stages, string name, Stopwatch stopwatch)
    {
        stages.Add(new JsonObject { ["name"] = name, ["elapsedMs"] = stopwatch.ElapsedMilliseconds });
        events.Emit("stage.end", new JsonObject { ["stage"] = name, ["elapsedMs"] = stopwatch.ElapsedMilliseconds });
    }
}
