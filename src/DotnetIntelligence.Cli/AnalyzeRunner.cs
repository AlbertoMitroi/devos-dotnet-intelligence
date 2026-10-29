using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Adapters;
using DotnetIntelligence.Analysis.Extraction;
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
/// The analyze pipeline (4.2): working copy, discovery, preflight, restore, load, extraction, syntax fallback,
/// then the artifacts (graph, snippets, generated sources, context packs), manifest.json last and run.json.
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

            var inventory = await SdkInventory.ReadAsync(cancellationToken).ConfigureAwait(false);
            var scrubber = new SecretScrubber(environment);
            var analysis = await RepositoryAnalyzer.RunAsync(
                workingCopy, discovery, inventory, options.NuGetPackagesDirectory, $"dotnet-intelligence.roslyn@{EngineInfo.Version}", scrubber,
                name => events.Emit("stage.start", new JsonObject { ["stage"] = name }),
                timing =>
                {
                    stages.Add(new JsonObject { ["name"] = timing.Name, ["elapsedMs"] = timing.ElapsedMilliseconds });
                    events.Emit("stage.end", new JsonObject { ["stage"] = timing.Name, ["elapsedMs"] = timing.ElapsedMilliseconds });
                },
                cancellationToken).ConfigureAwait(false);

            stage.Restart();
            events.Emit("stage.start", new JsonObject { ["stage"] = "adapters" });
            var conventions = Conventions.Load(options.ConfigPath);
            var suggestions = AdapterRunner.Run(analysis.Semantic, analysis.Graph, analysis.Files, analysis.Identity, workingCopy.Files, conventions, cancellationToken);
            EndStage(events, stages, "adapters", stage);

            stage.Restart();
            events.Emit("stage.start", new JsonObject { ["stage"] = "links" });
            var links = Linker.Link(analysis.Graph.Observations.ToList(), analysis.Projects);
            AddPackageUsage(analysis);
            EndStage(events, stages, "links", stage);

            var diagnostics = new List<AnalysisDiagnostic>();
            diagnostics.AddRange(workingCopy.Diagnostics);
            diagnostics.AddRange(discovery.Diagnostics);
            diagnostics.AddRange(analysis.Diagnostics);
            if (configurationHash is not null)
            {
                diagnostics.Add(conventions.IsEmpty
                    ? new AnalysisDiagnostic(DiagnosticSeverity.Warning, "configuration-not-applied", "repository", DiagnosticState.Unsupported,
                        "The configuration file has no recognized section (messaging: publish, subscribe, handlerInterfaces); nothing was applied.")
                    : new AnalysisDiagnostic(DiagnosticSeverity.Info, "configuration-applied", "repository", DiagnosticState.Partial,
                        $"Messaging conventions applied: {conventions.Messaging.Publish.Count} publish, {conventions.Messaging.Subscribe.Count} subscribe, {conventions.Messaging.HandlerInterfaces.Count} handler interfaces; contract identity '{conventions.Messaging.ContractIdentity}'."));
            }
            var unresolvedLinks = links.Count(l => (string)l["resolution"]! == "unresolved");
            if (unresolvedLinks > 0)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "unresolved-links", "repository", DiagnosticState.Partial,
                    $"{unresolvedLinks} of {links.Count} links are unresolved; each has its reason in graph/links.ndjson."));
            }
            diagnostics = diagnostics.Select(d => d with { Message = scrubber.Scrub(d.Message) }).ToList();
            foreach (var diagnostic in diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
            {
                var fields = diagnostic.ToJson();
                fields.Remove("id");
                events.Emit("diagnostic", fields);
            }

            stage.Restart();
            events.Emit("stage.start", new JsonObject { ["stage"] = "artifacts" });
            var graph = analysis.Graph;
            var diagnosticRecords = diagnostics.Select(d => d.ToJson()).GroupBy(d => (string)d["id"]!, StringComparer.Ordinal).Select(g => g.First()).ToList();
            generation.WriteNdjson("graph/projects.ndjson", analysis.Projects.Concat(analysis.Contexts));
            generation.WriteNdjson("graph/entities.ndjson", graph.Entities);
            generation.WriteNdjson("graph/declarations.ndjson", graph.Declarations);
            generation.WriteNdjson("graph/relations.ndjson", graph.Relations);
            generation.WriteNdjson("graph/evidence.ndjson", graph.Evidence);
            generation.WriteNdjson("graph/observations.ndjson", graph.Observations);
            generation.WriteNdjson("graph/links.ndjson", links);
            if (!suggestions.IsEmpty)
            {
                generation.WriteBytes("suggested-conventions.yml", System.Text.Encoding.UTF8.GetBytes(suggestions.Render()));
            }
            generation.WriteNdjson("graph/diagnostics.ndjson", diagnosticRecords);
            generation.WriteNdjson("snippets.ndjson", graph.Snippets);
            foreach (var (path, content) in graph.GeneratedFiles.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                generation.WriteBytes(path, content);
            }

            var contexts = analysis.Contexts;
            var semantic = contexts.Count(c => (string)c["loadMode"]! == "semantic");
            var syntax = contexts.Count(c => (string)c["loadMode"]! == "syntax");
            var failed = contexts.Count(c => (string)c["loadMode"]! == "failed");
            var incomplete = diagnostics.Any(d => d.State is DiagnosticState.Failed or DiagnosticState.Partial or DiagnosticState.Unsupported && d.Severity != DiagnosticSeverity.Info);
            var status = semantic + syntax == 0 && discovery.Projects.Count > 0 ? "failed"
                : semantic == contexts.Count && !incomplete ? "complete"
                : "partial";
            var exitCode = analysis.AllBlocked ? ExitCodes.PreflightBlocked
                : status == "failed" ? ExitCodes.NothingLoaded
                : status == "complete" ? ExitCodes.Complete
                : ExitCodes.Partial;
            var coverage = new JsonObject
            {
                ["projects"] = new JsonArray(contexts.Select(c => (JsonNode)new JsonObject
                {
                    ["project"] = (string)c["projectId"]!,
                    ["path"] = (string)c["project"]!,
                    ["state"] = (string)c["loadMode"]!,
                }).OrderBy(n => (string)n!["path"]!, StringComparer.Ordinal).ToArray()),
                ["summary"] = new JsonObject
                {
                    ["total"] = contexts.Count,
                    ["semantic"] = semantic,
                    ["syntax"] = syntax,
                    ["failed"] = failed,
                    ["notAnalyzed"] = discovery.Projects.Count - contexts.Count,
                },
            };
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
                    ["resolvedSdk"] = analysis.Preflight.FirstOrDefault(p => p.SolutionPath == s.Path)?.ResolvedSdk,
                }).ToArray()),
                ["analysisContexts"] = new JsonArray(contexts.Select(c => (string)c["id"]!).Order(StringComparer.Ordinal).Select(n => (JsonNode)n).ToArray()),
                ["coverage"] = coverage,
            };

            var packs = ContextPackWriter.Render(new ContextPackWriter.Input(
                manifest,
                analysis.Projects.OrderBy(p => (string)p["path"]!, StringComparer.Ordinal).ToList(),
                contexts,
                graph.Entities.ToList(),
                graph.Declarations.ToList(),
                graph.Relations.ToList(),
                diagnosticRecords.OrderBy(d => (string)d["id"]!, StringComparer.Ordinal).ToList()));
            foreach (var (path, content) in packs)
            {
                generation.WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(content));
            }

            // 3.6: validate the generation before the manifest exists; a failed check means no manifest.
            var integrity = GenerationVerifier.VerifyRecords(generation.Root,
                workingCopy.Files.ToDictionary(f => f.Path, f => f.BlobId, StringComparer.Ordinal), workingCopy.ObjectFormat);
            if (integrity.Count > 0)
            {
                foreach (var problem in integrity.Take(50))
                {
                    events.Log("integrity: " + problem);
                }
                events.Emit("run.end", new JsonObject { ["status"] = "failed", ["exitCode"] = ExitCodes.IntegrityFailed, ["integrityErrors"] = integrity.Count });
                return new AnalyzeOutcome(ExitCodes.IntegrityFailed, "failed", null, discovery.Projects.Count, coverage, diagnostics);
            }
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
                ["peakWorkingSetBytes"] = Process.GetCurrentProcess().PeakWorkingSet64,
                ["stages"] = stages,
                ["offline"] = options.Offline,
                ["includeWorkingTree"] = options.IncludeWorkingTree,
                ["shallow"] = workingCopy.Shallow,
                ["nugetPackages"] = options.NuGetPackagesDirectory,
                ["counts"] = new JsonObject
                {
                    ["entities"] = graph.Entities.Count(),
                    ["declarations"] = graph.Declarations.Count(),
                    ["relations"] = graph.Relations.Count(),
                    ["evidence"] = graph.Evidence.Count(),
                    ["snippets"] = graph.Snippets.Count(),
                    ["generatedFiles"] = graph.GeneratedFiles.Count,
                    ["observations"] = graph.Observations.Count(),
                    ["links"] = links.Count,
                },
            });

            var manifestPath = Path.Combine(generation.Root, GenerationWriter.ManifestFileName);
            events.Emit("run.end", new JsonObject
            {
                ["status"] = status,
                ["exitCode"] = exitCode,
                ["manifest"] = manifestPath,
            });
            return new AnalyzeOutcome(exitCode, status, manifestPath, discovery.Projects.Count, coverage, diagnostics);
        }
        finally
        {
            foreach (var directory in new[] { workRoot, workRoot + "-home" })
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    /// <summary>4.4: the external API a project actually uses, grouped by package and version, from references and calls to metadata symbols.</summary>
    private static void AddPackageUsage(RepositoryAnalysis analysis)
    {
        var projectOf = analysis.Graph.Entities.Where(e => e["project"] is not null)
            .ToDictionary(e => (string)e["id"]!, e => (string)e["project"]!, StringComparer.Ordinal);
        var usage = new Dictionary<string, Dictionary<string, (HashSet<string> Symbols, int Usages)>>(StringComparer.Ordinal);
        foreach (var relation in analysis.Graph.Relations)
        {
            var kind = (string)relation["kind"]!;
            var to = (string)relation["to"]!;
            if (kind is not ("references" or "calls_declared_target") || !to.StartsWith("dotnet:pkg:", StringComparison.Ordinal)
                || !projectOf.TryGetValue((string)relation["from"]!, out var project))
            {
                continue;
            }
            var package = to["dotnet:pkg:".Length..to.IndexOf(':', "dotnet:pkg:".Length)];
            if (!usage.TryGetValue(project, out var packages))
            {
                usage[project] = packages = new(StringComparer.Ordinal);
            }
            (HashSet<string> Symbols, int Usages) entry = packages.TryGetValue(package, out var existing) ? existing : (new HashSet<string>(StringComparer.Ordinal), 0);
            entry.Symbols.Add(to);
            packages[package] = (entry.Symbols, entry.Usages + relation["evidence"]!.AsArray().Count);
        }
        foreach (var record in analysis.Projects)
        {
            var packages = usage.GetValueOrDefault((string)record["path"]!) ?? new();
            record["usedPackageApi"] = new JsonArray(packages.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            {
                var at = p.Key.LastIndexOf('@');
                return (JsonNode)new JsonObject
                {
                    ["package"] = p.Key[..at],
                    ["version"] = p.Key[(at + 1)..],
                    ["symbols"] = p.Value.Symbols.Count,
                    ["usages"] = p.Value.Usages,
                };
            }).ToArray());
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
