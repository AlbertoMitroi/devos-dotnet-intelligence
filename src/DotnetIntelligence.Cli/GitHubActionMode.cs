using System.Text;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Cli;

public enum FailOn { Failed, Partial, Never }

public sealed record ActionInputs(
    string Path,
    string? Solution,
    string? Config,
    string Output,
    bool IncludeWorkingTree,
    FailOn FailOn);

/// <summary>
/// GitHub Action mode (2.5, D12): reads INPUT_* variables, runs analyze inside the workspace, emits
/// workflow annotations, writes the step outputs to GITHUB_OUTPUT and applies fail-on.
/// </summary>
public static class GitHubActionMode
{
    /// <summary>NuGet cache inside the workspace, so callers can restore it with actions/cache.</summary>
    public const string CacheDirectory = ".dotnet-intelligence/nuget";

    public static ActionInputs ReadInputs(IReadOnlyDictionary<string, string?> environment)
    {
        string? Input(string name)
        {
            // Docker container actions receive inputs as INPUT_<NAME> with hyphens kept; accept the underscore form too.
            var upper = name.ToUpperInvariant();
            foreach (var key in new[] { "INPUT_" + upper, "INPUT_" + upper.Replace('-', '_') })
            {
                if (environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            return null;
        }

        var includeWorkingTree = Input("include-working-tree") switch
        {
            null or "false" => false,
            "true" => true,
            var other => throw new UsageException($"Input include-working-tree must be 'true' or 'false', not '{other}'."),
        };
        var failOn = Input("fail-on") switch
        {
            null or "failed" => FailOn.Failed,
            "partial" => FailOn.Partial,
            "never" => FailOn.Never,
            var other => throw new UsageException($"Input fail-on must be 'failed', 'partial' or 'never', not '{other}'."),
        };
        return new ActionInputs(
            Input("path") ?? ".",
            Input("solution"),
            Input("config"),
            Input("output") ?? "intelligence-out",
            includeWorkingTree,
            failOn);
    }

    /// <summary>
    /// The step outputs (5.6): status and coverage come from the written manifest, so they describe exactly the
    /// generation a later step uploads; without a manifest (failed run), from the run's outcome.
    /// </summary>
    public static StringBuilder Outputs(string workspace, string output, AnalyzeOutcome outcome)
    {
        var relativeOutput = Path.GetRelativePath(workspace, output).Replace('\\', '/');
        var manifest = outcome.ManifestPath is { } path && File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) : null;
        var lines = new StringBuilder();
        lines.Append("generation-path=").Append(relativeOutput).Append('\n');
        lines.Append("manifest-path=").Append(manifest is null ? string.Empty : relativeOutput + "/manifest.json").Append('\n');
        lines.Append("status=").Append((string?)manifest?["status"] ?? outcome.Status).Append('\n');
        lines.Append("coverage=").Append(CanonicalJson.Serialize(manifest?["coverage"]?["summary"] ?? outcome.Coverage["summary"])).Append('\n');
        return lines;
    }

    /// <summary>Whether the step fails for a given status under fail-on.</summary>
    public static bool ShouldFail(FailOn failOn, string status) => failOn switch
    {
        FailOn.Never => false,
        FailOn.Partial => status is "partial" or "failed",
        _ => status == "failed",
    };

    public static async Task<int> RunAsync(
        EventWriter events,
        TextWriter stdout,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var workspace = environment.GetValueOrDefault("GITHUB_WORKSPACE")
            ?? throw new UsageException("GITHUB_WORKSPACE is not set; the gha command runs inside a GitHub Docker container action.");
        var inputs = ReadInputs(environment);
        var source = ResolveInside(workspace, inputs.Path, "path");
        var output = ResolveInside(workspace, inputs.Output, "output");
        var cache = Path.Combine(workspace, CacheDirectory);
        var config = inputs.Config is null ? null : ResolveInside(workspace, inputs.Config, "config");
        var workBase = environment.TryGetValue("DI_WORK", out var work) && !string.IsNullOrWhiteSpace(work) ? work : "/work";

        var options = new AnalyzeOptions(source, output, config, inputs.Solution, inputs.IncludeWorkingTree, Offline: false, workBase,
            cache, [output, Path.Combine(workspace, ".dotnet-intelligence")]);
        var outcome = await AnalyzeRunner.RunAsync(options, events, environment, cancellationToken).ConfigureAwait(false);

        foreach (var diagnostic in outcome.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
        {
            await stdout.WriteLineAsync(Annotation(diagnostic)).ConfigureAwait(false);
        }

        var lines = Outputs(workspace, output, outcome);
        if (environment.GetValueOrDefault("GITHUB_OUTPUT") is { Length: > 0 } outputFile)
        {
            await File.AppendAllTextAsync(outputFile, lines.ToString(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            events.Log("GITHUB_OUTPUT is not set; outputs:\n" + lines);
        }

        await HandOverOwnershipAsync(workspace, [output, Path.Combine(workspace, ".dotnet-intelligence")], events, cancellationToken).ConfigureAwait(false);

        if (ShouldFail(inputs.FailOn, outcome.Status))
        {
            events.Log($"Failing the step: status '{outcome.Status}' with fail-on '{inputs.FailOn.ToString().ToLowerInvariant()}'.");
            return outcome.ExitCode == ExitCodes.Complete ? ExitCodes.InternalError : outcome.ExitCode;
        }
        return ExitCodes.Complete;
    }

    /// <summary>"::warning title=code::message", escaped as the workflow command format requires.</summary>
    public static string Annotation(AnalysisDiagnostic diagnostic)
    {
        var level = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"::{level} title={EscapeProperty(diagnostic.Code)}::{EscapeData($"{diagnostic.Message} [{diagnostic.Scope}]")}";
    }

    public static string EscapeData(string value) =>
        value.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    public static string EscapeProperty(string value) =>
        EscapeData(value).Replace(":", "%3A").Replace(",", "%2C");

    private static string ResolveInside(string workspace, string relative, string inputName)
    {
        var root = Path.GetFullPath(workspace).TrimEnd('/');
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (full != root && !full.StartsWith(root + "/", StringComparison.Ordinal))
        {
            throw new UsageException($"Input {inputName} must stay inside the workspace: '{relative}'.");
        }
        return full;
    }

    /// <summary>
    /// GitHub runs Docker container actions as root (D16); hand the output back to the workspace owner
    /// so later steps can read, cache and clean it.
    /// </summary>
    private static async Task HandOverOwnershipAsync(string workspace, IEnumerable<string> paths, EventWriter events, CancellationToken cancellationToken)
    {
        if (Environment.UserName != "root")
        {
            return;
        }
        foreach (var path in paths.Where(p => Directory.Exists(p)))
        {
            var result = await ProcessRunner.RunAsync("chown", ["-R", "--reference=" + workspace, path], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                events.Log($"warning: could not hand over ownership of '{path}': {result.StandardError.Trim()}");
            }
        }
    }
}
