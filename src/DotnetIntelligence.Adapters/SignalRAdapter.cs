using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// SignalR (contract v1.1), by resolved symbol: MapHub&lt;THub&gt;(route) gives realtime.hub; SendAsync / SendCoreAsync on
/// an IClientProxy and calls on strongly typed client interfaces give realtime.send with the hub (from IHubContext&lt;THub&gt;
/// or the enclosing Hub), the client method name and the audience (Clients.All, User(s), Group(s)). Caller conventions
/// name a codebase's own helpers over SignalR.
/// </summary>
public sealed class SignalRAdapter(AdapterContext context)
{
    private const string SignalR = "Microsoft.AspNetCore.SignalR";

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var containing = AdapterContext.FullName(method.ContainingType);
        if (method.Name == "MapHub" && invocation.TargetMethod.TypeArguments.Length == 1)
        {
            var (route, constant) = context.StringTemplate(AdapterContext.Argument(invocation, "pattern"));
            context.Observe("realtime.hub", new JsonObject
            {
                ["hub"] = context.IdOf(invocation.TargetMethod.TypeArguments[0]),
                ["route"] = route,
                ["canonicalRoute"] = route is null ? null : Routes.Canonical(route),
                ["resolution"] = route is null ? "unresolved" : constant ? "exact" : "candidate",
            }, context.Evidence(invocation.Syntax));
            return;
        }
        if (method.Name is "SendAsync" or "SendCoreAsync" && containing is SignalR + ".ClientProxyExtensions" or SignalR + ".IClientProxy" or SignalR + ".ISingleClientProxy")
        {
            var methodArgument = AdapterContext.Argument(invocation, "method");
            var (name, constant) = context.StringTemplate(methodArgument);
            Send(invocation, model, AdapterContext.Receiver(invocation), name, constant, "signalr", DestinationParameter(methodArgument, "method"));
            return;
        }
        // Strongly typed hubs: Clients.All.ReceiveOrder(...) on the client interface T of IHubContext<THub, T> / Hub<T>.
        if (AdapterContext.Receiver(invocation) is { } receiver && ClientsAccess(receiver) is not null && method.ContainingType.TypeKind == TypeKind.Interface
            && !containing.StartsWith(SignalR, StringComparison.Ordinal))
        {
            Send(invocation, model, receiver, method.Name, true, "signalr-typed");
            return;
        }
        var name2 = containing + "." + method.Name;
        if (context.Conventions.Realtime.Send.FirstOrDefault(c => c.Method == name2) is { } convention)
        {
            var first = invocation.TargetMethod.IsExtensionMethod ? 1 : 0;
            var argument = convention.MethodArgument is { } index
                ? invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == index + first && a.ArgumentKind != ArgumentKind.DefaultValue)?.Value
                : null;
            var (clientMethod, constant) = argument is null ? (convention.ClientMethod, convention.ClientMethod is not null) : context.StringTemplate(argument);
            var hub = convention.Hub is null ? null : context.Compilation.GetTypeByMetadataName(convention.Hub);
            context.Observe("realtime.send", new JsonObject
            {
                ["hub"] = hub is null ? null : context.IdOf(hub),
                ["method"] = clientMethod,
                ["audience"] = convention.Audience,
                ["sender"] = context.EnclosingMember(invocation.Syntax, model),
                ["resolution"] = clientMethod is null ? "unresolved" : constant ? "exact" : "candidate",
                ["ext.dotnet.source"] = name2,
            }, context.Evidence(invocation.Syntax, "configured-convention"));
        }
    }

    /// <summary>
    /// C7 template: the destination is a parameter of the member that contains the call (a helper such as
    /// SendToAll(string method, ...)); call sites carry the value in call.argument observations.
    /// </summary>
    internal static JsonObject? DestinationParameter(IOperation? argument, string field) =>
        argument is null ? null : AdapterContext.Unwrap(argument) is IParameterReferenceOperation { Parameter: var parameter } && parameter.ContainingSymbol is IMethodSymbol
            ? new JsonObject { ["field"] = field, ["parameter"] = parameter.Name, ["ordinal"] = parameter.Ordinal }
            : null;

    private void Send(IInvocationOperation invocation, SemanticModel model, IOperation? receiver, string? method, bool constant, string via, JsonObject? destinationParameter = null)
    {
        var (hub, audience, detail) = Target(receiver, invocation, model);
        var fields = new JsonObject
        {
            ["hub"] = hub,
            ["method"] = method,
            ["audience"] = audience,
            ["sender"] = context.EnclosingMember(invocation.Syntax, model),
            ["resolution"] = method is null ? "unresolved" : constant && hub is not null ? "exact" : "candidate",
            ["ext.dotnet.audience"] = detail,
            ["ext.dotnet.via"] = via,
        };
        if (destinationParameter is not null)
        {
            fields["destinationParameter"] = destinationParameter;
        }
        context.Observe("realtime.send", fields, context.Evidence(invocation.Syntax));
    }

    /// <summary>The hub entity and audience behind "hubContext.Clients.Group(x)" or "Clients.All" inside a hub.</summary>
    private (string? Hub, string Audience, string Detail) Target(IOperation? receiver, IInvocationOperation invocation, SemanticModel model)
    {
        receiver = receiver is null ? null : AdapterContext.Unwrap(receiver);
        if (receiver is ILocalReferenceOperation local && context.LocalValue(local.Local) is { } value)
        {
            receiver = value;
        }
        var detail = receiver switch
        {
            IPropertyReferenceOperation property => property.Property.Name,
            IInvocationOperation call => call.TargetMethod.Name,
            _ => "unknown",
        };
        var audience = detail switch
        {
            "All" => "all",
            "User" or "Users" => "users",
            "Group" or "Groups" or "GroupExcept" or "OthersInGroup" => "group",
            _ => "unknown",
        };
        string? hub = null;
        if (receiver is not null && ClientsAccess(receiver) is { } clients)
        {
            var owner = clients.Instance is null ? null : AdapterContext.Unwrap(clients.Instance).Type;
            if (owner is INamedTypeSymbol { IsGenericType: true } named && AdapterContext.FullName(named.OriginalDefinition) == SignalR + ".IHubContext")
            {
                hub = context.IdOf(named.TypeArguments[0]);
            }
            else if (clients.Instance is IInstanceReferenceOperation or null && model.GetEnclosingSymbol(invocation.Syntax.SpanStart)?.ContainingType is { } enclosing
                && AdapterContext.DerivesFrom(enclosing, SignalR + ".Hub"))
            {
                hub = context.IdOf(enclosing);
            }
        }
        return (hub, audience, detail);
    }

    /// <summary>The "Clients" property access under a receiver such as Clients.All or hubContext.Clients.Group(x).</summary>
    private static IPropertyReferenceOperation? ClientsAccess(IOperation receiver)
    {
        var current = AdapterContext.Unwrap(receiver);
        for (var depth = 0; depth < 3 && current is not null; depth++)
        {
            if (current is IPropertyReferenceOperation { Property.Name: "Clients" } clients
                && AdapterContext.FullName(clients.Property.ContainingType).StartsWith(SignalR, StringComparison.Ordinal))
            {
                return clients;
            }
            current = current switch
            {
                IPropertyReferenceOperation property => property.Instance is null ? null : AdapterContext.Unwrap(property.Instance),
                IInvocationOperation call => call.Instance is null ? null : AdapterContext.Unwrap(call.Instance),
                _ => null,
            };
        }
        return null;
    }
}
