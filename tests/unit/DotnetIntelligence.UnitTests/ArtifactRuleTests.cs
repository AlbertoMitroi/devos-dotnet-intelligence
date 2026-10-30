using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotnetIntelligence.Artifacts;

namespace DotnetIntelligence.UnitTests;

/// <summary>Context pack and wiki rules (R4.8 to R4.12), link rules (R4.2) and the finalization order (R4.16).</summary>
public class ArtifactRuleTests
{
    [Fact]
    public void Cards_cite_evidence_end_with_gaps_and_say_what_was_cut()
    {
        using var generation = new SyntheticGeneration(endpointCount: ContextPackWriter.ListLimit + 5);
        var files = ContextPackWriter.Render(ServiceModel.Load(generation.Root, generation.Manifest));
        var card = files[$"context/projects/{ContextPackWriter.CardName(SyntheticGeneration.B)}.md"];
        Assert.Contains($"\n5 more endpoints omitted; see `query <generation> routes`", card, StringComparison.Ordinal);
        Assert.Contains("- GET `/api/items/{}` → `M:B.Api.Handle(System.Int32)`, src/B/Program.cs:4; called from 1 linked call(s)", card, StringComparison.Ordinal);
        foreach (var (path, content) in files)
        {
            Assert.Contains("\n## What could not be determined\n", content, StringComparison.Ordinal);
            var section = content[content.IndexOf("## What could not be determined", StringComparison.Ordinal)..];
            Assert.DoesNotContain("\n## ", section[3..], StringComparison.Ordinal);
            // Every list item of an observation section ends with or contains its evidence.
            foreach (var line in content.Split('\n').Where(l => l.StartsWith("- GET ", StringComparison.Ordinal) || l.StartsWith("- publishes", StringComparison.Ordinal)))
            {
                Assert.Matches(new Regex(@"[\w/.-]+:\d+"), line);
            }
            Assert.DoesNotContain("\r", content, StringComparison.Ordinal);
            Assert.EndsWith(".md", path, StringComparison.Ordinal);
        }
        var a = files[$"context/projects/{ContextPackWriter.CardName(SyntheticGeneration.A)}.md"];
        Assert.Contains("- GET `/api/items/{}` (exact), src/A/Client.cs:6; → candidate `B` src/B/Program.cs:4", a, StringComparison.Ordinal);
        Assert.Contains("`A` → `B` via http: 1 link(s), strongest candidate; for example src/A/Client.cs:6", files["context/overview.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Wiki_pages_record_commit_engine_queries_and_draw_resolution()
    {
        using var generation = new SyntheticGeneration();
        var files = WikiWriter.Render(ServiceModel.Load(generation.Root, generation.Manifest));
        var index = files["wiki/index.md"];
        Assert.Contains("- Commit: `0123456789abcdef0123456789abcdef01234567`", index, StringComparison.Ordinal);
        Assert.Contains("- Engine: dotnet-intelligence 0.0.0-test", index, StringComparison.Ordinal);
        Assert.Contains("## Legend", index, StringComparison.Ordinal);
        Assert.Contains("- `dotnet-intelligence query <generation> routes`", index, StringComparison.Ordinal);
        // The A to B link is a candidate: a dotted arrow labelled with its resolution.
        Assert.Matches(new Regex(@"n[0-9a-f]{10} -\.->\|""http ×1, candidate""\| n[0-9a-f]{10}"), index);
        Assert.Contains($"wiki/services/{ContextPackWriter.CardName(SyntheticGeneration.B)}.md", files.Keys);
        Assert.Equal(files, WikiWriter.Render(ServiceModel.Load(generation.Root, generation.Manifest)));
    }

    [Fact]
    public void Several_request_targets_are_all_kept_as_ambiguous()
    {
        var links = Linker.Link(
        [
            Grpc("obs:c", "grpc.call", "P.Client"),
            Grpc("obs:s1", "grpc.service", "P.Server1"),
            Grpc("obs:s2", "grpc.service", "P.Server2"),
        ], []);
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal("ambiguous", (string?)l["resolution"]));
        Assert.Equal(["obs:s1", "obs:s2"], links.Select(l => (string)l["to"]!["observation"]!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_request_without_target_is_unresolved_with_a_reason()
    {
        var link = Assert.Single(Linker.Link([Grpc("obs:c", "grpc.call", "P.Client")], []));
        Assert.Equal("unresolved", (string?)link["resolution"]);
        Assert.Null(link["to"]);
        Assert.Equal("No implementation of greet.Greeter/SayHello was found in the repository.", (string?)link["reason"]);
    }

    [Fact]
    public void A_path_is_only_as_strong_as_its_weakest_part_and_subscribers_fan_out()
    {
        var publish = Message("obs:p", "messaging.publish", "candidate");
        var links = Linker.Link([publish, Message("obs:s1", "messaging.subscribe", "exact"), Message("obs:s2", "messaging.subscribe", "exact")], []);
        // Fan-out (D18): two subscribers are two receivers, each link as weak as the candidate publish.
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal("candidate", (string?)l["resolution"]));
        Assert.All(links, l => Assert.Contains("ev:obs:p", l["evidence"]!.AsArray().Select(e => (string)e!)));
    }

    [Fact]
    public void Run_json_is_written_after_the_manifest_and_nothing_after_it()
    {
        var root = Directory.CreateTempSubdirectory("di-writer-").FullName;
        try
        {
            var writer = new GenerationWriter(root);
            writer.WriteBytes("graph/a.ndjson", []);
            var index = writer.Reserve(IndexWriter.FileName);
            File.WriteAllBytes(index, [1, 2, 3]);
            Assert.Throws<InvalidOperationException>(() => writer.WriteRun(new JsonObject()));
            var files = writer.WriteManifest(new JsonObject());
            Assert.Equal(["graph/a.ndjson", "index.sqlite"], files.Select(f => f.Path));
            Assert.Equal(3, files.Single(f => f.Path == "index.sqlite").Size);
            Assert.Throws<InvalidOperationException>(() => writer.WriteBytes("late.md", []));
            writer.WriteRun(new JsonObject());
            Assert.True(File.GetLastWriteTimeUtc(Path.Combine(root, "run.json")) >= File.GetLastWriteTimeUtc(Path.Combine(root, "manifest.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonObject Grpc(string id, string kind, string owner) => new()
    {
        ["id"] = id, ["kind"] = kind, ["project"] = owner.Split('.')[1] + ".csproj", ["service"] = "greet.Greeter", ["method"] = "SayHello",
        [kind == "grpc.call" ? "caller" : "implementation"] = owner, ["evidence"] = new JsonArray("ev:" + id),
    };

    private static JsonObject Message(string id, string kind, string resolution) => new()
    {
        ["id"] = id, ["kind"] = kind, ["project"] = id + ".csproj", ["resolution"] = resolution, ["handler"] = kind == "messaging.subscribe" ? "H" + id : null,
        ["contract"] = new JsonObject { ["identity"] = "name:OrderPlaced", ["name"] = "OrderPlaced" }, ["evidence"] = new JsonArray("ev:" + id),
    };
}
