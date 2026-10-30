using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;
using Microsoft.Data.Sqlite;

namespace DotnetIntelligence.Artifacts;

/// <summary>Result of one query: exit code (0 results, 2 usage, 3 ambiguous name, 4 no results), text and JSON.</summary>
public sealed record QueryResult(int Code, string Text, JsonNode? Json);

/// <summary>
/// query &lt;generation&gt; &lt;command&gt; (5.5): a small read-only command set over index.sqlite. The commands and
/// their JSON rows are shared with angular-intelligence (symbol, refs, callers, callees, impl, di, routes, http, search,
/// dump); endpoints, calls-to, messages and trace are this engine's additions. Rows carry repository paths and lines.
/// </summary>
public sealed class QueryEngine : IDisposable
{
    public const int Results = 0;
    public const int Usage = 2;
    public const int Ambiguous = 3;
    public const int NoResults = 4;

    public const string Commands = "symbol, refs, callers, callees, impl, di, routes, endpoints, http, calls-to, messages, trace, search, dump";

    private static readonly string[] Strength = ["unresolved", "candidate", "ambiguous", "exact"];

    private readonly SqliteConnection _connection;
    private readonly string _indexPath;

    private QueryEngine(string indexPath)
    {
        _indexPath = indexPath;
        _connection = IndexWriter.Open(indexPath, readOnly: true);
    }

    public void Dispose() => _connection.Dispose();

    public sealed record Options(
        IReadOnlyList<string> Positional,
        string? Usage = null,
        int Depth = 1,
        string? Route = null,
        string? To = null,
        string? Kind = null,
        int Limit = 50);

    public static QueryResult Run(string generation, string command, Options options)
    {
        // A generation directory, or an index file built apart from its generation (the index command).
        var indexPath = generation.EndsWith(".sqlite", StringComparison.Ordinal) ? generation : Path.Combine(generation, IndexWriter.FileName);
        if (!File.Exists(indexPath))
        {
            return new QueryResult(Usage, $"no {IndexWriter.FileName} in '{generation}'", new JsonObject { ["error"] = "no index" });
        }
        if (command == "dump")
        {
            return new QueryResult(Results, IndexDump.Dump(indexPath).TrimEnd('\n'), null);
        }
        using var engine = new QueryEngine(indexPath);
        var result = command switch
        {
            "symbol" => engine.Symbol(options),
            "refs" => engine.Refs(options),
            "callers" => engine.Calls(options, callers: true),
            "callees" => engine.Calls(options, callers: false),
            "impl" => engine.Impl(options),
            "di" => engine.Di(options),
            "routes" or "endpoints" => engine.Routes(options),
            "http" => engine.Http(options),
            "calls-to" => engine.CallsTo(options),
            "messages" => engine.Messages(options),
            "trace" => engine.Trace(options),
            "search" => engine.Search(options),
            _ => new QueryResult(Usage, $"unknown query command '{command}' ({Commands})", null),
        };
        return result.Code == NoResults ? result with { Text = result.Text + engine.CoverageNote() } : result;
    }

    // ---------------------------------------------------------------- commands

    private QueryResult Symbol(Options options)
    {
        var (id, failure) = Resolve(options.Positional.FirstOrDefault());
        if (failure is not null)
        {
            return failure;
        }
        var entity = Rows("select * from entities where id = $1", id)[0];
        var declarations = Rows("select path, line from declarations where entity = $1 order by path, line", id);
        var text = new StringBuilder();
        text.Append(id).Append('\n').Append($"  {entity["kind"]} {entity["signature"]}");
        foreach (var d in declarations)
        {
            text.Append('\n').Append($"  declared at {d["path"]}:{d["line"]}");
        }
        return new QueryResult(Results, text.ToString(), new JsonObject
        {
            ["entity"] = JsonNode.Parse((string)entity["json"]!),
            ["declarations"] = new JsonArray(declarations.Cast<JsonNode>().ToArray()),
        });
    }

    /// <summary>Every usage (one row per evidence) with path, line and the code line inline.</summary>
    private QueryResult Refs(Options options)
    {
        var (id, failure) = Resolve(options.Positional.FirstOrDefault());
        if (failure is not null)
        {
            return failure;
        }
        var rows = Rows(
            "select r.from_id, r.usage, e.path, e.line, e.text from relations r join relation_evidence re on re.relation = r.id join evidence e on e.id = re.evidence " +
            "where r.kind = 'references' and r.to_id = $1" + (options.Usage is null ? string.Empty : " and r.usage = $2") +
            " order by e.path, e.line, e.col, r.from_id", options.Usage is null ? [id] : [id, options.Usage]);
        return List(rows, r => $"{r["path"]}:{r["line"]}  {r["usage"]}  {r["from_id"]}\n    {((string?)r["text"] ?? string.Empty).Trim()}", $"no references to {id}");
    }

    /// <summary>
    /// Bounded call graph. Declared calls (calls_declared_target) and possible runtime targets of virtual and
    /// interface calls (possible_runtime_target, always candidate) are both followed; "kind" says which.
    /// </summary>
    private QueryResult Calls(Options options, bool callers)
    {
        var (id, failure) = Resolve(options.Positional.FirstOrDefault());
        if (failure is not null)
        {
            return failure;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal) { id! };
        var frontier = new List<string> { id! };
        var rows = new List<JsonObject>();
        var sql = callers
            ? "select from_id as other, path, line, kind, resolution, declared_target from relations where kind in ('calls_declared_target', 'possible_runtime_target') and to_id = $1 order by from_id, path, line, kind"
            : "select to_id as other, path, line, kind, resolution, declared_target from relations where kind in ('calls_declared_target', 'possible_runtime_target') and from_id = $1 order by to_id, path, line, kind";
        for (var depth = 1; depth <= Math.Max(1, options.Depth) && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var edge in Rows(sql, current))
                {
                    var row = new JsonObject { ["depth"] = depth, ["of"] = current };
                    foreach (var (key, value) in edge)
                    {
                        row[key] = value?.DeepClone();
                    }
                    rows.Add(row);
                    if (seen.Add((string)edge["other"]!))
                    {
                        next.Add((string)edge["other"]!);
                    }
                }
            }
            frontier = next;
        }
        return List(rows, r => $"{new string(' ', 2 * ((int)r["depth"]! - 1))}{r["other"]}  {r["path"]}:{r["line"]}" +
            ((string?)r["kind"] == "possible_runtime_target" ? $"  (possible runtime target of {r["declared_target"]})" : string.Empty),
            $"no {(callers ? "callers" : "callees")} of {id}");
    }

    private QueryResult Impl(Options options)
    {
        var (id, failure) = Resolve(options.Positional.FirstOrDefault());
        if (failure is not null)
        {
            return failure;
        }
        var rows = Rows("select kind, from_id, path, line from relations where kind in ('implements', 'overrides', 'inherits') and to_id = $1 order by from_id, kind", id);
        return List(rows, r => $"{r["kind"]}  {r["from_id"]}  {r["path"]}:{r["line"]}", $"no implementations of {id}");
    }

    private QueryResult Di(Options options)
    {
        var (id, failure) = Resolve(options.Positional.FirstOrDefault());
        if (failure is not null)
        {
            return failure;
        }
        var rows = Rows("select kind, path, line, json from observations where kind in ('di.registers', 'di.consumes') order by kind, path, line, id")
            .Where(r => (string?)JsonNode.Parse((string)r["json"]!)!["service"] == id).ToList();
        return List(rows, r =>
        {
            var o = JsonNode.Parse((string)r["json"]!)!;
            return (string)r["kind"]! == "di.registers"
                ? $"registers  scope {o["lifetime"]}  impl {o["implementation"] ?? ((bool?)o["factory"] == true ? "factory" : "?")}{((bool?)o["conditional"] == true ? "  conditional" : string.Empty)}  {r["path"]}:{r["line"]}"
                : $"consumes   {o["consumer"]} via {o["via"]}  {r["path"]}:{r["line"]}";
        }, $"no registrations or consumers of {id}");
    }

    /// <summary>The HTTP routes the repository serves (http.endpoint), optionally under a route prefix.</summary>
    private QueryResult Routes(Options options)
    {
        var rows = Observations("http.endpoint")
            .Where(o => options.Route is null || ((string?)o["canonicalRoute"])?.StartsWith(options.Route, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(o => (string?)o["canonicalRoute"], StringComparer.Ordinal).ThenBy(o => (string?)o["method"], StringComparer.Ordinal).ThenBy(o => (string)o["at"]!, StringComparer.Ordinal)
            .ToList();
        return List(rows, o => $"{o["method"]} {o["canonicalRoute"]}  {IndexWriter.ShortId((string?)o["handler"] ?? "(no handler)")}  {o["at"]}", "no routes");
    }

    /// <summary>Outgoing HTTP calls, each with the endpoints it was linked to ("targets", from configured_route links).</summary>
    private QueryResult Http(Options options)
    {
        var rows = Observations("http.call")
            .Where(o => options.Route is null || ((string?)o["canonicalRoute"])?.StartsWith(options.Route, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        foreach (var row in rows)
        {
            row["targets"] = new JsonArray(LinksFrom((string)row["id"]!).Cast<JsonNode>().ToArray());
        }
        return List(rows, o =>
        {
            var line = $"{o["method"]} {o["canonicalRoute"] ?? "(unknown route)"}  {o["resolution"]}  {o["at"]}";
            foreach (var t in o["targets"]!.AsArray())
            {
                line += t!["to"] is null
                    ? $"\n    -> unresolved: {t["reason"]}"
                    : $"\n    -> {t["resolution"]} {t["to"]!["method"]} {t["to"]!["canonicalRoute"]}  {t["to"]!["at"]}";
            }
            return line;
        }, "no HTTP calls");
    }

    /// <summary>Callers of the endpoints whose route starts with the given prefix (or of one endpoint by observation id).</summary>
    private QueryResult CallsTo(Options options)
    {
        var term = options.Positional.FirstOrDefault() ?? options.Route;
        if (term is null)
        {
            return new QueryResult(Usage, "missing endpoint (route prefix or observation id)", null);
        }
        var endpoints = Observations("http.endpoint", "grpc.service")
            .Where(o => (string)o["id"]! == term || ((string?)o["canonicalRoute"])?.StartsWith(term, StringComparison.OrdinalIgnoreCase) == true
                || $"{o["service"]}/{o["method"]}".StartsWith(term, StringComparison.Ordinal))
            .ToList();
        if (endpoints.Count == 0)
        {
            return new QueryResult(NoResults, $"no endpoint matches '{term}'", new JsonArray());
        }
        var rows = new List<JsonObject>();
        foreach (var endpoint in endpoints.OrderBy(e => (string)e["at"]!, StringComparer.Ordinal))
        {
            foreach (var link in Rows("select id, kind, via, from_obs, resolution from links where to_obs = $1 order by from_obs, id", endpoint["id"]))
            {
                rows.Add(new JsonObject
                {
                    ["link"] = (string)link["id"]!,
                    ["kind"] = (string)link["kind"]!,
                    ["resolution"] = (string)link["resolution"]!,
                    ["from"] = Observation((string)link["from_obs"]!),
                    ["to"] = endpoint.DeepClone(),
                });
            }
        }
        return List(rows, r => $"{(r["to"]!["canonicalRoute"] is null ? $"gRPC {r["to"]!["service"]}/{r["to"]!["method"]}" : $"{r["to"]!["method"]} {r["to"]!["canonicalRoute"]}")}  <- {r["resolution"]}  {r["from"]!["kind"]} {r["from"]!["at"]}",
            $"no callers of the {endpoints.Count} endpoint(s) matching '{term}'");
    }

    /// <summary>Publishers and subscribers per message contract (name, full name or identity contains the text).</summary>
    private QueryResult Messages(Options options)
    {
        var term = options.Positional.FirstOrDefault();
        var rows = Observations("messaging.publish", "messaging.subscribe")
            .Where(o => term is null || new[] { "name", "fullName", "identity" }.Any(f => ((string?)o["contract"]?[f])?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(o => (string?)o["contract"]?["identity"], StringComparer.Ordinal).ThenBy(o => (string)o["kind"]!, StringComparer.Ordinal).ThenBy(o => (string)o["at"]!, StringComparer.Ordinal)
            .ToList();
        foreach (var row in rows.Where(r => (string)r["kind"]! == "messaging.publish"))
        {
            row["targets"] = new JsonArray(LinksFrom((string)row["id"]!).Cast<JsonNode>().ToArray());
        }
        return List(rows, o =>
        {
            var line = $"{((string)o["kind"]! == "messaging.publish" ? "publish  " : "subscribe")}  {o["contract"]?["identity"]}  {o["transport"] ?? "unknown"}  {o["at"]}";
            foreach (var t in o["targets"]?.AsArray() ?? [])
            {
                line += t!["to"] is null ? $"\n    -> unresolved: {t["reason"]}" : $"\n    -> {t["kind"]} {t["resolution"]}  {t["to"]!["at"]}";
            }
            return line;
        }, term is null ? "no messages" : $"no message contract matches '{term}'");
    }

    /// <summary>
    /// Bounded path across code and services: calls between entities, observations made inside an entity (calls,
    /// publishes, sends, consumes), links between observations and the handlers that serve an observation. With
    /// --to, the shortest path to that symbol, observation or project; without it, every reachable service-side
    /// observation. Each path reports its weakest link.
    /// </summary>
    private QueryResult Trace(Options options)
    {
        var start = options.Positional.FirstOrDefault();
        if (start is null)
        {
            return new QueryResult(Usage, "missing start (symbol or observation id)", null);
        }
        string startId;
        if (start.StartsWith("obs:", StringComparison.Ordinal))
        {
            startId = start;
        }
        else
        {
            var (id, failure) = Resolve(start);
            if (failure is not null)
            {
                return failure;
            }
            startId = id!;
        }
        Func<string, bool> isTarget;
        if (options.To is null)
        {
            isTarget = node => node.StartsWith("obs:", StringComparison.Ordinal) && node != startId && ServiceSide.Contains((string)Observation(node)["kind"]!);
        }
        else if (options.To.StartsWith("obs:", StringComparison.Ordinal))
        {
            isTarget = node => node == options.To;
        }
        else if (options.To.EndsWith(".csproj", StringComparison.Ordinal))
        {
            isTarget = node => node != startId && ProjectOf(node) == options.To;
        }
        else
        {
            var (toId, failure) = Resolve(options.To);
            if (failure is not null)
            {
                return failure;
            }
            isTarget = node => node == toId;
        }

        var depth = options.Depth <= 1 ? 8 : options.Depth;
        var parent = new Dictionary<string, (string From, JsonObject Step)>(StringComparer.Ordinal);
        var frontier = new List<string> { startId };
        var visited = new HashSet<string>(StringComparer.Ordinal) { startId };
        var reached = new List<string>();
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var node in frontier)
            {
                foreach (var step in Steps(node))
                {
                    var to = (string)step["to"]!;
                    if (!visited.Add(to))
                    {
                        continue;
                    }
                    parent[to] = (node, step);
                    if (isTarget(to))
                    {
                        reached.Add(to);
                        if (options.To is not null)
                        {
                            continue;
                        }
                    }
                    next.Add(to);
                }
            }
            frontier = next.Order(StringComparer.Ordinal).ToList();
            if (options.To is not null && reached.Count > 0)
            {
                break;
            }
        }

        var rows = new List<JsonObject>();
        foreach (var end in reached.Order(StringComparer.Ordinal).Take(options.Limit))
        {
            var steps = new List<JsonObject>();
            for (var node = end; node != startId; node = parent[node].From)
            {
                steps.Add(parent[node].Step);
            }
            steps.Reverse();
            var weakestIndex = 0;
            for (var i = 1; i < steps.Count; i++)
            {
                if (Rank(steps[i]) < Rank(steps[weakestIndex]))
                {
                    weakestIndex = i;
                }
            }
            rows.Add(new JsonObject
            {
                ["from"] = startId,
                ["to"] = end,
                ["steps"] = new JsonArray(steps.Select(s => (JsonNode)s.DeepClone()).ToArray()),
                ["weakest"] = new JsonObject { ["resolution"] = (string)steps[weakestIndex]["resolution"]!, ["step"] = weakestIndex + 1 },
            });
        }
        return List(rows, r =>
        {
            var b = new StringBuilder();
            b.Append($"{r["to"]}  ({r["steps"]!.AsArray().Count} steps, weakest link: step {r["weakest"]!["step"]}, {r["weakest"]!["resolution"]})");
            var n = 1;
            foreach (var s in r["steps"]!.AsArray())
            {
                b.Append($"\n  {n++}. {s!["kind"]} {s["resolution"]}  {s["to"]}{(s["at"] is null ? string.Empty : $"  {s["at"]}")}");
            }
            return b.ToString();
        }, options.To is null ? $"no service-side observation reachable from {startId} within {depth} steps" : $"no path from {startId} to {options.To} within {depth} steps");
    }

    private static readonly HashSet<string> ServiceSide = new(StringComparer.Ordinal)
    {
        "http.endpoint", "grpc.service", "messaging.subscribe", "inprocess.handler", "workflow.handler", "realtime.subscribe",
    };

    /// <summary>Observation fields naming the entity where the observed code runs, and the entity that serves it.</summary>
    private static readonly string[] SourceFields = ["caller", "sender", "publisher", "consumer"];
    private static readonly string[] TargetFields = ["handler", "implementation"];

    private List<JsonObject> Steps(string node)
    {
        var steps = new List<JsonObject>();
        if (node.StartsWith("obs:", StringComparison.Ordinal))
        {
            foreach (var link in Rows("select l.id, l.kind, l.via, l.to_obs, l.resolution, o.path, o.line from links l join observations o on o.id = l.to_obs where l.from_obs = $1 order by l.to_obs", node))
            {
                steps.Add(Step((string)link["to_obs"]!, $"link.{link["kind"]}", (string)link["resolution"]!, link["path"], link["line"], (string)link["id"]!));
            }
            var observation = Observation(node);
            foreach (var field in TargetFields)
            {
                if ((string?)observation[field] is { } entity && entity.StartsWith("dotnet:", StringComparison.Ordinal))
                {
                    var declaration = Rows("select path, line from declarations where entity = $1 order by path, line limit 1", entity).FirstOrDefault();
                    steps.Add(Step(entity, $"{observation["kind"]}.{field}", "exact", declaration?["path"], declaration?["line"], node));
                }
            }
            return steps;
        }
        foreach (var call in Rows("select to_id, kind, resolution, path, line, id from relations where from_id = $1 and kind in ('calls_declared_target', 'possible_runtime_target') order by to_id, kind", node))
        {
            steps.Add(Step((string)call["to_id"]!, (string)call["kind"]!, (string)call["resolution"]!, call["path"], call["line"], (string)call["id"]!));
        }
        // A type: its members are where its calls are made.
        foreach (var member in Rows("select to_id, path, line, id from relations where from_id = $1 and kind = 'contains' order by to_id", node))
        {
            steps.Add(Step((string)member["to_id"]!, "contains", "exact", member["path"], member["line"], (string)member["id"]!));
        }
        foreach (var o in ObservationsMadeIn(node))
        {
            steps.Add(Step((string)o["id"]!, (string)o["kind"]!, (string?)o["resolution"] ?? "exact", o["path"], o["line"], (string)o["id"]!));
        }
        return steps.DistinctBy(s => (string)s["to"]!).ToList();
    }

    private List<JsonObject>? _sourceIndex;

    private IEnumerable<JsonObject> ObservationsMadeIn(string entity)
    {
        _sourceIndex ??= Rows("select id, kind, path, line, json from observations order by id")
            .Select(r =>
            {
                var o = JsonNode.Parse((string)r["json"]!)!.AsObject();
                var source = SourceFields.Select(f => (string?)(o[f] as JsonValue)).FirstOrDefault(v => v is not null);
                return new JsonObject { ["id"] = r["id"]!.DeepClone(), ["kind"] = r["kind"]!.DeepClone(), ["path"] = r["path"]?.DeepClone(), ["line"] = r["line"]?.DeepClone(), ["source"] = source, ["resolution"] = (string?)o["resolution"] };
            })
            .Where(o => o["source"] is not null).ToList();
        return _sourceIndex.Where(o => (string)o["source"]! == entity);
    }

    private static JsonObject Step(string to, string kind, string resolution, JsonNode? path, JsonNode? line, string via) => new()
    {
        ["to"] = to,
        ["kind"] = kind,
        ["resolution"] = resolution,
        ["at"] = path is null ? null : $"{path}:{line}",
        ["via"] = via,
    };

    private static int Rank(JsonObject step) => Array.IndexOf(Strength, (string)step["resolution"]!) is var i and >= 0 ? i : 3;

    private string? ProjectOf(string node)
    {
        if (node.StartsWith("obs:", StringComparison.Ordinal))
        {
            return (string?)Observation(node)["project"];
        }
        return Rows("select project from entities where id = $1", node).FirstOrDefault()?["project"]?.GetValue<string>();
    }

    /// <summary>FTS5 search over entities, snippets and observations; quoted terms, ranked by bm25 then ref. --kind filters.</summary>
    private QueryResult Search(Options options)
    {
        var text = string.Join(' ', options.Positional);
        if (text.Length == 0)
        {
            return new QueryResult(Usage, "missing search text", null);
        }
        var match = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        var rows = options.Kind is null
            ? Rows("select ref, kind, text from search where search match $1 order by rank, ref limit $2", match, options.Limit)
            : Rows("select ref, kind, text from search where search match $1 and kind = $3 order by rank, ref limit $2", match, options.Limit, options.Kind);
        foreach (var row in rows)
        {
            var reference = (string)row["ref"]!;
            row["at"] = (string)row["kind"]! switch
            {
                "snippet" => Rows("select e.path, e.line from evidence_snippet es join evidence e on e.id = es.evidence where es.snippet = $1 order by e.path, e.line limit 1", reference)
                    .Select(d => $"{d["path"]}:{d["line"]}").FirstOrDefault(),
                "observation" => (string?)Observation(reference)["at"],
                _ => Rows("select path, line from declarations where entity = $1 order by path, line limit 1", reference).Select(d => $"{d["path"]}:{d["line"]}").FirstOrDefault(),
            };
        }
        return List(rows, r => $"{r["kind"]}  {r["ref"]}  {((string)r["text"]!).Trim()[..Math.Min(160, ((string)r["text"]!).Trim().Length)]}{(r["at"] is null ? string.Empty : $"  {r["at"]}")}",
            $"no matches for '{text}'");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Exact id; else a unique simple name; else a qualified name ("Type.Member" contained in the id). Several matches: exit 3 with the candidates.</summary>
    private (string? Id, QueryResult? Failure) Resolve(string? term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return (null, new QueryResult(Usage, "missing symbol", null));
        }
        if (Rows("select id from entities where id = $1", term).Count == 1)
        {
            return (term, null);
        }
        var candidates = Rows("select id, kind, name, signature from entities where name = $1 order by id", term);
        if (candidates.Count == 0)
        {
            var bare = term.Split('(', 2)[0];
            var name = bare[(bare.LastIndexOf('.') + 1)..];
            candidates = Rows("select id, kind, name, signature from entities where name = $1 and (instr(id, '.' || $2) > 0 or instr(id, ':' || $2) > 0) order by id", name, term);
        }
        // A type and its constructors share the simple name: the type is meant.
        var types = candidates.Where(c => (string)c["kind"]! is not ("constructor" or "method")).Select(c => (string)c["id"]!).ToHashSet(StringComparer.Ordinal);
        if (types.Count == 1 && candidates.All(c => types.Contains((string)c["id"]!) || (string)c["kind"]! == "constructor"
            && Rows("select container from entities where id = $1", c["id"]).FirstOrDefault()?["container"]?.GetValue<string>() is { } container && types.Contains(container)))
        {
            candidates = candidates.Where(c => types.Contains((string)c["id"]!)).ToList();
        }
        return candidates.Count switch
        {
            1 => ((string)candidates[0]["id"]!, null),
            0 => (null, new QueryResult(NoResults, $"no entity named '{term}'", new JsonArray())),
            _ => (null, new QueryResult(Ambiguous,
                $"'{term}' is ambiguous; candidates:\n" + string.Join('\n', candidates.Select(c => $"  {c["id"]}  ({c["kind"]}) {c["signature"]}")),
                new JsonArray(candidates.Cast<JsonNode>().ToArray()))),
        };
    }

    /// <summary>Observations of the given kinds as their record plus "at" (path:line of the first evidence).</summary>
    private List<JsonObject> Observations(params string[] kinds)
    {
        var rows = new List<JsonObject>();
        foreach (var kind in kinds)
        {
            rows.AddRange(Rows("select json, path, line from observations where kind = $1 order by path, line, id", kind).Select(WithAt));
        }
        return rows;
    }

    private readonly Dictionary<string, JsonObject> _observations = new(StringComparer.Ordinal);

    private JsonObject Observation(string id)
    {
        if (!_observations.TryGetValue(id, out var observation))
        {
            observation = Rows("select json, path, line from observations where id = $1", id).Select(WithAt).FirstOrDefault()
                ?? new JsonObject { ["id"] = id, ["kind"] = "unknown" };
            _observations[id] = observation;
        }
        return (JsonObject)observation.DeepClone();
    }

    private static JsonObject WithAt(JsonObject row)
    {
        var o = JsonNode.Parse((string)row["json"]!)!.AsObject();
        o["at"] = row["path"] is null ? null : $"{row["path"]}:{row["line"]}";
        return o;
    }

    /// <summary>Links leaving an observation: kind, resolution, reason and the target observation with its location.</summary>
    private List<JsonObject> LinksFrom(string observation) =>
        Rows("select id, kind, via, to_obs, resolution, reason from links where from_obs = $1 order by to_obs, id", observation)
            .Select(l => new JsonObject
            {
                ["link"] = (string)l["id"]!,
                ["kind"] = (string)l["kind"]!,
                ["resolution"] = (string)l["resolution"]!,
                ["reason"] = l["reason"]?.DeepClone(),
                ["to"] = l["to_obs"] is null ? null : Observation((string)l["to_obs"]!),
            }).ToList();

    /// <summary>"No results" is not "not analyzed": the note says when the generation is partial and what it misses.</summary>
    private string CoverageNote()
    {
        var meta = Rows("select key, value from meta").ToDictionary(r => (string)r["key"]!, r => (string?)r["value"], StringComparer.Ordinal);
        if (meta.GetValueOrDefault("status") is not { } status || status == "complete")
        {
            return string.Empty;
        }
        var coverage = meta.GetValueOrDefault("coverage") is { } c ? JsonNode.Parse(c) : null;
        var parts = new[] { "syntax", "failed", "notAnalyzed" }
            .Select(k => (Key: k, Count: (int?)coverage?[k] ?? 0)).Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {(x.Key == "notAnalyzed" ? "not analyzed" : x.Key == "syntax" ? "syntax only" : "failed")}");
        var detail = string.Join(", ", parts);
        return $"\nnote: the generation is {status}{(detail.Length > 0 ? $" ({detail} of {coverage?["total"]} projects)" : string.Empty)}; an absence of results is not proof of absence (see the diagnostics table).";
    }

    private static QueryResult List(List<JsonObject> rows, Func<JsonObject, string> format, string empty) =>
        rows.Count == 0
            ? new QueryResult(NoResults, empty, new JsonArray())
            : new QueryResult(Results, string.Join('\n', rows.Select(format)), new JsonArray(rows.Cast<JsonNode>().ToArray()));

    private List<JsonObject> Rows(string sql, params object?[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue("$" + (i + 1).ToString(CultureInfo.InvariantCulture), parameters[i] switch
            {
                null => DBNull.Value,
                JsonNode node => node.GetValue<string>(),
                var value => value,
            });
        }
        using var reader = command.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                {
                    long number when number is >= int.MinValue and <= int.MaxValue => JsonValue.Create((int)number),
                    long number => JsonValue.Create(number),
                    double number => JsonValue.Create(number),
                    string text => JsonValue.Create(text),
                    var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    public string IndexPath => _indexPath;
}
