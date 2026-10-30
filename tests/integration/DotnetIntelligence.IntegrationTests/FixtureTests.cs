using System.Text.Json.Nodes;
using DotnetIntelligence.Artifacts;
using DotnetIntelligence.Cli;
using YamlDotNet.RepresentationModel;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>
/// Fixtures 1 to 15 (4.5): each fixture has a hand-written expected.yml. Every declared scope is compared as a set,
/// in both directions (full precision and recall): load modes, warning and error diagnostics, declarations of the
/// listed entities, relations of the listed kinds and prefixes, observations of the listed kinds, and all links.
/// </summary>
[Collection(AnalyzeCollection.Name)]
public class FixtureTests : IDisposable
{
    private readonly TestRepositories _repos = new();

    public void Dispose() => _repos.Dispose();

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        // DI_FIXTURES=f19-masstransit,f20-... limits the run while iterating on a few fixtures; the image runs them all.
        var only = Environment.GetEnvironmentVariable("DI_FIXTURES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(TestRepositories.EngineRoot, "tests", "fixtures")).Order(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(directory, "expected.yml")) && (only is null || only.Contains(Path.GetFileName(directory))))
            {
                data.Add(Path.GetFileName(directory));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_matches_expected(string fixture)
    {
        var fixtureDirectory = Path.Combine(TestRepositories.EngineRoot, "tests", "fixtures", fixture);
        var expected = ExpectedYaml.Load(Path.Combine(fixtureDirectory, "expected.yml"));
        var (repo, _) = await _repos.FromFixtureAsync(fixture);
        var failures = new List<string>();
        var runIndex = 0;
        foreach (var run in expected["runs"]?.AsArray().Select(r => r!.AsObject()) ?? [expected])
        {
            var output = Path.Combine(_repos.Root, $"out-{runIndex++}");
            var args = new List<string> { "analyze", "--src", repo, "--out", output };
            if ((string?)run["config"] is { } config)
            {
                args.AddRange(["--config", Path.Combine(fixtureDirectory, config)]);
            }
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await CommandLine.RunAsync([.. args], stdout, stderr,
                new Dictionary<string, string?> { ["DI_WORK"] = _repos.NewDirectory($"work-{runIndex}") }, CancellationToken.None);
            var label = (string?)run["config"] is { } c ? $"[with {c}] " : string.Empty;
            failures.AddRange(FixtureComparer.Compare(run, code, output, stderr.ToString()).Select(f => label + f));
            if (File.Exists(Path.Combine(output, "manifest.json")))
            {
                // G4.3: every fixture generation passes verify.
                failures.AddRange(GenerationVerifier.Verify(output).Select(e => $"{label}verify: {e}"));
                // G4.4: the fixture questions, answered by query with path:line evidence.
                failures.AddRange((await QueryFailuresAsync(run, output)).Select(f => label + f));
            }
        }
        Assert.True(failures.Count == 0, $"{fixture}:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>"queries:" in expected.yml: each query's exit code and the lines its text output must contain.</summary>
    private static async Task<List<string>> QueryFailuresAsync(JsonObject run, string output)
    {
        var failures = new List<string>();
        foreach (var item in run["queries"]?.AsArray().Select(q => q!.AsObject()) ?? [])
        {
            var query = (string)item["query"]!;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await CommandLine.RunAsync(["query", output, .. query.Split(' ', StringSplitOptions.RemoveEmptyEntries)], stdout, stderr,
                new Dictionary<string, string?>(), CancellationToken.None);
            var text = stdout.ToString();
            if ((string?)item["exitCode"] is { } expectedCode && int.Parse(expectedCode, System.Globalization.CultureInfo.InvariantCulture) != code)
            {
                failures.Add($"query '{query}': exit code {code}, expected {expectedCode}. Output: {text.Trim()} {stderr.ToString().Trim()}");
            }
            foreach (var line in item["lines"]?.AsArray().Select(l => (string)l!) ?? [])
            {
                if (!text.Contains(line, StringComparison.Ordinal))
                {
                    failures.Add($"query '{query}': output does not contain '{line}'. Output:\n{text}");
                }
            }
        }
        return failures;
    }
}

/// <summary>expected.yml as JSON nodes; scalars stay strings.</summary>
internal static class ExpectedYaml
{
    public static JsonObject Load(string path)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(path)));
        return Convert(stream.Documents[0].RootNode)!.AsObject();
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, Convert(c.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
        YamlScalarNode { Value: "null" or "~" } => null,
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null,
    };
}
