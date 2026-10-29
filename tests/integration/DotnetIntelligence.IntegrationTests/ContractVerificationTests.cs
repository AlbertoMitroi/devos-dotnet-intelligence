using System.Text.Json.Nodes;
using DotnetIntelligence.Cli;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>
/// Contract fixtures (3.7), derived from a real generation of fixture f00: the valid generation, one copy per
/// integrity failure (dangling reference, duplicate id, checksum mismatch, unknown required capability), and one
/// from a newer minor with extra fields and an unknown kind, which must be accepted.
/// </summary>
[Collection(AnalyzeCollection.Name)]
public class ContractVerificationTests : IDisposable
{
    private readonly TestRepositories _repos = new();

    public void Dispose() => _repos.Dispose();

    private async Task<string> GenerationAsync()
    {
        var (repo, _) = await _repos.FromFixtureAsync("f00-minimal");
        var output = Path.Combine(_repos.Root, "valid");
        var code = await CommandLine.RunAsync(["analyze", "--src", repo, "--out", output], TextWriter.Null, TextWriter.Null,
            new Dictionary<string, string?> { ["DI_WORK"] = _repos.NewDirectory("work") }, CancellationToken.None);
        Assert.Equal(ExitCodes.Complete, code);
        return output;
    }

    private string Copy(string source, string name)
    {
        var target = Path.Combine(_repos.Root, name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        return target;
    }

    /// <summary>Recomputes the manifest checksums, so a test isolates the record-level problem it introduces.</summary>
    private static void Reseal(string root)
    {
        var manifestPath = Path.Combine(root, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (var file in manifest["files"]!.AsArray())
        {
            var full = Path.Combine(root, (string)file!["path"]!);
            file["sha256"] = Hashing.Sha256HexOfFile(full);
            file["size"] = new FileInfo(full).Length;
        }
        CanonicalJson.WriteDocument(manifestPath, manifest);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> Verify(string root)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CommandLine.RunAsync(["verify", root], stdout, stderr, new Dictionary<string, string?>(), CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static void EditLines(string root, string file, Func<List<string>, List<string>> edit)
    {
        var path = Path.Combine(root, file);
        File.WriteAllText(path, string.Concat(edit(File.ReadAllLines(path).ToList()).Select(l => l + "\n")));
    }

    [Fact]
    public async Task The_valid_generation_is_accepted()
    {
        var (code, stdout, stderr) = await Verify(await GenerationAsync());

        Assert.True(code == ExitCodes.Complete, stderr);
        Assert.StartsWith("ok", stdout);
    }

    [Fact]
    public async Task A_dangling_reference_is_rejected()
    {
        var copy = Copy(await GenerationAsync(), "dangling");
        EditLines(copy, "graph/relations.ndjson", lines =>
        {
            var record = JsonNode.Parse(lines[0])!.AsObject();
            record["to"] = "dotnet:src/Missing/Missing.csproj:T:Nowhere";
            lines[0] = CanonicalJson.Serialize(record);
            return lines;
        });
        Reseal(copy);

        var (code, _, stderr) = await Verify(copy);

        Assert.Equal(ExitCodes.IntegrityFailed, code);
        Assert.Contains("'dotnet:src/Missing/Missing.csproj:T:Nowhere' does not exist", stderr);
    }

    [Fact]
    public async Task A_duplicate_id_is_rejected()
    {
        var copy = Copy(await GenerationAsync(), "duplicate");
        EditLines(copy, "graph/entities.ndjson", lines => [lines[0], .. lines]);
        Reseal(copy);

        var (code, _, stderr) = await Verify(copy);

        Assert.Equal(ExitCodes.IntegrityFailed, code);
        Assert.Contains("duplicate id", stderr);
    }

    [Fact]
    public async Task A_checksum_mismatch_is_rejected()
    {
        var copy = Copy(await GenerationAsync(), "tampered");
        File.AppendAllText(Path.Combine(copy, "graph/entities.ndjson"), " ");

        var (code, _, stderr) = await Verify(copy);

        Assert.Equal(ExitCodes.IntegrityFailed, code);
        Assert.Contains("graph/entities.ndjson: checksum does not match the manifest", stderr);
    }

    [Fact]
    public async Task An_unknown_required_capability_is_refused()
    {
        var copy = Copy(await GenerationAsync(), "capability");
        var manifestPath = Path.Combine(copy, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["requiredCapabilities"] = new JsonArray("time-travel");
        CanonicalJson.WriteDocument(manifestPath, manifest);

        var (code, _, stderr) = await Verify(copy);

        Assert.Equal(ExitCodes.IntegrityFailed, code);
        Assert.Contains("required capability 'time-travel' is not understood", stderr);
    }

    [Fact]
    public async Task A_newer_minor_with_extra_fields_and_an_unknown_kind_is_accepted_and_reported()
    {
        var copy = Copy(await GenerationAsync(), "newer-minor");
        EditLines(copy, "graph/relations.ndjson", lines =>
        {
            var record = JsonNode.Parse(lines[0])!.AsObject();
            record["kind"] = "observed_at_runtime";
            record["confidence"] = 0.5;
            lines[0] = CanonicalJson.Serialize(record);
            return lines;
        });
        EditLines(copy, "graph/entities.ndjson", lines => lines.Select(l =>
        {
            var record = JsonNode.Parse(l)!.AsObject();
            record["ext.other.tag"] = "x";
            return CanonicalJson.Serialize(record);
        }).ToList());
        var manifestPath = Path.Combine(copy, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["schemaVersion"] = "1.1";
        manifest["newTopLevelField"] = true;
        CanonicalJson.WriteDocument(manifestPath, manifest);
        Reseal(copy);

        var (code, stdout, stderr) = await Verify(copy);

        Assert.True(code == ExitCodes.Complete, stderr);
        Assert.Contains("unknown kind 'observed_at_runtime' kept, not interpreted", stdout);
    }
}
