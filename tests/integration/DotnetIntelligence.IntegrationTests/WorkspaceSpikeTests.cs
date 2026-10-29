using DotnetIntelligence.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>
/// Spike S1 (PLAN section 4): MSBuildWorkspace 5.x loads a net10.0 solution in the image with only the
/// image's runtimes, through its out-of-process build host. The result is recorded in DECISIONS.md (D4).
/// </summary>
public class WorkspaceSpikeTests : IDisposable
{
    private readonly TestRepositories _repos = new();

    public void Dispose() => _repos.Dispose();

    [Fact]
    public async Task Msbuild_workspace_loads_the_minimal_fixture_semantically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (repo, _) = await _repos.FromFixtureAsync("f00-minimal");
        var restore = await ProcessRunner.RunAsync("dotnet", ["restore", Path.Combine(repo, "Minimal.slnx")], repo, cancellationToken: cancellationToken);
        Assert.True(restore.ExitCode == 0, restore.StandardOutputText + restore.StandardError);

        using var workspace = MSBuildWorkspace.Create();
        var failures = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(e => failures.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
        var solution = await workspace.OpenSolutionAsync(Path.Combine(repo, "Minimal.slnx"), cancellationToken: cancellationToken);

        var project = Assert.Single(solution.Projects);
        TestContext.Current.SendDiagnosticMessage("WorkspaceFailed: " + (failures.Count == 0 ? "none" : string.Join(" | ", failures)));
        Assert.DoesNotContain(failures, f => f.StartsWith("Failure", StringComparison.Ordinal));
        Assert.Contains(project.Documents, d => d.Name == "Greeter.cs");
        var compilation = await project.GetCompilationAsync(cancellationToken);
        Assert.NotNull(compilation);
        Assert.Empty(compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.NotNull(compilation.GetTypeByMetadataName("Minimal.Greeter"));
    }
}
