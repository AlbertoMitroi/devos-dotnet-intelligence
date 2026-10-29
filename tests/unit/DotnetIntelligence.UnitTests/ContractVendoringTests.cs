using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.UnitTests;

/// <summary>
/// schema/ is vendored from intelligence-contract (contract decision C1) by scripts/vendor-contract.sh, which records
/// the source commit and a SHA-256 per file in schema/CONTRACT-SOURCE.json. Any local edit or extra file is drift.
/// </summary>
public class ContractVendoringTests
{
    private static readonly JsonObject Source = JsonNode.Parse(RepositoryRoot.ReadText("schema/CONTRACT-SOURCE.json"))!.AsObject();

    [Fact]
    public void Source_names_a_full_commit()
    {
        Assert.Equal("intelligence-contract", (string?)Source["repository"]);
        Assert.Matches("^[0-9a-f]{40}$", (string?)Source["commit"]);
    }

    [Fact]
    public void Every_vendored_file_matches_its_recorded_checksum()
    {
        var files = Source["files"]!.AsObject();
        Assert.NotEmpty(files);
        foreach (var (path, sha256) in files)
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RepositoryRoot.File(path))));
            Assert.True(actual == (string?)sha256, $"{path} drifted from intelligence-contract {Source["commit"]}: re-vendor with scripts/vendor-contract.sh instead of editing it.");
        }
    }

    [Fact]
    public void No_unrecorded_file_exists_under_schema()
    {
        var recorded = Source["files"]!.AsObject().Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var present = Directory.EnumerateFiles(RepositoryRoot.File("schema"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RepositoryRoot.Path, f).Replace('\\', '/'))
            .Where(f => f != "schema/CONTRACT-SOURCE.json");
        var unrecorded = present.Where(f => !recorded.Contains(f)).ToList();
        Assert.True(unrecorded.Count == 0, "Not vendored from intelligence-contract: " + string.Join(", ", unrecorded));
    }
}
