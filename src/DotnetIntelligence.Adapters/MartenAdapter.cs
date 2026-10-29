using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Marten (contract v1.1), by resolved symbol: events appended to streams (eventstore.append), registered projections
/// with their lifecycle and the events they handle (eventstore.projection), and document types stored or queried
/// (data.table, store marten-document, table by Marten's alias convention unless an alias is given).
/// </summary>
public sealed class MartenAdapter(AdapterContext context)
{
    private static readonly HashSet<string> AppendMethods = new(StringComparer.Ordinal) { "Append", "StartStream", "AppendOptimistic", "AppendExclusive" };
    private static readonly HashSet<string> DocumentMethods = new(StringComparer.Ordinal) { "Store", "Insert", "Update", "Delete", "Query", "Load", "LoadAsync", "LoadManyAsync", "HardDelete" };
    private static readonly HashSet<string> ProjectionHandlers = new(StringComparer.Ordinal) { "Apply", "Create", "Project", "ShouldDelete", "Transform", "Evolve", "DetermineAction" };
    private static readonly HashSet<string> ProjectionRegistrations = new(StringComparer.Ordinal) { "ProjectEvent", "ProjectEventAsync", "Identity", "Identities", "CreateEvent", "DeleteEvent", "TransformsEvent" };

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var containing = AdapterContext.FullName(method.ContainingType);
        // Marten 8 declares event and projection APIs in Marten.*; Marten 9 moved them to JasperFx.Events.* (shared base types).
        if (!containing.StartsWith("Marten", StringComparison.Ordinal) && !containing.StartsWith("JasperFx.Events", StringComparison.Ordinal))
        {
            return;
        }
        var receiver = AdapterContext.FullName(invocation.Instance is null ? null : AdapterContext.Unwrap(invocation.Instance).Type);
        if (AppendMethods.Contains(method.Name) && (containing.StartsWith("Marten.Events.", StringComparison.Ordinal) || containing.StartsWith("JasperFx.Events.", StringComparison.Ordinal)))
        {
            Append(invocation);
        }
        else if (method.Name is "Add" or "Snapshot" or "LiveStreamAggregation" or "InlineSelfAggregate"
            && receiver.StartsWith("Marten", StringComparison.Ordinal) && receiver.Contains("Projection", StringComparison.Ordinal))
        {
            Projection(invocation);
        }
        else if (method.Name == "For" && containing.StartsWith("Marten.MartenRegistry", StringComparison.Ordinal) && invocation.TargetMethod.TypeArguments.Length == 1)
        {
            // Schema.For<T>().DocumentAlias("x")
            var alias = invocation.Parent is IInvocationOperation { TargetMethod.Name: "DocumentAlias" } aliasCall
                && context.StringTemplate(aliasCall.Arguments.LastOrDefault()?.Value) is { Template: { } a, Constant: true } ? a : null;
            Document(invocation.TargetMethod.TypeArguments[0], invocation, alias);
        }
        else if (DocumentMethods.Contains(method.Name) && (AdapterContext.Implements(method.ContainingType, "Marten.IDocumentOperations") || AdapterContext.Implements(method.ContainingType, "Marten.IQuerySession")))
        {
            var document = invocation.TargetMethod.TypeArguments.Length == 1 ? invocation.TargetMethod.TypeArguments[0] : null;
            if (document is not null && document.TypeKind != TypeKind.TypeParameter)
            {
                Document(document, invocation, null);
            }
        }
    }

    private void Append(IInvocationOperation invocation)
    {
        var streamArgument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0 && a.ArgumentKind != ArgumentKind.ParamArray);
        // A string stream key folds to a template ("order-{}"); Guid keys and computed keys are "unknown".
        var (stream, _) = context.StringTemplate(streamArgument?.Value);
        var aggregate = invocation.TargetMethod.Name == "StartStream" && invocation.TargetMethod.TypeArguments.Length == 1 ? context.IdOf(invocation.TargetMethod.TypeArguments[0]) : null;
        var events = new List<ITypeSymbol>();
        var unknown = false;
        foreach (var argument in invocation.Arguments.Where(a => a.Parameter is { } p && p.Ordinal > 0 && a.ArgumentKind != ArgumentKind.DefaultValue))
        {
            if (argument.ArgumentKind == ArgumentKind.ParamArray && argument.Value is IArrayCreationOperation { Initializer: { } initializer })
            {
                events.AddRange(initializer.ElementValues.Select(v => AdapterContext.Unwrap(v).Type).Where(t => t is not null)!);
            }
            else if (argument.Parameter!.Type is IArrayTypeSymbol or INamedTypeSymbol { Name: "IEnumerable" or "IReadOnlyList" or "IList" })
            {
                unknown = true;
            }
        }
        var evidence = context.Evidence(invocation.Syntax);
        var streamValue = stream ?? "unknown";
        foreach (var type in events.Distinct<ITypeSymbol>(SymbolEqualityComparer.Default))
        {
            if (context.IdOf(type) is { } eventId && type.SpecialType == SpecialType.None)
            {
                context.Observe("eventstore.append", new JsonObject
                {
                    ["event"] = eventId,
                    ["stream"] = streamValue,
                    ["store"] = "marten",
                    ["aggregate"] = aggregate,
                    ["resolution"] = type.TypeKind == TypeKind.Interface || type.IsAbstract ? "candidate" : "exact",
                }, evidence);
            }
        }
        if (unknown || events.Count == 0)
        {
            context.Observe("eventstore.append", new JsonObject
            {
                ["event"] = null,
                ["stream"] = streamValue,
                ["store"] = "marten",
                ["aggregate"] = aggregate,
                ["resolution"] = "unresolved",
                ["reason"] = "The events are passed as a collection; their types are not known statically.",
            }, evidence);
        }
    }

    private void Projection(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        ITypeSymbol? projection = method.TypeArguments.Length == 1 ? method.TypeArguments[0]
            : invocation.Arguments.Select(a => AdapterContext.Unwrap(a.Value)).OfType<IObjectCreationOperation>().FirstOrDefault()?.Type;
        if (projection is null || context.IdOf(projection) is not { } projectionId)
        {
            return;
        }
        var lifecycle = method.Name == "LiveStreamAggregation" ? "live"
            : invocation.Arguments.Select(a => AdapterContext.Unwrap(a.Value)).OfType<IFieldReferenceOperation>()
                .FirstOrDefault(f => f.Field.ContainingType.TypeKind == TypeKind.Enum && f.Field.ContainingType.Name.EndsWith("Lifecycle", StringComparison.Ordinal))
                ?.Field.Name.ToLowerInvariant()
            ?? (method.Name == "InlineSelfAggregate" ? "inline" : "unknown");
        var events = HandledEvents(projection).Select(e => context.IdOf(e)).Where(e => e is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        context.Observe("eventstore.projection", new JsonObject
        {
            ["projection"] = projectionId,
            ["events"] = new JsonArray(events.Select(e => (JsonNode)e!).ToArray()),
            ["lifecycle"] = lifecycle,
            ["store"] = "marten",
            ["ext.dotnet.registration"] = method.Name,
        }, context.Evidence(invocation.Syntax));
    }

    /// <summary>First parameter of Apply/Create/Project/... methods and type arguments of ProjectEvent&lt;T&gt;-style registrations; IEvent&lt;T&gt; is unwrapped.</summary>
    private IEnumerable<ITypeSymbol> HandledEvents(ITypeSymbol projection)
    {
        for (var type = projection; type is not null && type.Locations.Any(l => l.IsInSource); type = type.BaseType)
        {
            foreach (var member in type.GetMembers().OfType<IMethodSymbol>().Where(m => ProjectionHandlers.Contains(m.Name) && m.Parameters.Length > 0))
            {
                yield return UnwrapEvent(member.Parameters[0].Type);
            }
            foreach (var syntax in type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).Where(s => context.Compilation.ContainsSyntaxTree(s.SyntaxTree)))
            {
                var model = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var node in syntax.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(node).Symbol is IMethodSymbol { TypeArguments.Length: >= 1 } call && ProjectionRegistrations.Contains(call.Name))
                    {
                        yield return UnwrapEvent(call.TypeArguments[0]);
                    }
                }
            }
        }
    }

    private static ITypeSymbol UnwrapEvent(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named && AdapterContext.FullName(named.OriginalDefinition) is "Marten.Events.IEvent" or "JasperFx.Events.IEvent"
            ? named.TypeArguments[0] : type;

    private void Document(ITypeSymbol document, IInvocationOperation invocation, string? alias)
    {
        if (context.IdOf(document) is not { } documentId || document.SpecialType != SpecialType.None)
        {
            return;
        }
        alias ??= document.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == "Marten.Schema.DocumentAliasAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
        // Marten's default alias: the type name in lower case, "outer_inner" for nested types.
        var name = alias ?? (document.ContainingType is { } outer ? $"{outer.Name}_{document.Name}" : document.Name).ToLowerInvariant();
        context.Observe("data.table", new JsonObject
        {
            ["entity"] = documentId,
            ["table"] = "mt_doc_" + name,
            ["schema"] = null,
            ["store"] = "marten-document",
            ["tableSource"] = alias is null ? "convention" : "literal",
        }, context.Evidence(invocation.Syntax));
    }
}
