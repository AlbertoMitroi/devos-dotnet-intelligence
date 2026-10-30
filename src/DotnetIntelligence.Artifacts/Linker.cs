using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Links between services inside the repository (5.1), from normalized observation fields only. Every link keeps
/// the evidence of both sides plus the configuration or topology evidence it used. Several matches are all kept as
/// ambiguous; none gives an unresolved link with the reason. A link is only as strong as its weakest part.
/// </summary>
public static class Linker
{
    private static readonly string[] Strength = ["unresolved", "candidate", "ambiguous", "exact"];

    /// <param name="relations">inherits and implements relations, for candidate in-process sends (C8 rule 4); others are ignored.</param>
    public static IReadOnlyList<JsonObject> Link(IReadOnlyList<JsonObject> observations, IReadOnlyList<JsonObject> projects, IEnumerable<JsonObject>? relations = null)
    {
        var links = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var byKind = observations.GroupBy(o => (string)o["kind"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        List<JsonObject> Of(string kind) => byKind.GetValueOrDefault(kind) ?? [];

        var resources = Of("topology.resource").Where(r => r["name"] is not null)
            .GroupBy(r => ((string)r["name"]!).ToLowerInvariant(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var registrations = Of("di.registers");

        // ---- HTTP: http.call -> typed client registration -> base address -> topology resource -> project -> endpoint ----
        var endpointsByProject = Of("http.endpoint").GroupBy(e => (string)e["project"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var call in Of("http.call"))
        {
            var evidence = Evidence(call);
            var parts = new List<string> { (string)call["resolution"]! };
            string? reason = null;
            var targets = new List<JsonObject>();
            var route = (string?)call["canonicalRoute"];
            var registration = registrations.FirstOrDefault(r => (string?)r["ext.dotnet.registration"] == "typedHttpClient"
                && ((string?)r["service"] == (string?)call["client"] || (string?)r["implementation"] == (string?)call["client"]));
            var baseAddress = (string?)registration?["baseAddress"];
            if (route is null)
            {
                reason = (string?)call["reason"] ?? "The request URI could not be resolved.";
            }
            else if (call["remoteService"] is not null)
            {
                // Generated client proxies (ABP) carry the server's route; the base URL points at a gateway or a service
                // host, so the target is any endpoint of the repository with the same verb and route.
                var scored = Of("http.endpoint").Where(e => MethodMatches((string)call["method"]!, (string)e["method"]!))
                    .Select(e => (Endpoint: e, Score: RouteScore(route, (string)e["canonicalRoute"]!))).Where(x => x.Score is not null).ToList();
                if (scored.Count > 0)
                {
                    var best = scored.Max(x => x.Score);
                    targets.AddRange(scored.Where(x => x.Score == best).Select(x => x.Endpoint));
                }
                else
                {
                    reason = $"No endpoint of the repository matches {call["method"]} {route}.";
                }
            }
            else if (registration is null || baseAddress is null)
            {
                reason = "No typed-client registration with a base address was found for the calling client.";
            }
            else
            {
                evidence.AddRange(Evidence(registration));
                parts.Add((bool?)registration["baseAddressResolved"] == true ? "exact" : "candidate");
                var (host, basePath) = HostOf(baseAddress);
                if (host is null || !resources.TryGetValue(host.ToLowerInvariant(), out var hostResources))
                {
                    reason = $"Base address '{baseAddress}' does not name a topology resource.";
                }
                else
                {
                    var projectResources = hostResources.Where(r => r["targetProject"] is not null).ToList();
                    evidence.AddRange(projectResources.SelectMany(Evidence));
                    var topologyReferences = Of("topology.reference").Where(r => ((string?)r["to"])?.Equals(host, StringComparison.OrdinalIgnoreCase) == true).ToList();
                    evidence.AddRange(topologyReferences.SelectMany(Evidence));
                    var fullRoute = Combine(basePath, route);
                    var scored = new List<(JsonObject Endpoint, int Score)>();
                    foreach (var targetProject in projectResources.Select(r => (string)r["targetProject"]!).Distinct(StringComparer.Ordinal))
                    {
                        foreach (var endpoint in endpointsByProject.GetValueOrDefault(targetProject) ?? [])
                        {
                            if (MethodMatches((string)call["method"]!, (string)endpoint["method"]!) && RouteScore(fullRoute, (string)endpoint["canonicalRoute"]!) is { } score)
                            {
                                scored.Add((endpoint, score));
                            }
                        }
                    }
                    // Route precedence: keep only the best-scoring endpoints (literal segments before parameters, as ASP.NET Core routing does).
                    if (scored.Count > 0)
                    {
                        var best = scored.Max(s => s.Score);
                        targets.AddRange(scored.Where(s => s.Score == best).Select(s => s.Endpoint));
                    }
                    if (projectResources.Count == 0)
                    {
                        reason = $"Topology resource '{host}' is not a project of this repository.";
                    }
                    else if (targets.Count == 0)
                    {
                        reason = $"No endpoint of {string.Join(", ", projectResources.Select(r => (string)r["targetProject"]!).Distinct(StringComparer.Ordinal))} matches {call["method"]} {fullRoute}.";
                    }
                }
            }
            AddLinks(links, "configured_route", "http", call, targets, evidence, parts, reason);
        }

        // ---- gRPC: grpc.call -> grpc.service by fully qualified service and method ----
        var services = Of("grpc.service").GroupBy(s => $"{s["service"]}/{s["method"]}", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var call in Of("grpc.call"))
        {
            var targets = services.GetValueOrDefault($"{call["service"]}/{call["method"]}") ?? [];
            var evidence = Evidence(call);
            evidence.AddRange(Of("grpc.proto").Where(p => (string?)p["service"] == (string?)call["service"]).SelectMany(Evidence));
            evidence.AddRange(registrations.Where(r => (string?)r["ext.dotnet.registration"] == "grpcClient" && (string?)r["service"] == (string?)call["client"]).SelectMany(Evidence));
            AddLinks(links, "consumes_contract", "grpc", call, targets, evidence, ["exact"],
                targets.Count == 0 ? $"No implementation of {call["service"]}/{call["method"]} was found in the repository." : null);
        }

        // ---- messaging: publish -> subscribe by contract identity; same simple name only as possible communication ----
        var subscribers = Of("messaging.subscribe");
        foreach (var publish in Of("messaging.publish"))
        {
            var identity = (string?)publish["contract"]!["identity"];
            var exactTargets = Subscriptions(subscribers.Where(s => (string?)s["contract"]!["identity"] == identity));
            AddLinks(links, "consumes_contract", "messaging", publish, exactTargets, Evidence(publish), [(string)publish["resolution"]!],
                exactTargets.Count == 0 ? "No subscriber of the same contract was found." : null, keepUnresolved: false, fanOut: true);
            var name = (string?)publish["contract"]!["name"];
            var nameTargets = Subscriptions(subscribers.Where(s => (string?)s["contract"]!["identity"] != identity && (string?)s["contract"]!["name"] == name));
            AddLinks(links, "possible_communication", "messaging", publish, nameTargets, Evidence(publish), ["candidate"],
                exactTargets.Count == 0 && nameTargets.Count == 0 ? "No subscriber of the same contract or of a contract with the same name was found." : null,
                keepUnresolved: exactTargets.Count == 0, fanOut: true);
        }

        // ---- in-process dispatch (contract v1.2, C8): inprocess.send -> inprocess.handler ----
        LinkDispatch(links, Of("inprocess.send"), Of("inprocess.handler"), relations ?? []);

        // ---- workflow: workflow.node -> workflow.handler by role, process and name (conventions) ----
        var workflowHandlers = Of("workflow.handler").Where(h => (string?)h["resolution"] != "unresolved").ToList();
        var roles = workflowHandlers.Select(h => (string)h["role"]!).ToHashSet(StringComparer.Ordinal);
        foreach (var node in Of("workflow.node"))
        {
            var nodeType = (string)node["nodeType"]!;
            // The "event" role handles message events (a flow event name against the BPMN message name).
            var isEvent = node["messageName"] is not null;
            if (!roles.Contains(nodeType) && !(isEvent && roles.Contains("event")))
            {
                continue;
            }
            var names = new List<string?> { (string?)node["name"], (string?)node["messageName"] };
            names.AddRange((node["metadata"] as JsonObject)?.Select(m => (string?)m.Value) ?? []);
            var scored = new List<(JsonObject Handler, string Strength)>();
            foreach (var handler in workflowHandlers)
            {
                var role = (string)handler["role"]!;
                var process = (string?)handler["processId"];
                if (!(role == nodeType || role == "event" && isEvent) || process is not ("*" or null) && process != (string?)node["processId"])
                {
                    continue;
                }
                var handled = (string?)handler["nodeName"] ?? (string?)handler["eventName"];
                if (handled is null)
                {
                    continue;
                }
                if (names.Any(n => n == handled))
                {
                    scored.Add((handler, (string)handler["resolution"]!));
                }
                else if (names.Any(n => n is not null && Normalize(n) == Normalize(handled)))
                {
                    scored.Add((handler, "candidate"));
                }
            }
            var best = scored.Count == 0 ? [] : scored.Where(x => x.Strength == scored.Select(y => y.Strength).OrderByDescending(r => Array.IndexOf(Strength, r)).First()).ToList();
            var targets = best.Select(x =>
            {
                var copy = (JsonObject)x.Handler.DeepClone();
                copy["resolution"] = x.Strength;
                return copy;
            }).ToList();
            AddLinks(links, "dispatches_to", "workflow", node, targets, Evidence(node), ["exact"],
                targets.Count == 0 ? $"No handler with role {nodeType} names '{node["name"]}' (process {node["processId"]})." : null, keepUnresolved: true);
        }

        // ---- DI: di.consumes -> di.registers of the same service in the consumer's composition roots ----
        var references = projects.ToDictionary(p => (string)p["path"]!, p => (p["projectReferences"]?.AsArray() ?? []).Select(r => (string)r!).ToList(), StringComparer.Ordinal);
        var registrationsByService = registrations.GroupBy(r => (string)r["service"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var consume in Of("di.consumes"))
        {
            var service = (string)consume["service"]!;
            if (!registrationsByService.TryGetValue(service, out var candidates))
            {
                continue;
            }
            var consumerProject = (string)consume["project"]!;
            var key = (string?)consume["key"];
            // The composition root decides where a registration applies, not the project of the registering code:
            // a library's AddX extension called from an application's entry point registers into that application.
            var roots = candidates.Where(r => (string?)r["key"] == key)
                .Where(r => RootProject(r) is var root && (root == consumerProject || References(references, root, consumerProject))).ToList();
            AddLinks(links, "consumes_contract", "di", consume, roots, Evidence(consume), ["exact"],
                roots.Count == 0 ? "The service is registered only in composition roots that do not include the consumer's project." : null, keepUnresolved: true);
        }

        return links.Values.ToList();
    }

    /// <summary>
    /// C8: handlers of a send share mechanism, pattern (defaults: request for mediatr, notification for ngrx) and request
    /// (and requestTypeArguments when both carry them). request and stream: one handler gives one link as strong as both
    /// sides, several are all ambiguous, none is unresolved. notification fans out. A candidate send links, as candidate,
    /// to every handler whose request derives from or implements its static type. Evidence: both sites.
    /// </summary>
    private static void LinkDispatch(Dictionary<string, JsonObject> links, List<JsonObject> sends, List<JsonObject> handlers, IEnumerable<JsonObject> relations)
    {
        var supertypes = relations.Where(r => (string?)r["kind"] is "inherits" or "implements")
            .GroupBy(r => (string)r["from"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => (string)r["to"]!).ToList(), StringComparer.Ordinal);
        bool DerivesFrom(string type, string ancestor)
        {
            if (ancestor.EndsWith(":T:System.Object", StringComparison.Ordinal))
            {
                return true;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([type]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == ancestor)
                {
                    return true;
                }
                foreach (var next in supertypes.GetValueOrDefault(current) ?? [])
                {
                    if (seen.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }
            return false;
        }
        static string PatternOf(JsonObject o) => (string?)o["pattern"] ?? ((string?)o["mechanism"] == "ngrx" ? "notification" : "request");
        static string? Arguments(JsonObject o) => o["requestTypeArguments"] is JsonArray a ? a.ToJsonString() : null;

        foreach (var send in sends)
        {
            var pattern = PatternOf(send);
            var request = (string)send["request"]!;
            var sendResolution = (string?)send["resolution"] ?? "exact";
            var sameKind = handlers.Where(h => (string?)h["mechanism"] == (string?)send["mechanism"] && PatternOf(h) == pattern).ToList();
            var evidence = Site(send);
            if (sendResolution == "candidate")
            {
                var derived = sameKind.Where(h => (string)h["request"]! == request || DerivesFrom((string)h["request"]!, request))
                    .OrderBy(h => (string)h["id"]!, StringComparer.Ordinal).ToList();
                if (derived.Count == 0)
                {
                    Add(links, "dispatches_to", "inprocess", send, null, "unresolved", evidence,
                        $"No {send["mechanism"]} {pattern} handler of a request that derives from or implements {request} was found in the repository.");
                }
                foreach (var handler in derived)
                {
                    Add(links, "dispatches_to", "inprocess", send, handler, "candidate", [.. evidence, .. Site(handler)], null);
                }
                continue;
            }
            var matching = sameKind.Where(h => (string)h["request"]! == request
                    && (Arguments(send) is null || Arguments(h) is null || Arguments(send) == Arguments(h)))
                .OrderBy(h => (string)h["id"]!, StringComparer.Ordinal).ToList();
            if (matching.Count == 0)
            {
                // Open generic handlers (candidate, request = their constraint) receive every request that derives from it.
                var open = sameKind.Where(h => (string?)h["resolution"] == "candidate" && DerivesFrom(request, (string)h["request"]!))
                    .OrderBy(h => (string)h["id"]!, StringComparer.Ordinal).ToList();
                foreach (var handler in open)
                {
                    Add(links, "dispatches_to", "inprocess", send, handler, "candidate", [.. evidence, .. Site(handler)],
                        $"Open generic handler: {request} satisfies its constraint {handler["request"]}.");
                }
                if (open.Count == 0)
                {
                    Add(links, "dispatches_to", "inprocess", send, null, "unresolved", evidence,
                        $"No {send["mechanism"]} {pattern} handler of {request} was found in the repository.");
                }
                continue;
            }
            foreach (var handler in matching)
            {
                var oneSided = (Arguments(send) is null) != (Arguments(handler) is null);
                var weakest = new[] { sendResolution, (string?)handler["resolution"] ?? "exact", oneSided ? "candidate" : "exact" }
                    .Select(r => Array.IndexOf(Strength, r)).Where(i => i >= 0).DefaultIfEmpty(3).Min();
                var resolution = pattern != "notification" && matching.Count > 1 ? (weakest >= 2 ? "ambiguous" : Strength[weakest]) : Strength[weakest];
                Add(links, "dispatches_to", "inprocess", send, handler, resolution, [.. evidence, .. Site(handler)],
                    oneSided ? $"Only the {(Arguments(send) is null ? "handler" : "send")} states the type arguments of the generic request {request}." : null);
            }
        }
    }

    /// <summary>The observation's site (v1.2), else its whole evidence list.</summary>
    private static List<string> Site(JsonObject observation) => (string?)observation["site"] is { } site ? [site] : Evidence(observation);

    /// <summary>
    /// One target per subscription (project and handler): the handler interface and the subscription registration are
    /// two observations of the same subscription. The first by id is the target; the others add their evidence.
    /// </summary>
    private static List<JsonObject> Subscriptions(IEnumerable<JsonObject> subscribers) =>
        subscribers.GroupBy(s => $"{s["project"]}|{s["handler"]}", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                // The handler type is the subscription's natural representative; registrations add their evidence.
                var ordered = g.OrderBy(s => (string?)s["ext.dotnet.via"] == "handler-interface" ? 0 : 1).ThenBy(s => (string)s["id"]!, StringComparer.Ordinal).ToList();
                var merged = (JsonObject)ordered[0].DeepClone();
                merged["evidence"] = new JsonArray(ordered.SelectMany(Evidence).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(e => (JsonNode)e).ToArray());
                merged["resolution"] = ordered.Select(s => (string)s["resolution"]!).OrderByDescending(r => Array.IndexOf(Strength, r)).First();
                return merged;
            }).ToList();

    /// <param name="fanOut">Publish/subscribe: several subscribers are all real receivers, not alternatives, so they are not ambiguous (D18).</param>
    /// <summary>The project of a registration's composition root ("dotnet:{project}:{documentation id}"), else the registering project.</summary>
    private static string RootProject(JsonObject registration)
    {
        if ((string?)registration["compositionRoot"] is { } root && root.StartsWith("dotnet:", StringComparison.Ordinal) && !root.StartsWith("dotnet:pkg:", StringComparison.Ordinal))
        {
            var end = root.IndexOf(".csproj:", StringComparison.Ordinal);
            if (end > 0)
            {
                return root["dotnet:".Length..(end + ".csproj".Length)];
            }
        }
        return (string)registration["project"]!;
    }

    private static void AddLinks(Dictionary<string, JsonObject> links, string kind, string via, JsonObject from, List<JsonObject> targets,
        List<string> evidence, List<string> parts, string? reason, bool keepUnresolved = true, bool fanOut = false)
    {
        var distinctTargets = targets.DistinctBy(t => (string)t["id"]!).OrderBy(t => (string)t["id"]!, StringComparer.Ordinal).ToList();
        if (distinctTargets.Count == 0)
        {
            if (keepUnresolved)
            {
                Add(links, kind, via, from, null, "unresolved", evidence, reason);
            }
            return;
        }
        foreach (var target in distinctTargets)
        {
            var targetParts = fanOut ? parts.Append((string?)target["resolution"] ?? "exact") : parts.Concat(distinctTargets.Select(t => (string?)t["resolution"] ?? "exact"));
            var weakest = targetParts.Select(p => Array.IndexOf(Strength, p)).Where(i => i >= 0).DefaultIfEmpty(3).Min();
            var resolution = !fanOut && distinctTargets.Count > 1 ? (weakest >= 2 ? "ambiguous" : Strength[weakest]) : Strength[weakest];
            Add(links, kind, via, from, target, resolution, [.. evidence, .. Evidence(target)], null);
        }
    }

    private static void Add(Dictionary<string, JsonObject> links, string kind, string via, JsonObject from, JsonObject? to, string resolution, List<string> evidence, string? reason)
    {
        var identity = new JsonObject
        {
            ["kind"] = kind,
            ["via"] = via,
            ["from"] = Side(from),
            ["to"] = to is null ? null : Side(to),
        };
        var id = Hashing.RecordId("lnk", identity);
        var record = (JsonObject)identity.DeepClone();
        record["id"] = id;
        record["resolution"] = resolution;
        record["evidence"] = new JsonArray(evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(e => (JsonNode)e).ToArray());
        if (reason is not null)
        {
            record["reason"] = reason;
        }
        links[id] = record;
    }

    private static JsonObject Side(JsonObject observation) => new()
    {
        ["observation"] = (string)observation["id"]!,
        ["kind"] = (string)observation["kind"]!,
        ["project"] = (string)observation["project"]!,
    };

    private static List<string> Evidence(JsonObject observation) => observation["evidence"]!.AsArray().Select(e => (string)e!).ToList();

    private static bool References(Dictionary<string, List<string>> references, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var next in references.GetValueOrDefault(current) ?? [])
            {
                if (next == to)
                {
                    return true;
                }
                if (seen.Add(next))
                {
                    stack.Push(next);
                }
            }
        }
        return false;
    }

    /// <summary>Host and path of a base address; "https+http://catalog-api" (service discovery scheme lists) gives "catalog-api".</summary>
    internal static (string? Host, string Path) HostOf(string address)
    {
        var withoutScheme = address.Contains("://", StringComparison.Ordinal) ? address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..] : address;
        var slash = withoutScheme.IndexOf('/');
        var authority = slash < 0 ? withoutScheme : withoutScheme[..slash];
        var path = slash < 0 ? string.Empty : withoutScheme[slash..];
        var host = authority.Split(':')[0];
        return (host.Length == 0 || host.Contains('{') ? null : host, path);
    }

    internal static string Combine(string basePath, string route)
    {
        var canonicalBase = Contracts.Routes.Canonical(basePath);
        return canonicalBase == "/" ? route : canonicalBase + (route == "/" ? string.Empty : route);
    }


    /// <summary>Lower case letters and digits only: "Send Quote" and "SendQuote" compare equal.</summary>
    internal static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static bool MethodMatches(string call, string endpoint) => call == "{}" || endpoint == "*" || endpoint == "{}" || call == endpoint;

    /// <summary>
    /// Segment match with a precedence score, or null when the route does not match. Per segment: literal = literal 3,
    /// call hole against an endpoint parameter 2, call hole against a literal or a literal against a parameter 1.
    /// "{?}" may be absent, "{**}" takes the rest.
    /// </summary>
    internal static int? RouteScore(string call, string endpoint)
    {
        var c = call.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var e = endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var score = 0;
        for (var i = 0; i < e.Length; i++)
        {
            if (e[i] == "{**}")
            {
                return score;
            }
            if (i >= c.Length)
            {
                return e.Skip(i).All(s => s == "{?}") ? score : null;
            }
            var endpointParameter = e[i] is "{}" or "{?}";
            var callHole = c[i] == "{}";
            if (c[i] == e[i] && !callHole)
            {
                score += 3;
            }
            else if (callHole && endpointParameter)
            {
                score += 2;
            }
            else if (callHole || endpointParameter || c[i].Contains("{}", StringComparison.Ordinal) && SegmentPattern(c[i]).IsMatch(e[i]))
            {
                score += 1;
            }
            else
            {
                return null;
            }
        }
        return c.Length == e.Length ? score : null;
    }

    private static System.Text.RegularExpressions.Regex SegmentPattern(string segment) =>
        new("^" + System.Text.RegularExpressions.Regex.Escape(segment).Replace(@"\{}", ".*", StringComparison.Ordinal) + "$");
}
