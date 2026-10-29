using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// MediatR (contract v1.1, in-process dispatch), by resolved symbol. ISender.Send / IPublisher.Publish (and IMediator)
/// become inprocess.send with the request type; IRequestHandler, INotificationHandler and IStreamRequestHandler
/// implementations become inprocess.handler. The linker joins them with dispatches_to links (via inprocess).
/// A request whose static type is an interface or abstract type is a candidate: the runtime type decides.
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
            if (!Handlers.TryGetValue(AdapterContext.FullName(@interface.OriginalDefinition), out var pattern) || context.IdOf(@interface.TypeArguments[0]) is not { } requestId)
            {
                continue;
            }
            context.Observe("inprocess.handler", new JsonObject
            {
                ["request"] = requestId,
                ["handler"] = handlerId,
                ["mechanism"] = "mediatr",
                ["pattern"] = pattern,
                ["resolution"] = "exact",
            }, context.Evidence(location));
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
        if (request is null || request.SpecialType == SpecialType.System_Object || context.IdOf(request) is not { } requestId
            || context.EnclosingMember(invocation.Syntax, model) is not { } sender)
        {
            return;
        }
        context.Observe("inprocess.send", new JsonObject
        {
            ["request"] = requestId,
            ["sender"] = sender,
            ["mechanism"] = "mediatr",
            ["pattern"] = pattern,
            ["resolution"] = request.TypeKind == TypeKind.Interface || request.IsAbstract ? "candidate" : "exact",
        }, context.Evidence(invocation.Syntax));
    }
}
