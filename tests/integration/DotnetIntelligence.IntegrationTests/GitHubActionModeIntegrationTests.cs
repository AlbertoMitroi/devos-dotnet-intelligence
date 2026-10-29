using DotnetIntelligence.Cli;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>R1.17 to R1.19: the gha command inside a simulated GitHub workspace.</summary>
[Collection(AnalyzeCollection.Name)]
public class GitHubActionModeIntegrationTests : IDisposable
{
    private readonly TestRepositories _repos = new();

    public void Dispose() => _repos.Dispose();

    private async Task<(int Code, Dictionary<string, string> Outputs, string Stdout)> RunAction(Dictionary<string, string?> inputs)
    {
        var workspace = Path.Combine(_repos.Root, "workspace");
        if (!Directory.Exists(Path.Combine(workspace, "repo")) && inputs.GetValueOrDefault("INPUT_PATH") != "partial")
        {
            Directory.CreateDirectory(workspace);
            await _repos.FromFixtureAsync("f00-minimal", Path.Combine("workspace", "repo"));
        }
        var outputFile = Path.Combine(_repos.Root, "github-output-" + Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string?>(inputs)
        {
            ["GITHUB_WORKSPACE"] = workspace,
            ["GITHUB_OUTPUT"] = outputFile,
            ["DI_WORK"] = _repos.NewDirectory("work-" + Guid.NewGuid().ToString("N")),
        };
        var stdout = new StringWriter();
        var code = await CommandLine.RunAsync(["gha"], stdout, TextWriter.Null, environment, CancellationToken.None);
        var outputs = File.Exists(outputFile)
            ? File.ReadAllLines(outputFile).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1])
            : [];
        return (code, outputs, stdout.ToString());
    }

    [Fact]
    public async Task Outputs_are_written_for_the_workflow()
    {
        var (code, outputs, _) = await RunAction(new() { ["INPUT_PATH"] = "repo", ["INPUT_OUTPUT"] = "out-a" });

        Assert.Equal(ExitCodes.Complete, code);
        Assert.Equal("out-a", outputs["generation-path"]);
        Assert.Equal("out-a/manifest.json", outputs["manifest-path"]);
        Assert.Equal("complete", outputs["status"]);
        Assert.Equal("""{"failed":0,"notAnalyzed":0,"semantic":1,"syntax":0,"total":1}""", outputs["coverage"]);
        Assert.True(File.Exists(Path.Combine(_repos.Root, "workspace", "out-a", "manifest.json")));
    }

    [Fact]
    public async Task Fail_on_partial_fails_the_step_with_the_engine_exit_code()
    {
        var partial = Path.Combine(_repos.Root, "workspace", "partial");
        Directory.CreateDirectory(Path.Combine(partial, "src", "B"));
        await TestRepositories.Git(partial, ["init", "-q", "-b", "main"]);
        // No TargetFramework: restore fails, the project falls back to syntax analysis, the generation is partial.
        await File.WriteAllTextAsync(Path.Combine(partial, "src", "B", "B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await File.WriteAllTextAsync(Path.Combine(partial, "src", "B", "B.cs"), "class B {}\n");
        await TestRepositories.CommitAllAsync(partial);

        var (code, outputs, _) = await RunAction(new() { ["INPUT_PATH"] = "partial", ["INPUT_OUTPUT"] = "out-b", ["INPUT_FAIL-ON"] = "partial" });

        Assert.Equal(ExitCodes.Partial, code);
        Assert.Equal("partial", outputs["status"]);
    }

    [Fact]
    public async Task Paths_outside_the_workspace_are_refused()
    {
        var (code, _, _) = await RunAction(new() { ["INPUT_PATH"] = "../elsewhere" });

        Assert.Equal(ExitCodes.Usage, code);
    }
}
