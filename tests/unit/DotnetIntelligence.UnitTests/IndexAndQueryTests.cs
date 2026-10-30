using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotnetIntelligence.Artifacts;
using Microsoft.Data.Sqlite;

namespace DotnetIntelligence.UnitTests;

/// <summary>index.sqlite (R4.3 to R4.7) and query (R4.14, R4.15) on a hand-written generation.</summary>
public class IndexAndQueryTests
{
    [Fact]
    public void Bundled_sqlite_supports_fts5()
    {
        var (version, fts5) = IndexWriter.SqliteInfo();
        Assert.True(fts5, $"SQLite {version} without FTS5");
    }

    [Fact]
    public void Index_schema_matches_the_vendored_contract()
    {
        var sql = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "schema", "index", "v1", "index.sql"));
        var sections = sql[sql.IndexOf("-- base", StringComparison.Ordinal)..sql.IndexOf("-- system", StringComparison.Ordinal)];
        var contract = sections.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("create", StringComparison.Ordinal))
            .Select(l => Regex.Replace(l.TrimEnd(';'), "\\s+", " ")).ToList();
        Assert.Equal(contract, IndexWriter.Schema.Select(s => Regex.Replace(s, "\\s+", " ")));
        Assert.Contains("index schema 1", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("select * from relations where to_id = 'x' and kind = 'references'", "relations_to")]
    [InlineData("select * from relations where from_id = 'x' and kind = 'calls_declared_target'", "relations_from")]
    [InlineData("select * from declarations where path = 'x' and line = 3", "declarations_location")]
    [InlineData("select * from declarations where entity = 'x'", "declarations_entity")]
    [InlineData("select * from evidence where path = 'x' and line = 3", "evidence_location")]
    [InlineData("select * from links where from_obs = 'x'", "links_from")]
    [InlineData("select * from links where to_obs = 'x'", "links_to")]
    [InlineData("select relation from relation_evidence where evidence = 'x'", "relation_evidence_evidence")]
    [InlineData("select * from entities where name = 'x'", "entities_name")]
    public void Lookups_in_both_directions_use_an_index(string sql, string index)
    {
        using var generation = new SyntheticGeneration();
        var path = generation.BuildIndex();
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "explain query plan " + sql;
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
        {
            plan.Add(reader.GetString(3));
        }
        Assert.Contains(plan, p => p.Contains(index, StringComparison.Ordinal));
    }

    [Fact]
    public void The_index_and_its_dump_are_identical_across_builds()
    {
        using var generation = new SyntheticGeneration();
        var first = generation.BuildIndex(Path.Combine(generation.Root, "one.sqlite"));
        var second = generation.BuildIndex(Path.Combine(generation.Root, "two.sqlite"));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        var dump = IndexDump.Dump(first);
        Assert.Equal(dump, IndexDump.Dump(second));
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("meta\t[\"indexSchema\",\"1\"]", lines);
        Assert.Equal(lines.OrderBy(l => l.Split('\t')[0], StringComparer.Ordinal).ThenBy(l => l, StringComparer.Ordinal), lines);
    }

    [Fact]
    public void Observation_rows_point_at_the_site_not_at_configuration_or_route_groups()
    {
        using var generation = new SyntheticGeneration();
        var path = generation.BuildIndex();
        var http = Run(generation, "http");
        Assert.Contains("GET /api/items/{}  exact  src/A/Client.cs:6", http.Text, StringComparison.Ordinal);
        Assert.Contains("-> candidate GET /api/items/{}  src/B/Program.cs:4", http.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Symbol_returns_candidates_with_exit_3_and_never_picks_one()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var result = Run(generation, "symbol", "Handle");
        Assert.Equal(QueryEngine.Ambiguous, result.Code);
        Assert.Contains(SyntheticGeneration.Handle, result.Text, StringComparison.Ordinal);
        Assert.Contains(SyntheticGeneration.HandleAll, result.Text, StringComparison.Ordinal);
        Assert.Equal(2, result.Json!.AsArray().Count);

        var exact = Run(generation, "symbol", "B.Api.Handle(System.Int32)");
        Assert.Equal(QueryEngine.Results, exact.Code);
        Assert.Contains("declared at src/B/Api.cs:9", exact.Text, StringComparison.Ordinal);

        var missing = Run(generation, "symbol", "Nothing");
        Assert.Equal(QueryEngine.NoResults, missing.Code);
        Assert.Equal("[]", missing.Json!.ToJsonString());
    }

    [Fact]
    public void Refs_list_every_usage_with_the_code_line()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var result = Run(generation, "refs", "B.Api");
        Assert.Equal(QueryEngine.Results, result.Code);
        Assert.Equal(2, result.Json!.AsArray().Count);
        Assert.Contains("src/A/Client.cs:5  read  " + SyntheticGeneration.Get + "\n    var first = api.Handle(1);", result.Text, StringComparison.Ordinal);
        Assert.Contains("src/A/Client.cs:7  read", result.Text, StringComparison.Ordinal);
        Assert.Equal(QueryEngine.NoResults, Run(generation, "refs", "B.Api", "--usage", "write").Code);
    }

    [Fact]
    public void Callers_and_callees_share_the_row_shape_of_the_other_engine()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var callers = Run(generation, "callers", SyntheticGeneration.Handle);
        Assert.Equal(QueryEngine.Results, callers.Code);
        var row = callers.Json!.AsArray().Single()!.AsObject();
        foreach (var field in new[] { "depth", "of", "other", "path", "line" })
        {
            Assert.True(row.ContainsKey(field), field);
        }
        Assert.Equal(SyntheticGeneration.Get, (string?)row["other"]);
        Assert.Equal("src/A/Client.cs", (string?)row["path"]);
        Assert.Equal(5, (int)row["line"]!);
        Assert.Contains(SyntheticGeneration.Handle, Run(generation, "callees", "A.Client.Get").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Routes_and_calls_to_show_both_sides_with_evidence()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var routes = Run(generation, "routes", "--route", "/api/items");
        Assert.Equal("GET /api/items/{}  M:B.Api.Handle(System.Int32)  src/B/Program.cs:4", routes.Text);
        Assert.Equal("src/B/Program.cs:4", (string?)routes.Json![0]!["at"]);
        var callsTo = Run(generation, "calls-to", "/api/items");
        Assert.Equal("GET /api/items/{}  <- candidate  http.call src/A/Client.cs:6", callsTo.Text);
    }

    [Fact]
    public void Search_finds_names_snippets_and_observations_with_locations()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var byName = Run(generation, "search", "Client", "--kind", "entity");
        Assert.Contains(SyntheticGeneration.Client, byName.Text, StringComparison.Ordinal);
        Assert.Contains("src/A/Client.cs:3", byName.Text, StringComparison.Ordinal);
        var bySnippet = Run(generation, "search", "MapGroup", "--kind", "snippet");
        Assert.Contains("src/B/Program.cs:3", bySnippet.Text, StringComparison.Ordinal);
        var byRoute = Run(generation, "search", "items", "--kind", "observation");
        Assert.Contains("http.endpoint", byRoute.Text, StringComparison.Ordinal);
        Assert.Contains("src/B/Program.cs:4", byRoute.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_crosses_services_and_reports_the_weakest_link()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var result = Run(generation, "trace", "A.Client.Get", "--to", SyntheticGeneration.Handle);
        Assert.Equal(QueryEngine.Results, result.Code);
        var row = result.Json!.AsArray().Single()!;
        // Shortest path: the direct call. The service path (http.call, link, endpoint handler) is longer.
        Assert.Equal("calls_declared_target", (string?)row["steps"]![0]!["kind"]);

        var service = Run(generation, "trace", "A.Client.Get");
        Assert.Contains("weakest link: step 2, candidate", service.Text, StringComparison.Ordinal);
        Assert.Contains("link.configured_route candidate", service.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_results_in_a_partial_generation_says_that_absence_is_not_proof()
    {
        using var complete = new SyntheticGeneration();
        complete.BuildIndex();
        var none = Run(complete, "callers", "A.Client.Get");
        Assert.Equal(QueryEngine.NoResults, none.Code);
        Assert.Equal($"no callers of {SyntheticGeneration.Get}", none.Text);

        using var partial = new SyntheticGeneration(status: "partial");
        partial.BuildIndex();
        var note = Run(partial, "callers", "A.Client.Get");
        Assert.Equal(QueryEngine.NoResults, note.Code);
        Assert.Contains("the generation is partial (2 projects: 1 syntax only, 1 diagnostics failed, partial or unsupported)", note.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_commands_and_missing_indexes_are_usage_errors()
    {
        using var generation = new SyntheticGeneration();
        Assert.Equal(QueryEngine.Usage, Run(generation, "routes").Code);
        generation.BuildIndex();
        Assert.Equal(QueryEngine.Usage, Run(generation, "nonsense").Code);
    }

    [Fact]
    public void Verify_rejects_an_index_built_from_other_records()
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        File.AppendAllText(Path.Combine(generation.Root, "graph", "links.ndjson"), string.Empty);
        Assert.DoesNotContain(GenerationVerifier.VerifyRecords(generation.Root, null, "sha1"), e => e.Contains("index.sqlite", StringComparison.Ordinal));
        var path = Path.Combine(generation.Root, IndexWriter.FileName);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "delete from links";
            command.ExecuteNonQuery();
        }
        Assert.Contains(GenerationVerifier.VerifyRecords(generation.Root, null, "sha1"), e => e == "index.sqlite: table links has 0 rows, the graph has 1 records");
    }

    /// <summary>The JSON rows of every verb match the vendored contract schemas (schema/query/v1), and every verb with data returns rows.</summary>
    [Theory]
    [InlineData("symbol", "symbol", new[] { "A.Client.Get" })]
    [InlineData("ambiguous", "symbol", new[] { "Handle" })]
    [InlineData("refs", "refs", new[] { "B.Api" })]
    [InlineData("callers", "callers", new[] { SyntheticGeneration.Handle })]
    [InlineData("callers", "callees", new[] { "A.Client.Get" })]
    [InlineData("impl", "impl", new[] { SyntheticGeneration.HandleAll })]
    [InlineData("di", "di", new[] { "B.Api" })]
    [InlineData("routes", "routes", new string[0])]
    [InlineData("routes", "endpoints", new string[0])]
    [InlineData("http", "http", new string[0])]
    [InlineData("calls-to", "calls-to", new[] { "/api/items" })]
    [InlineData("messages", "messages", new string[0])]
    [InlineData("search", "search", new[] { "items" })]
    [InlineData("trace", "trace", new[] { "A.Client.Get" })]
    public void Json_rows_match_the_contract_query_schemas(string schema, string command, string[] args)
    {
        using var generation = new SyntheticGeneration();
        generation.BuildIndex();
        var result = Run(generation, command, args);
        Assert.True(result.Code is QueryEngine.Results or QueryEngine.Ambiguous, $"{command}: exit {result.Code} {result.Text}");
        var json = result.Json!;
        Assert.True(json is JsonObject || json.AsArray().Count > 0, $"{command} returned no rows");
        var validator = SchemaValidator.FromDirectory(Path.Combine(RepositoryRoot.Path, "schema", "query", "v1"));
        // Rows go through canonical JSON first, as --json prints them.
        var printed = JsonNode.Parse(DotnetIntelligence.Contracts.CanonicalJson.Serialize(json));
        Assert.Empty(validator.Validate(schema, printed));
    }

    private static QueryResult Run(SyntheticGeneration generation, string command, params string[] args)
    {
        var positional = new List<string>();
        string? usage = null, route = null, to = null, kind = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--usage": usage = args[++i]; break;
                case "--route": route = args[++i]; break;
                case "--to": to = args[++i]; break;
                case "--kind": kind = args[++i]; break;
                default: positional.Add(args[i]); break;
            }
        }
        return QueryEngine.Run(generation.Root, command, new QueryEngine.Options(positional, usage, 1, route, to, kind));
    }
}
