using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetIntelligence.Cli;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>Skeleton analyze (Gate 1): layout, manifest written last with checksums, exit codes, determinism, no secrets.</summary>
public class AnalyzeEndToEndTests : IDisposable
{
    private const string CredentialSentinel = "Sentinel-Credential-7f3a9c";
    private const string UrlSentinel = "ghs_UrlTokenSentinel4242";

    private readonly TestRepositories _repos = new();

    public void Dispose() => _repos.Dispose();

    private Dictionary<string, string?> Environment() => new()
    {
        ["DI_WORK"] = _repos.NewDirectory("work"),
        ["NuGetPackageSourceCredentials_private"] = $"Username=ci;Password={CredentialSentinel}",
    };

    private static async Task<(int Code, string Stdout, string Stderr)> RunCli(string[] args, IReadOnlyDictionary<string, string?> environment)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CommandLine.RunAsync(args, stdout, stderr, environment, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Analyze_produces_a_partial_generation_with_a_valid_manifest()
    {
        var (repo, commit) = await _repos.FromFixtureAsync("f00-minimal");
        var output = Path.Combine(_repos.Root, "out");

        var (code, stdout, stderr) = await RunCli(["analyze", "--src", repo, "--out", output], Environment());

        Assert.True(code == ExitCodes.Partial, stderr);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")))!.AsObject();
        Assert.Equal("partial", (string?)manifest["status"]);
        Assert.Equal(commit, (string?)manifest["commit"]);
        Assert.False((bool)manifest["dirty"]!);
        Assert.Equal("Minimal.slnx", (string?)manifest["solutions"]![0]!["path"]);
        Assert.Equal("not-analyzed", (string?)manifest["coverage"]!["projects"]![0]!["state"]);
        Assert.True(File.Exists(Path.Combine(output, "run.json")));

        var listed = manifest["files"]!.AsArray().Select(f => (Path: (string)f!["path"]!, Sha: (string)f["sha256"]!)).ToList();
        Assert.Equal(["graph/diagnostics.ndjson", "graph/projects.ndjson"], listed.Select(f => f.Path));
        Assert.All(listed, f => Assert.Equal(f.Sha, Hashing.Sha256HexOfFile(Path.Combine(output, f.Path))));

        var project = JsonNode.Parse(File.ReadLines(Path.Combine(output, "graph/projects.ndjson")).Single())!;
        Assert.Equal("src/Minimal/Minimal.csproj", (string?)project["path"]);
        Assert.Equal(["Minimal.slnx"], project["solutions"]!.AsArray().Select(s => (string)s!));

        var events = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal("run.start", (string?)events.First()["event"]);
        Assert.Equal("run.end", (string?)events.Last()["event"]);
        Assert.All(events, e => Assert.NotNull(e["event"]));
    }

    [Fact]
    public async Task Two_runs_produce_byte_identical_artifacts_except_run_json()
    {
        var (repo, _) = await _repos.FromFixtureAsync("f00-minimal");
        var first = Path.Combine(_repos.Root, "first");
        var second = Path.Combine(_repos.Root, "second");

        Assert.Equal(ExitCodes.Partial, (await RunCli(["analyze", "--src", repo, "--out", first], Environment())).Code);
        Assert.Equal(ExitCodes.Partial, (await RunCli(["analyze", "--src", repo, "--out", second], Environment())).Code);

        var files = Directory.EnumerateFiles(first, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(first, f))
            .Where(f => f != "run.json")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Contains("manifest.json", files);
        Assert.All(files, f => Assert.Equal(File.ReadAllBytes(Path.Combine(first, f)), File.ReadAllBytes(Path.Combine(second, f))));
    }

    [Fact]
    public async Task Credentials_never_reach_outputs_or_logs()
    {
        var (repo, _) = await _repos.FromFixtureAsync("f00-minimal");
        await TestRepositories.Git(repo, ["remote", "add", "origin", $"https://x-access-token:{UrlSentinel}@example.invalid/org/repo.git"]);
        var output = Path.Combine(_repos.Root, "out");

        var (code, stdout, stderr) = await RunCli(["analyze", "--src", repo, "--out", output], Environment());

        Assert.Equal(ExitCodes.Partial, code);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")))!;
        Assert.Equal("https://example.invalid/org/repo.git", (string?)manifest["repository"]!["remoteUrl"]);
        var everything = new StringBuilder(stdout).Append(stderr);
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            everything.Append(await File.ReadAllTextAsync(file));
        }
        Assert.DoesNotContain(CredentialSentinel, everything.ToString());
        Assert.DoesNotContain(UrlSentinel, everything.ToString());
    }

    [Fact]
    public async Task Configuration_is_hashed_and_reported_as_not_applied()
    {
        var (repo, _) = await _repos.FromFixtureAsync("f00-minimal");
        var config = Path.Combine(_repos.Root, "di.yml");
        await File.WriteAllTextAsync(config, "conventions: []\n");
        var output = Path.Combine(_repos.Root, "out");

        Assert.Equal(ExitCodes.Partial, (await RunCli(["analyze", "--src", repo, "--out", output, "--config", config], Environment())).Code);

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")))!;
        Assert.Equal(Hashing.Sha256Hex("conventions: []\n"), (string?)manifest["configurationHash"]);
        Assert.Contains(File.ReadLines(Path.Combine(output, "graph/diagnostics.ndjson")),
            l => JsonDocument.Parse(l).RootElement.GetProperty("code").GetString() == "configuration-not-applied");
    }

    [Fact]
    public async Task A_source_that_is_not_a_repository_exits_30_without_manifest()
    {
        var plain = _repos.NewDirectory("plain");
        var output = Path.Combine(_repos.Root, "out");

        var (code, _, _) = await RunCli(["analyze", "--src", plain, "--out", output], Environment());

        Assert.Equal(ExitCodes.NothingLoaded, code);
        Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
    }

    [Fact]
    public async Task A_non_empty_output_directory_is_refused()
    {
        var (repo, _) = await _repos.FromFixtureAsync("f00-minimal");
        var output = _repos.NewDirectory("used");
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "x");

        var (code, _, stderr) = await RunCli(["analyze", "--src", repo, "--out", output], Environment());

        Assert.Equal(ExitCodes.InternalError, code);
        Assert.Contains("not empty", stderr);
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(output, "keep.txt")));
    }

    [Fact]
    public async Task Repositories_without_solution_fall_back_to_project_files()
    {
        var repo = await _repos.InitAsync("nosln");
        Directory.CreateDirectory(Path.Combine(repo, "src", "B"));
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "B", "B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await TestRepositories.CommitAllAsync(repo);
        var output = Path.Combine(_repos.Root, "out");

        Assert.Equal(ExitCodes.Partial, (await RunCli(["analyze", "--src", repo, "--out", output], Environment())).Code);

        Assert.Contains("src/B/B.csproj", await File.ReadAllTextAsync(Path.Combine(output, "graph/projects.ndjson")));
        Assert.Contains("\"code\":\"no-solution\"", await File.ReadAllTextAsync(Path.Combine(output, "graph/diagnostics.ndjson")));
    }
}
