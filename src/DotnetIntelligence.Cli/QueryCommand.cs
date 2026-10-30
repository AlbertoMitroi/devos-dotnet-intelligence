using System.Globalization;
using DotnetIntelligence.Artifacts;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Cli;

/// <summary>
/// query &lt;generation&gt; &lt;command&gt; [args] [--json] (5.5). Text by default; --json prints the canonical JSON
/// rows. Exit codes: 0 results, 2 usage, 3 ambiguous name (candidates listed, none picked), 4 no results.
/// </summary>
public static class QueryCommand
{
    public const string Usage =
        "Usage: query <generation> <command> [args] [--json]\n" +
        "  symbol <name or id>             exact match, or the candidates (exit 3)\n" +
        "  refs <symbol> [--usage <kind>]  usages with path, line and the code line\n" +
        "  callers|callees <symbol> [--depth N]\n" +
        "  impl <symbol>                   implementations, overrides, derived types\n" +
        "  di <service type>               registrations and consumers\n" +
        "  routes|endpoints [--route <prefix>]   HTTP endpoints served by the repository\n" +
        "  http [--route <prefix>]         outgoing HTTP calls and the endpoints they link to\n" +
        "  calls-to <route prefix or observation id>\n" +
        "  messages [contract]             publishers, subscribers and their links\n" +
        "  trace <symbol or observation> [--to <symbol, observation or project>] [--depth N]\n" +
        "  search <text> [--kind entity|snippet|observation] [--limit N]\n" +
        "  dump                            canonical dump of index.sqlite";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout)
    {
        if (args.Length < 2)
        {
            throw new UsageException(Usage);
        }
        var generation = Path.GetFullPath(args[0]);
        var command = args[1];
        var positional = new List<string>();
        var json = false;
        string? usage = null, route = null, to = null, kind = null;
        int depth = 1, limit = 50;
        for (var i = 2; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new UsageException($"Missing value for {args[i]}.\n{Usage}");
            int Number()
            {
                var name = args[i];
                return int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new UsageException($"{name} takes a positive number.");
            }
            switch (args[i])
            {
                case "--json": json = true; break;
                case "--usage": usage = Value(); break;
                case "--route": route = Value(); break;
                case "--to": to = Value(); break;
                case "--kind": kind = Value(); break;
                case "--depth": depth = Number(); break;
                case "--limit": limit = Number(); break;
                case var option when option.StartsWith("--", StringComparison.Ordinal): throw new UsageException($"Unknown option '{option}'.\n{Usage}");
                default: positional.Add(args[i]); break;
            }
        }
        var result = QueryEngine.Run(generation, command, new QueryEngine.Options(positional, usage, depth, route, to, kind, limit));
        var text = json && result.Json is not null ? CanonicalJson.Serialize(result.Json) : result.Text;
        await stdout.WriteAsync(text + "\n").ConfigureAwait(false);
        return result.Code;
    }

    /// <summary>
    /// index &lt;generation&gt; &lt;file.sqlite&gt;: builds the derived index of a generation from its NDJSON files into a
    /// separate file, for generations written before the index existed. The generation itself is not changed.
    /// </summary>
    public static Task<int> IndexAsync(string[] args, TextWriter stdout)
    {
        if (args.Length != 2 || !args[1].EndsWith(".sqlite", StringComparison.Ordinal))
        {
            throw new UsageException("Usage: index <generation> <file.sqlite>");
        }
        var generation = Path.GetFullPath(args[0]);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(generation, GenerationWriter.ManifestFileName)))!;
        IndexWriter.Write(generation, Path.GetFullPath(args[1]), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = (string)manifest["schemaVersion"]!,
            ["indexSchema"] = IndexWriter.SchemaVersion,
            ["commit"] = (string)manifest["commit"]!,
            ["engine"] = $"{manifest["engine"]!["name"]}@{manifest["engine"]!["version"]}",
            ["status"] = (string?)manifest["status"] ?? "unknown",
            ["coverage"] = CanonicalJson.Serialize(manifest["coverage"]?["summary"]),
        });
        return stdout.WriteLineAsync($"index written: {args[1]}").ContinueWith(_ => ExitCodes.Complete, TaskScheduler.Default);
    }
}
