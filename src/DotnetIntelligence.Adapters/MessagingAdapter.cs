using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Messaging (4.3). Generic heuristics produce candidates: classes implementing a generic "...Handler" interface,
/// generic subscription registrations (&lt;TEvent, THandler&gt;), and publish-like calls whose argument derives from the
/// parameter's event base type. Caller conventions turn matches into exact observations (method configured-convention).
/// Candidates are collected into suggested-conventions.yml for a human to confirm.
/// </summary>
public sealed class MessagingAdapter(AdapterContext context, Conventions conventions, SuggestionCollector suggestions)
{
    private const string ConventionMethod = "configured-convention";
    private static readonly string[] PublishWords = ["Publish", "Send", "Raise", "Emit", "Dispatch"];

    public void VisitType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
        {
            return;
        }
        foreach (var @interface in type.AllInterfaces.Where(i => i.IsGenericType))
        {
            var definition = AdapterContext.FullName(@interface.OriginalDefinition);
            var convention = conventions.Messaging.HandlerInterfaces.FirstOrDefault(c => c.Type == definition);
            if (convention is not null && convention.EventTypeArgument < @interface.TypeArguments.Length)
            {
                Subscribe(@interface.TypeArguments[convention.EventTypeArgument], type, "exact", ConventionMethod, location, "handler-interface", definition);
            }
            else if (@interface.TypeArguments.Length == 1 && @interface.Name.EndsWith("Handler", StringComparison.Ordinal))
            {
                Subscribe(@interface.TypeArguments[0], type, "candidate", null, location, "handler-interface", definition);
                suggestions.HandlerInterface(definition);
            }
        }
    }

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var name = AdapterContext.FullName(method.ContainingType) + "." + method.Name;
        // IOperation presents extension calls in their static form: the receiver is the first argument.
        var first = invocation.TargetMethod.IsExtensionMethod ? 1 : 0;
        var arguments = invocation.Arguments.Where(a => a.Parameter is { } p && p.Ordinal >= first && a.ArgumentKind != ArgumentKind.DefaultValue)
            .OrderBy(a => a.Parameter!.Ordinal).ToList();

        if (conventions.Messaging.Subscribe.FirstOrDefault(c => c.Method == name) is { } subscribe
            && Math.Max(subscribe.EventTypeArgument, subscribe.HandlerTypeArgument) < invocation.TargetMethod.TypeArguments.Length)
        {
            Subscribe(invocation.TargetMethod.TypeArguments[subscribe.EventTypeArgument], invocation.TargetMethod.TypeArguments[subscribe.HandlerTypeArgument],
                "exact", ConventionMethod, invocation.Syntax.GetLocation(), "subscription", name);
            return;
        }
        if (conventions.Messaging.Publish.FirstOrDefault(c => c.Method == name) is { } publish && publish.EventArgument < arguments.Count)
        {
            var types = PossibleTypes(arguments[publish.EventArgument].Value, 0).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToList();
            foreach (var type in types)
            {
                // A value chosen between branches publishes one of them: each is a candidate, not a certainty.
                Publish(type, invocation, model, types.Count == 1 ? "exact" : "candidate", ConventionMethod, name);
            }
            return;
        }

        var typeArguments = invocation.TargetMethod.TypeArguments;
        if (typeArguments.Length == 2 && typeArguments[1].AllInterfaces.Any(i => i.IsGenericType && i.Name.EndsWith("Handler", StringComparison.Ordinal)
                && i.TypeArguments.Length >= 1 && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], typeArguments[0])))
        {
            Subscribe(typeArguments[0], typeArguments[1], "candidate", null, invocation.Syntax.GetLocation(), "subscription", name);
            suggestions.Subscription(name);
            return;
        }
        if (PublishWords.Any(w => method.Name.StartsWith(w, StringComparison.Ordinal)) && arguments.Count > 0
            && arguments[0].Parameter?.Type is INamedTypeSymbol baseType && baseType.SpecialType == SpecialType.None
            && AdapterContext.Unwrap(arguments[0].Value).Type is INamedTypeSymbol argumentType
            && !SymbolEqualityComparer.Default.Equals(argumentType, baseType)
            && (AdapterContext.DerivesFrom(argumentType, AdapterContext.FullName(baseType)) || AdapterContext.Implements(argumentType, AdapterContext.FullName(baseType))))
        {
            Publish(argumentType, invocation, model, "candidate", null, name);
            suggestions.Publish(name);
        }
    }

    /// <summary>The concrete types a value can have: through locals, conditional and coalesce branches, conversions.</summary>
    private IEnumerable<ITypeSymbol> PossibleTypes(IOperation operation, int depth)
    {
        operation = AdapterContext.Unwrap(operation);
        if (depth > 8)
        {
            return operation.Type is { } t ? [t] : [];
        }
        return operation switch
        {
            ILocalReferenceOperation local when context.LocalValue(local.Local) is { } value => PossibleTypes(value, depth + 1),
            IConditionalOperation conditional when conditional.WhenFalse is not null =>
                PossibleTypes(conditional.WhenTrue, depth + 1).Concat(PossibleTypes(conditional.WhenFalse, depth + 1)),
            ICoalesceOperation coalesce => PossibleTypes(coalesce.Value, depth + 1).Concat(PossibleTypes(coalesce.WhenNull, depth + 1)),
            { Type: { } type } => [type],
            _ => [],
        };
    }

    private void Subscribe(ITypeSymbol contract, ITypeSymbol handler, string resolution, string? method, Location location, string via, string source)
    {
        if (context.IdOf(contract) is not { } contractId || context.IdOf(handler) is not { } handlerId)
        {
            return;
        }
        context.Observe("messaging.subscribe", new JsonObject
        {
            ["contract"] = Contract(contract, contractId),
            ["handler"] = handlerId,
            ["resolution"] = resolution,
            ["destination"] = null,
            ["transport"] = null,
            ["ext.dotnet.via"] = via,
            ["ext.dotnet.source"] = source,
        }, context.Evidence(location, method));
    }

    private void Publish(ITypeSymbol? contract, IInvocationOperation invocation, SemanticModel model, string resolution, string? method, string source)
    {
        if (contract is null || context.IdOf(contract) is not { } contractId || context.EnclosingMember(invocation.Syntax, model) is not { } publisher)
        {
            return;
        }
        context.Observe("messaging.publish", new JsonObject
        {
            ["contract"] = Contract(contract, contractId),
            ["publisher"] = publisher,
            ["resolution"] = resolution,
            ["destination"] = null,
            ["transport"] = null,
            ["ext.dotnet.source"] = source,
        }, context.Evidence(invocation.Syntax, method));
    }

    private JsonObject Contract(ITypeSymbol type, string id) => new()
    {
        ["entity"] = id,
        ["name"] = type.Name,
        ["fullName"] = AdapterContext.FullName(type),
        ["identity"] = conventions.Messaging.ContractIdentity == "typeName" ? "name:" + type.Name : id,
    };
}

/// <summary>Collects heuristic sources for suggested-conventions.yml (written into the generation, never applied).</summary>
public sealed class SuggestionCollector
{
    private readonly SortedSet<string> _publish = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _subscribe = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _handlers = new(StringComparer.Ordinal);

    public void Publish(string method) { lock (_publish) _publish.Add(method); }
    public void Subscription(string method) { lock (_subscribe) _subscribe.Add(method); }
    public void HandlerInterface(string type) { lock (_handlers) _handlers.Add(type); }

    public bool IsEmpty => _publish.Count == 0 && _subscribe.Count == 0 && _handlers.Count == 0;

    public string Render()
    {
        var b = new StringBuilder();
        b.Append("# Suggested messaging conventions, generated from heuristic candidates.\n");
        b.Append("# Review, keep what is right, and pass the file with --config to make the matches exact.\n");
        b.Append("messaging:\n  contractIdentity: entity\n");
        b.Append("  publish:\n");
        foreach (var m in _publish) b.Append($"    - method: {m}\n      eventArgument: 0\n");
        b.Append("  subscribe:\n");
        foreach (var m in _subscribe) b.Append($"    - method: {m}\n      eventTypeArgument: 0\n      handlerTypeArgument: 1\n");
        b.Append("  handlerInterfaces:\n");
        foreach (var t in _handlers) b.Append($"    - type: {t}\n      eventTypeArgument: 0\n");
        return b.ToString();
    }
}
