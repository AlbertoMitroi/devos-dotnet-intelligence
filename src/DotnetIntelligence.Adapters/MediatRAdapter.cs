using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// MediatR in-process dispatch (contract v1.2, C8), by resolved symbol. ISender.Send / IPublisher.Publish /
/// CreateStream (and IMediator) become inprocess.send with the static type of the argument; one inprocess.handler per
/// implemented IRequestHandler, INotificationHandler or IStreamRequestHandler interface, with the class as handler and
/// the implementing Handle method as handlerMethod. Constructed generic requests carry requestTypeArguments. A send
/// whose static type is an interface, an abstract class, a type parameter or object is a candidate; an open generic
/// handler is a candidate on its type parameter's constraint, or a diagnostic when it has none.
/// </summary>
public sealed class MediatRAdapter(AdapterContext context)
{
    private static readonly Dictionary<string, string> Handlers = new(StringComparer.Ordinal)
    {
        ["MediatR.IRequestHandler"] = "request",
        ["MediatR.INotificationHandler"] = "notification",
        ["MediatR.IStreamRequestHandler"] = "stream",
    };

    public void VisitType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.Locations.FirstOrDefault(l => l.IsInSource) is not { } location || context.IdOf(type) is not { } handlerId)
        {
            return;
        }
        foreach (var @interface in type.AllInterfaces.Where(i => i.IsGenericType))
        {
            if (!Handlers.TryGetValue(AdapterContext.FullName(@interface.OriginalDefinition), out var pattern))
            {
                continue;
            }
            var argument = @interface.TypeArguments[0];
            var resolution = "exact";
            if (argument is ITypeParameterSymbol parameter)
            {
                if (Constraint(parameter) is not { } constraint)
                {
                    context.Diagnostics.Add(new AnalysisDiagnostic(Analysis.DiagnosticSeverity.Info, "open-generic-handler-without-constraint", context.Project.ProjectPath, DiagnosticState.Unsupported,
                        $"{AdapterContext.FullName(type)} handles {parameter.Name} of {AdapterContext.FullName(@interface.OriginalDefinition)} without a class or interface constraint; no inprocess.handler is emitted for it."));
                    continue;
                }
                argument = constraint;
                resolution = "candidate";
            }
            if (context.IdOf(argument) is not { } requestId)
            {
                continue;
            }
            var fields = new JsonObject
            {
                ["request"] = requestId,
                ["handler"] = handlerId,
                ["mechanism"] = "mediatr",
                ["pattern"] = pattern,
                ["resolution"] = resolution,
            };
            if (TypeArguments(argument) is { } arguments)
            {
                fields["requestTypeArguments"] = arguments;
            }
            var handle = @interface.GetMembers("Handle").OfType<IMethodSymbol>().FirstOrDefault();
            if (handle is not null && type.FindImplementationForInterfaceMember(handle) is IMethodSymbol implementation && context.IdOf(implementation) is { } methodId)
            {
                fields["handlerMethod"] = methodId;
            }
            context.Observe("inprocess.handler", fields, context.Evidence(location));
        }
    }

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod;
        var containing = AdapterContext.FullName(method.ContainingType);
        var pattern = (containing, method.Name) switch
        {
            ("MediatR.ISender" or "MediatR.IMediator", "Send") => "request",
            ("MediatR.ISender" or "MediatR.IMediator", "CreateStream") => "stream",
            ("MediatR.IPublisher" or "MediatR.IMediator", "Publish") => "notification",
            _ => null,
        };
        if (pattern is null || invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0) is not { } argument)
        {
            return;
        }
        var request = AdapterContext.Unwrap(argument.Value).Type;
        if (request is ITypeParameterSymbol parameter)
        {
            request = Constraint(parameter) ?? context.Compilation.GetSpecialType(SpecialType.System_Object);
        }
        if (request is null || context.IdOf(request) is not { } requestId || context.EnclosingMember(invocation.Syntax, model) is not { } sender)
        {
            return;
        }
        var concrete = request.TypeKind is TypeKind.Class or TypeKind.Struct && !request.IsAbstract && request.SpecialType != SpecialType.System_Object
            && AdapterContext.Unwrap(argument.Value).Type is not ITypeParameterSymbol;
        var fields = new JsonObject
        {
            ["request"] = requestId,
            ["sender"] = sender,
            ["mechanism"] = "mediatr",
            ["pattern"] = pattern,
            ["resolution"] = concrete ? "exact" : "candidate",
        };
        if (TypeArguments(request) is { } arguments)
        {
            fields["requestTypeArguments"] = arguments;
        }
        context.Observe("inprocess.send", fields, context.Evidence(invocation.Syntax));
    }

    private static ITypeSymbol? Constraint(ITypeParameterSymbol parameter) =>
        parameter.ConstraintTypes.FirstOrDefault(t => t.TypeKind is TypeKind.Class or TypeKind.Interface);

    /// <summary>Entities of the type arguments of a constructed generic type, in order; null for a non-generic type or when an argument has no entity (a type parameter).</summary>
    private JsonArray? TypeArguments(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named || SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition))
        {
            return null;
        }
        var ids = named.TypeArguments.Select(a => a is ITypeParameterSymbol ? null : context.IdOf(a)).ToList();
        return ids.Any(i => i is null) ? null : new JsonArray(ids.Select(i => (JsonNode)i!).ToArray());
    }
}
