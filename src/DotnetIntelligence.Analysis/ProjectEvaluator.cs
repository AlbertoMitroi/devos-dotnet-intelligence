using System.Text.Json;

namespace DotnetIntelligence.Analysis;

/// <param name="PackageId">Evaluated PackageId, PackageVersion (MSBuild "Version") and IsPackable (contract v1.1); null when not evaluated.</param>
public sealed record ProjectEvaluation(string ProjectPath, string? AssetsFile, IReadOnlyList<string> TargetFrameworks, string? Error,
    string? PackageId = null, string? PackageVersion = null, bool? IsPackable = null);

/// <summary>
/// Asks MSBuild for evaluated properties instead of assuming layouts: the restore output (ProjectAssetsFile) moves
/// with UseArtifactsOutput, BaseIntermediateOutputPath or MSBuildProjectExtensionsPath.
/// </summary>
public static class ProjectEvaluator
{
    public static async Task<IReadOnlyDictionary<string, ProjectEvaluation>> EvaluateAsync(
        string workRoot,
        IEnumerable<string> projects,
        IReadOnlyDictionary<string, string?> toolEnvironment,
        SecretScrubber scrubber,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workRoot).TrimEnd('/') + "/";
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, ProjectEvaluation>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(projects.Distinct(StringComparer.Ordinal), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        }, async (project, ct) =>
        {
            var full = Path.Combine(workRoot, project);
            var result = await ProcessRunner.RunAsync("dotnet",
                ["msbuild", full, "-nologo", "-nodeReuse:false", "-getProperty:ProjectAssetsFile", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks",
                 "-getProperty:PackageId", "-getProperty:Version", "-getProperty:IsPackable"],
                Path.GetDirectoryName(full), toolEnvironment, ct).ConfigureAwait(false);
            string? assets = null;
            string? error = null;
            string? packageId = null, packageVersion = null;
            bool? isPackable = null;
            var frameworks = new List<string>();
            if (result.ExitCode == 0)
            {
                try
                {
                    using var json = JsonDocument.Parse(result.StandardOutput);
                    var properties = json.RootElement.GetProperty("Properties");
                    assets = properties.GetProperty("ProjectAssetsFile").GetString();
                    var list = properties.GetProperty("TargetFrameworks").GetString();
                    var single = properties.GetProperty("TargetFramework").GetString();
                    frameworks.AddRange((string.IsNullOrWhiteSpace(list) ? single ?? string.Empty : list)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    packageId = NonEmpty(properties.GetProperty("PackageId").GetString());
                    packageVersion = NonEmpty(properties.GetProperty("Version").GetString());
                    isPackable = NonEmpty(properties.GetProperty("IsPackable").GetString()) is { } packable ? string.Equals(packable, "true", StringComparison.OrdinalIgnoreCase) : null;
                }
                catch (JsonException ex)
                {
                    error = "Unexpected msbuild -getProperty output: " + ex.Message;
                }
            }
            else
            {
                error = scrubber.Scrub(result.StandardOutputText + result.StandardError).Replace(root, string.Empty, StringComparison.Ordinal).Trim();
            }
            results[project] = new ProjectEvaluation(project, string.IsNullOrEmpty(assets) ? null : assets, frameworks, error, packageId, packageVersion, isPackable);
        }).ConfigureAwait(false);
        return results;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
