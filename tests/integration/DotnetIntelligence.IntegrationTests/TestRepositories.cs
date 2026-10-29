using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>Temporary Git repositories for tests, built with fixed author and committer data.</summary>
internal sealed class TestRepositories : IDisposable
{
    private static readonly Dictionary<string, string?> FixedIdentity = new()
    {
        ["GIT_AUTHOR_NAME"] = "Fixture",
        ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
        ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00Z",
        ["GIT_COMMITTER_NAME"] = "Fixture",
        ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid",
        ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    public string Root { get; } = Directory.CreateTempSubdirectory("di-tests-").FullName;

    public static string EngineRoot { get; } = FindEngineRoot();

    public string NewDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Copies a fixture from tests/fixtures and commits it with scripts/fixture-repo.sh.</summary>
    public async Task<(string Path, string Commit)> FromFixtureAsync(string fixture, string? name = null)
    {
        var target = Path.Combine(Root, name ?? fixture);
        var result = await ProcessRunner.RunAsync("bash", [
            Path.Combine(EngineRoot, "scripts", "fixture-repo.sh"),
            Path.Combine(EngineRoot, "tests", "fixtures", fixture),
            target]);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return (target, result.StandardOutputText.Trim());
    }

    /// <summary>An empty repository with fixed identity; files are added through <see cref="Git"/>.</summary>
    public async Task<string> InitAsync(string name, params string[] initArguments)
    {
        var path = NewDirectory(name);
        await Git(path, ["init", "-q", "-b", "main", .. initArguments]);
        await Git(path, ["config", "core.autocrlf", "false"]);
        return path;
    }

    public static async Task<string> Git(string repository, string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", ["-c", "safe.directory=*", "-C", repository, .. arguments], repository, FixedIdentity);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)}: {result.StandardError}");
        return result.StandardOutputText;
    }

    public static async Task CommitAllAsync(string repository, string message = "commit")
    {
        await Git(repository, ["add", "-A"]);
        await Git(repository, ["commit", "-q", "-m", message]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of a temporary directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Files chowned by a test may not be removable; the container is discarded anyway.
        }
    }

    private static string FindEngineRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotnetIntelligence.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Engine repository root not found.");
    }
}
