using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>R1.10 to R1.13: committed-state export, working-tree mode, shallow clones, submodules, safe.directory.</summary>
public class WorkingCopyTests : IDisposable
{
    private readonly TestRepositories _repos = new();

    public void Dispose() => _repos.Dispose();

    private string Work(string name) => Path.Combine(_repos.Root, "work-" + name);

    [Fact]
    public async Task Export_writes_the_committed_bytes_regardless_of_eol_attributes()
    {
        var repo = await _repos.InitAsync("eol");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "*.cs text eol=crlf\n*.bin -text\n");
        await File.WriteAllBytesAsync(Path.Combine(repo, "A.cs"), "class A {}\n"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(repo, "data.bin"), "x\r\ny\r\n"u8.ToArray());
        await TestRepositories.CommitAllAsync(repo);

        var copy = await WorkingCopyExporter.ExportAsync(repo, Work("eol"), includeWorkingTree: false, []);

        Assert.Equal("class A {}\n"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(copy.Root, "A.cs")));
        Assert.Equal("x\r\ny\r\n"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(copy.Root, "data.bin")));
        foreach (var file in copy.Files)
        {
            var hashed = (await TestRepositories.Git(repo, ["rev-parse", "HEAD:" + file.Path])).Trim();
            Assert.Equal(hashed, file.BlobId);
            Assert.Equal(hashed, WorkingCopyExporter.ComputeBlobId(await File.ReadAllBytesAsync(Path.Combine(copy.Root, file.Path)), "sha1"));
        }
        Assert.False(copy.Dirty);
        Assert.Equal((await TestRepositories.Git(repo, ["rev-parse", "HEAD"])).Trim(), copy.Commit);
    }

    [Fact]
    public async Task Committed_mode_ignores_local_changes_and_working_tree_mode_marks_dirty()
    {
        var repo = await _repos.InitAsync("dirty");
        await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "class A {}\n");
        await TestRepositories.CommitAllAsync(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "class A { int x; }\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "New.cs"), "class New {}\n");
        Directory.CreateDirectory(Path.Combine(repo, "intelligence-out"));
        await File.WriteAllTextAsync(Path.Combine(repo, "intelligence-out", "manifest.json"), "{}\n");

        var committed = await WorkingCopyExporter.ExportAsync(repo, Work("committed"), includeWorkingTree: false, []);
        var working = await WorkingCopyExporter.ExportAsync(repo, Work("working"), includeWorkingTree: true, [Path.Combine(repo, "intelligence-out")]);

        Assert.False(committed.Dirty);
        Assert.Equal("class A {}\n", await File.ReadAllTextAsync(Path.Combine(committed.Root, "A.cs")));
        Assert.DoesNotContain(committed.Files, f => f.Path == "New.cs");
        Assert.True(working.Dirty);
        Assert.Equal("class A { int x; }\n", await File.ReadAllTextAsync(Path.Combine(working.Root, "A.cs")));
        Assert.Contains(working.Files, f => f.Path == "New.cs");
        Assert.DoesNotContain(working.Files, f => f.Path.StartsWith("intelligence-out/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Working_tree_mode_without_changes_is_clean()
    {
        var repo = await _repos.InitAsync("clean");
        await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "class A {}\n");
        await TestRepositories.CommitAllAsync(repo);

        var working = await WorkingCopyExporter.ExportAsync(repo, Work("clean"), includeWorkingTree: true, []);

        Assert.False(working.Dirty);
    }

    [Fact]
    public async Task Uninitialized_submodules_and_symlinks_become_diagnostics()
    {
        var repo = await _repos.InitAsync("links");
        await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "class A {}\n");
        File.CreateSymbolicLink(Path.Combine(repo, "link.cs"), "A.cs");
        await TestRepositories.Git(repo, ["add", "-A"]);
        await TestRepositories.Git(repo, ["update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,external/lib"]);
        await TestRepositories.Git(repo, ["commit", "-q", "-m", "links"]);

        var copy = await WorkingCopyExporter.ExportAsync(repo, Work("links"), includeWorkingTree: false, []);

        Assert.Contains(copy.Diagnostics, d => d.Code == "submodule-uninitialized" && d.Scope == "external/lib" && d.State == DiagnosticState.Unknown);
        Assert.Contains(copy.Diagnostics, d => d.Code == "symlink-skipped" && d.Scope == "link.cs");
        Assert.Equal(["A.cs"], copy.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Shallow_clones_are_exported()
    {
        var origin = await _repos.InitAsync("origin");
        await File.WriteAllTextAsync(Path.Combine(origin, "A.cs"), "class A {}\n");
        await TestRepositories.CommitAllAsync(origin, "one");
        await File.WriteAllTextAsync(Path.Combine(origin, "B.cs"), "class B {}\n");
        await TestRepositories.CommitAllAsync(origin, "two");
        var clone = Path.Combine(_repos.Root, "shallow");
        await TestRepositories.Git(_repos.Root, ["clone", "-q", "--depth", "1", "file://" + origin, clone]);

        var copy = await WorkingCopyExporter.ExportAsync(clone, Work("shallow"), includeWorkingTree: false, []);

        Assert.True(copy.Shallow);
        Assert.Equal(["A.cs", "B.cs"], copy.Files.Select(f => f.Path));
        Assert.Equal("file://" + origin, copy.RemoteUrl);
    }

    [Fact]
    public async Task Sha256_repositories_get_sha256_blob_ids()
    {
        var repo = await _repos.InitAsync("sha256", "--object-format=sha256");
        await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "class A {}\n");
        await TestRepositories.CommitAllAsync(repo);

        var copy = await WorkingCopyExporter.ExportAsync(repo, Work("sha256"), includeWorkingTree: true, []);

        Assert.Equal("sha256", copy.ObjectFormat);
        Assert.Equal((await TestRepositories.Git(repo, ["rev-parse", "HEAD:A.cs"])).Trim(), copy.Files.Single().BlobId);
        Assert.False(copy.Dirty);
    }

    [Fact]
    public async Task Repositories_owned_by_another_user_are_read_without_changing_git_config()
    {
        if (Environment.UserName != "root")
        {
            Assert.Skip("Needs root to give the repository to another user (runs in the image's test stage).");
        }
        var repo = await _repos.InitAsync("foreign");
        await File.WriteAllTextAsync(Path.Combine(repo, "A.cs"), "class A {}\n");
        await TestRepositories.CommitAllAsync(repo);
        var chown = await ProcessRunner.RunAsync("chown", ["-R", "12345:12345", repo]);
        Assert.Equal(0, chown.ExitCode);
        var globalConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig");
        var before = File.Exists(globalConfig) ? await File.ReadAllTextAsync(globalConfig) : null;

        var plain = await ProcessRunner.RunAsync("git", ["-C", repo, "rev-parse", "HEAD"]);
        var copy = await WorkingCopyExporter.ExportAsync(repo, Work("foreign"), includeWorkingTree: false, []);

        Assert.NotEqual(0, plain.ExitCode);
        Assert.Contains("dubious ownership", plain.StandardError);
        Assert.Equal(["A.cs"], copy.Files.Select(f => f.Path));
        Assert.Equal(before, File.Exists(globalConfig) ? await File.ReadAllTextAsync(globalConfig) : null);
    }

    [Fact]
    public async Task Non_repositories_are_rejected()
    {
        var plain = _repos.NewDirectory("plain");

        await Assert.ThrowsAsync<GitException>(() => WorkingCopyExporter.ExportAsync(plain, Work("plain"), false, []));
    }
}
