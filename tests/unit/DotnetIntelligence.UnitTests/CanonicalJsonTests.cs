using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.UnitTests;

public class CanonicalJsonTests
{
    [Fact]
    public void Keys_are_sorted_ordinally_at_every_level_without_whitespace()
    {
        var node = JsonNode.Parse("""{ "b": 1, "a": { "z": true, "B": null, "a": [ { "y": 1, "x": 2 } ] } }""");

        Assert.Equal("""{"a":{"B":null,"a":[{"x":2,"y":1}],"z":true},"b":1}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void Same_content_built_in_different_orders_gives_identical_bytes()
    {
        var first = new JsonObject { ["path"] = "src/ä.cs", ["line"] = 3, ["ok"] = true };
        var second = new JsonObject { ["ok"] = true, ["line"] = 3, ["path"] = "src/ä.cs" };

        Assert.Equal(CanonicalJson.SerializeToUtf8(first), CanonicalJson.SerializeToUtf8(second));
    }

    [Fact]
    public void Output_is_utf8_without_bom_and_keeps_non_ascii_text()
    {
        var bytes = CanonicalJson.SerializeToUtf8(new JsonObject { ["name"] = "Ünïcode" });

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("""{"name":"Ünïcode"}""", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Ndjson_terminates_every_record_with_a_line_feed()
    {
        using var stream = new MemoryStream();
        CanonicalJson.WriteNdjson(stream, [new JsonObject { ["id"] = "a" }, new JsonObject { ["id"] = "b" }]);

        Assert.Equal("{\"id\":\"a\"}\n{\"id\":\"b\"}\n", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void Record_ids_are_prefixed_content_hashes()
    {
        var id = Hashing.RecordId("prj", new JsonObject { ["path"] = "src/A/A.csproj" });

        Assert.Matches("^prj:[0-9a-f]{32}$", id);
        Assert.Equal(id, Hashing.RecordId("prj", new JsonObject { ["path"] = "src/A/A.csproj" }));
        Assert.NotEqual(id, Hashing.RecordId("prj", new JsonObject { ["path"] = "src/B/B.csproj" }));
    }
}
