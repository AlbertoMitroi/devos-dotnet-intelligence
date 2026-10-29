using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Elasticsearch clients (contract v1.1, search.index), by resolved symbol: Elastic.Clients.Elasticsearch 8 and NEST.
/// Index, bulk, search, get, update and delete calls and index creation give the index name (literal argument,
/// configuration value per D21, ".Index(name)" in the request lambda, or DefaultMappingFor&lt;T&gt; / DefaultIndex in the
/// client settings) and the document type. Repository-wide, because settings and calls live in different projects.
/// </summary>
public sealed class ElasticsearchAdapter
{
    private static readonly string[] Namespaces = ["Elastic.Clients.Elasticsearch", "Nest", "Elasticsearch.Net"];
    private static readonly Dictionary<string, string> Operations = new(StringComparer.Ordinal)
    {
        ["Index"] = "index", ["IndexAsync"] = "index", ["IndexDocument"] = "index", ["IndexDocumentAsync"] = "index",
        ["IndexMany"] = "index", ["IndexManyAsync"] = "index", ["Bulk"] = "index", ["BulkAsync"] = "index",
        ["Search"] = "search", ["SearchAsync"] = "search", ["Count"] = "search", ["CountAsync"] = "search",
        ["Get"] = "get", ["GetAsync"] = "get",
        ["Update"] = "update", ["UpdateAsync"] = "update",
        ["Delete"] = "delete", ["DeleteAsync"] = "delete", ["DeleteByQuery"] = "delete", ["DeleteByQueryAsync"] = "delete",
        ["Create"] = "create", ["CreateAsync"] = "create",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, (JsonObject Index, List<string?> Evidence)> _defaultIndexByDocument = new(StringComparer.Ordinal);
    private (JsonObject Index, List<string?> Evidence)? _defaultIndex;
    private readonly List<(AdapterContext Context, JsonObject Fields, JsonObject? Index, string? Document, List<string?> Evidence)> _pending = [];

    public void VisitInvocation(IInvocationOperation invocation, AdapterContext context)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var containing = AdapterContext.FullName(method.ContainingType);
        if (!Namespaces.Any(n => containing.StartsWith(n + ".", StringComparison.Ordinal)))
        {
            return;
        }
        if (method.Name is "DefaultMappingFor" && invocation.TargetMethod.TypeArguments.Length == 1)
        {
            if (context.IdOf(invocation.TargetMethod.TypeArguments[0]) is { } mappedDocument && IndexInLambda(invocation, context, ["IndexName"]) is { } mapped)
            {
                lock (_gate) _defaultIndexByDocument.TryAdd(mappedDocument, mapped);
            }
            return;
        }
        if (method.Name == "DefaultIndex")
        {
            var evidence = new List<string?> { context.Evidence(invocation.Syntax) };
            var index = context.Configuration!.Destination("index", invocation.Arguments.LastOrDefault()?.Value, context, evidence);
            lock (_gate) _defaultIndex ??= (index, evidence);
            return;
        }
        // Only client calls; request descriptors have methods with the same names (.Index("x") inside a lambda).
        if (!Operations.TryGetValue(method.Name, out var operation) || !method.ContainingType.Name.EndsWith("Client", StringComparison.Ordinal))
        {
            return;
        }
        var indicesApi = containing.Contains("Indices", StringComparison.Ordinal) || containing.Contains("IndexManagement", StringComparison.Ordinal);
        if (operation == "create" && indicesApi)
        {
            operation = "create-index";
        }
        else if (indicesApi)
        {
            return;
        }
        var document = invocation.TargetMethod.TypeArguments.FirstOrDefault(t => t.TypeKind != TypeKind.TypeParameter && t.SpecialType == SpecialType.None);
        var evidenceList = new List<string?> { context.Evidence(invocation.Syntax) };
        JsonObject? indexDestination = null;
        var indexArgument = invocation.Arguments.FirstOrDefault(a => a.ArgumentKind != ArgumentKind.DefaultValue && a.Parameter is { } p
            && (p.Name is "index" or "indices" or "indexName" || p.Type.Name is "IndexName" or "Indices"));
        if (indexArgument is not null)
        {
            indexDestination = context.Configuration!.Destination("index", indexArgument.Value, context, evidenceList);
        }
        else if (IndexInLambda(invocation, context, ["Index", "Indices"]) is { } fromLambda)
        {
            indexDestination = fromLambda.Index;
            evidenceList.AddRange(fromLambda.Evidence);
        }
        var documentId = document is null ? null : context.IdOf(document);
        lock (_gate)
        {
            _pending.Add((context, new JsonObject
            {
                ["document"] = documentId,
                ["operation"] = operation,
                ["client"] = containing.StartsWith("Elastic.Clients.Elasticsearch", StringComparison.Ordinal) ? "elastic-clients-elasticsearch" : "nest",
                ["sender"] = context.EnclosingMember(invocation.Syntax, context.Compilation.GetSemanticModel(invocation.Syntax.SyntaxTree)),
            }, indexDestination, documentId, evidenceList));
        }
    }

    /// <summary>The index named by a call such as .Index("orders") or .IndexName("orders") inside the request lambda.</summary>
    private static (JsonObject Index, List<string?> Evidence)? IndexInLambda(IInvocationOperation invocation, AdapterContext context, string[] names)
    {
        foreach (var argument in invocation.Arguments.Select(a => AdapterContext.Unwrap(a.Value)))
        {
            var lambda = argument is IDelegateCreationOperation creation ? creation.Target : argument;
            if (lambda is not IAnonymousFunctionOperation function)
            {
                continue;
            }
            foreach (var call in function.Body.Descendants().OfType<IInvocationOperation>().Where(c => names.Contains(c.TargetMethod.Name) && c.Arguments.Length > 0))
            {
                var evidence = new List<string?> { context.Evidence(call.Syntax) };
                var value = call.Arguments.First(a => a.Parameter?.Ordinal == (call.TargetMethod.IsExtensionMethod ? 1 : 0)).Value;
                return (context.Configuration!.Destination("index", value, context, evidence), evidence);
            }
        }
        return null;
    }

    public void Emit()
    {
        foreach (var (context, fields, index, documentId, evidence) in _pending)
        {
            var copy = (JsonObject)fields.DeepClone();
            var allEvidence = new List<string?>(evidence);
            var resolved = index;
            if (resolved is null && documentId is not null && _defaultIndexByDocument.TryGetValue(documentId, out var mapped))
            {
                resolved = mapped.Index;
                allEvidence.AddRange(mapped.Evidence);
            }
            if (resolved is null && _defaultIndex is { } fallback)
            {
                resolved = fallback.Index;
                allEvidence.AddRange(fallback.Evidence);
            }
            copy["index"] = resolved?.DeepClone() ?? new JsonObject { ["kind"] = "index", ["name"] = null, ["nameSource"] = "unknown" };
            // A name with holes ("orders-{}") keeps its template under ext.dotnet.template: a candidate, not unresolved.
            copy["resolution"] = (string?)copy["index"]!["name"] is not null ? "exact" : copy["index"]!["ext.dotnet.template"] is not null ? "candidate" : "unresolved";
            context.Observe("search.index", copy, [.. allEvidence]);
        }
    }
}
