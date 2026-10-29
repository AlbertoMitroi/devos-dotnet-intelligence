using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// MassTransit 8 (4.3, messaging), by resolved symbol. Publish and Send on MassTransit endpoints, IConsumer&lt;T&gt;
/// implementations and AddConsumer registrations become messaging.publish / messaging.subscribe (exact). Bus transports
/// (UsingRabbitMq, UsingAzureServiceBus, UsingInMemory) apply to the projects whose code they run; several are listed in
/// "transports" (environment-specific). Destinations: queues from ReceiveEndpoint, endpoint names and Send addresses,
/// topics of the Kafka rider (AddProducer, TopicEndpoint, ITopicProducer), names from literals, configuration (D21) or
/// MassTransit's conventions (kebab-case endpoint names, message entity names).
/// Repository-wide: registrations in a host apply to consumers and producers declared in libraries, so observations
/// are emitted by <see cref="Emit"/> after every project was visited.
/// </summary>
public sealed class MassTransitAdapter
{
    private const string Ns = "MassTransit";
    private static readonly Dictionary<string, string> Transports = new(StringComparer.Ordinal)
    {
        ["UsingRabbitMq"] = "rabbitmq",
        ["UsingAzureServiceBus"] = "azure-service-bus",
        ["UsingInMemory"] = "in-memory",
        ["UsingAmazonSqs"] = "amazon-sqs",
        ["UsingActiveMq"] = "activemq",
        ["UsingGrpc"] = "grpc",
        ["UsingSqlServer"] = "sql",
        ["UsingPostgres"] = "sql",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, SortedSet<string>> _transportsByProject = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string?>> _transportEvidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(JsonObject Destination, string Transport, List<string?> Evidence)>> _consumerEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (JsonObject Destination, List<string?> Evidence)> _producerTopics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (JsonObject Destination, List<string?> Evidence)> _sendConventions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _configureEndpoints = new(StringComparer.Ordinal);
    private readonly List<Pending> _pending = [];
    private Func<string, IReadOnlyList<string>> _runners = p => [p];

    private sealed record Pending(AdapterContext Context, string Kind, JsonObject Fields, List<string?> Evidence,
        string? ConsumerId, string? ContractId, string? Transport, bool UsesBus, JsonObject? Destination);

    public void UseRunners(Func<string, IReadOnlyList<string>> runners) => _runners = runners;

    // ---- types ----

    public void VisitType(INamedTypeSymbol type, AdapterContext context)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
        {
            return;
        }
        // Consumer definitions name their endpoint: EndpointName = "x" in the constructor.
        if (BaseGeneric(type, "MassTransit.ConsumerDefinition") is { } definition && context.IdOf(definition.TypeArguments[0]) is { } definedConsumer)
        {
            foreach (var constructor in type.InstanceConstructors)
            {
                foreach (var syntax in constructor.DeclaringSyntaxReferences.Select(r => r.GetSyntax()))
                {
                    var model = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                    foreach (var assignment in syntax.DescendantNodes().Select(n => model.GetOperation(n)).OfType<ISimpleAssignmentOperation>())
                    {
                        if (AdapterContext.Unwrap(assignment.Target) is IPropertyReferenceOperation { Property.Name: "EndpointName" })
                        {
                            var evidence = new List<string?> { context.Evidence(assignment.Syntax) };
                            var destination = context.Configuration!.Destination("queue", assignment.Value, context, evidence);
                            AddConsumerEndpoint(definedConsumer, destination, "bus", evidence);
                        }
                    }
                }
            }
        }
        if (context.IdOf(type) is not { } handlerId)
        {
            return;
        }
        foreach (var @interface in type.AllInterfaces.Where(i => i.IsGenericType && AdapterContext.FullName(i.OriginalDefinition) == "MassTransit.IConsumer"))
        {
            var message = @interface.TypeArguments[0];
            if (context.IdOf(message) is not { } contractId)
            {
                continue;
            }
            Add(new Pending(context, "messaging.subscribe", new JsonObject
            {
                ["contract"] = Contract(message, contractId, context),
                ["handler"] = handlerId,
                ["resolution"] = "exact",
                ["ext.dotnet.via"] = "handler-interface",
                ["ext.dotnet.source"] = "MassTransit.IConsumer",
            }, [context.Evidence(location)], handlerId, contractId, null, true, null));
        }
    }

    // ---- invocations ----

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model, AdapterContext context)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var containing = AdapterContext.FullName(method.ContainingType);
        if (!containing.StartsWith(Ns + ".", StringComparison.Ordinal))
        {
            return;
        }
        var name = method.Name;
        if (Transports.TryGetValue(name, out var transport))
        {
            lock (_gate)
            {
                if (!_transportsByProject.TryGetValue(context.Project.ProjectPath, out var set))
                {
                    _transportsByProject[context.Project.ProjectPath] = set = new SortedSet<string>(StringComparer.Ordinal);
                    _transportEvidence[context.Project.ProjectPath] = [];
                }
                set.Add(transport);
                _transportEvidence[context.Project.ProjectPath].Add(context.Evidence(invocation.Syntax));
            }
            return;
        }
        switch (name)
        {
            case "ConfigureEndpoints":
                lock (_gate) _configureEndpoints.Add(context.Project.ProjectPath);
                return;
            case "AddConsumer" when invocation.TargetMethod.TypeArguments.Length >= 1:
                Registration(invocation, context);
                return;
            case "ReceiveEndpoint":
                Endpoint(invocation, context, "queue", "bus", AdapterContext.Argument(invocation, "queueName"));
                return;
            case "TopicEndpoint":
                Endpoint(invocation, context, "topic", "kafka", AdapterContext.Argument(invocation, "topicName"));
                return;
            case "AddProducer" when invocation.TargetMethod.TypeArguments.Length >= 1:
            {
                var message = invocation.TargetMethod.TypeArguments[^1];
                if (context.IdOf(message) is { } messageId)
                {
                    var evidence = new List<string?> { context.Evidence(invocation.Syntax) };
                    var destination = context.Configuration!.Destination("topic", AdapterContext.Argument(invocation, "topicName"), context, evidence);
                    lock (_gate) _producerTopics.TryAdd(messageId, (destination, evidence));
                }
                return;
            }
            case "Map" when containing == "MassTransit.EndpointConvention" && invocation.TargetMethod.TypeArguments.Length == 1:
            {
                if (context.IdOf(invocation.TargetMethod.TypeArguments[0]) is { } messageId)
                {
                    var evidence = new List<string?> { context.Evidence(invocation.Syntax) };
                    var destination = Address(AdapterContext.Argument(invocation, "destinationAddress"), context, evidence);
                    lock (_gate) _sendConventions.TryAdd(messageId, (destination, evidence));
                }
                return;
            }
            case "Publish" or "Send" when IsEndpointCall(method):
                Produce(invocation, model, context, name == "Publish" ? "publish" : "send", null);
                return;
            case "Produce" when AdapterContext.FullName(method.ContainingType) == "MassTransit.ITopicProducer":
                Produce(invocation, model, context, "produce", AdapterContext.Receiver(invocation)?.Type as INamedTypeSymbol);
                return;
        }
    }

    private static bool IsEndpointCall(IMethodSymbol method) =>
        AdapterContext.Implements(method.ContainingType, "MassTransit.IPublishEndpoint")
        || AdapterContext.Implements(method.ContainingType, "MassTransit.ISendEndpoint")
        || method.ContainingType.IsStatic && method.ContainingType.Name.EndsWith("Extensions", StringComparison.Ordinal);

    private void Registration(IInvocationOperation invocation, AdapterContext context)
    {
        var consumer = invocation.TargetMethod.TypeArguments[0];
        if (!AdapterContext.Implements(consumer, "MassTransit.IConsumer") || context.IdOf(consumer) is not { } consumerId)
        {
            return;
        }
        var evidence = context.Evidence(invocation.Syntax);
        foreach (var @interface in consumer.AllInterfaces.Where(i => i.IsGenericType && AdapterContext.FullName(i.OriginalDefinition) == "MassTransit.IConsumer"))
        {
            if (context.IdOf(@interface.TypeArguments[0]) is { } contractId)
            {
                Add(new Pending(context, "messaging.subscribe", new JsonObject
                {
                    ["contract"] = Contract(@interface.TypeArguments[0], contractId, context),
                    ["handler"] = consumerId,
                    ["resolution"] = "exact",
                    ["ext.dotnet.via"] = "subscription",
                    ["ext.dotnet.source"] = AdapterContext.FullName(invocation.TargetMethod.ContainingType) + ".AddConsumer",
                }, [evidence], consumerId, contractId, null, true, null));
            }
        }
        // AddConsumer<T>().Endpoint(e => e.Name = "x")
        if (invocation.Parent is IInvocationOperation { TargetMethod.Name: "Endpoint" } endpoint && endpoint.Arguments.Length > 0)
        {
            var configure = endpoint.Arguments.Select(a => AdapterContext.Unwrap(a.Value)).FirstOrDefault(a => a is IDelegateCreationOperation or IAnonymousFunctionOperation);
            var (_, _, node, value) = context.AssignedValueInLambda(configure, "Name");
            if (value is not null)
            {
                var endpointEvidence = new List<string?> { context.Evidence(node!) };
                AddConsumerEndpoint(consumerId, context.Configuration!.Destination("queue", value, context, endpointEvidence), "bus", endpointEvidence);
            }
        }
    }

    /// <summary>ReceiveEndpoint("queue", e =&gt; e.ConfigureConsumer&lt;T&gt;(ctx)) and TopicEndpoint&lt;TKey, TValue&gt;("topic", "group", e =&gt; ...).</summary>
    private void Endpoint(IInvocationOperation invocation, AdapterContext context, string kind, string transport, IOperation? nameArgument)
    {
        var configure = invocation.Arguments.Select(a => AdapterContext.Unwrap(a.Value)).LastOrDefault(a => a is IDelegateCreationOperation or IAnonymousFunctionOperation);
        if (configure is IDelegateCreationOperation creation)
        {
            configure = creation.Target;
        }
        if (configure is not IAnonymousFunctionOperation lambda)
        {
            return;
        }
        var evidence = new List<string?> { context.Evidence(invocation.Syntax) };
        var destination = context.Configuration!.Destination(kind, nameArgument, context, evidence);
        if (kind == "topic" && AdapterContext.Argument(invocation, "groupId") is { } group)
        {
            var groupEvidence = new List<string?>();
            destination["ext.dotnet.consumerGroup"] = context.Configuration.Destination("group", group, context, groupEvidence)["name"]?.DeepClone();
            evidence.AddRange(groupEvidence);
        }
        foreach (var configured in lambda.Body.Descendants().OfType<IInvocationOperation>()
                     .Where(i => i.TargetMethod.Name is "ConfigureConsumer" or "Consumer" && i.TargetMethod.TypeArguments.Length >= 1))
        {
            if (context.IdOf(configured.TargetMethod.TypeArguments[0]) is { } consumerId)
            {
                AddConsumerEndpoint(consumerId, destination, transport, [.. evidence, context.Evidence(configured.Syntax)]);
            }
        }
    }

    private void Produce(IInvocationOperation invocation, SemanticModel model, AdapterContext context, string operation, INamedTypeSymbol? producer)
    {
        ITypeSymbol? message = producer is { TypeArguments.Length: > 0 } ? producer.TypeArguments[^1]
            : invocation.TargetMethod.TypeArguments.Length == 1 && invocation.TargetMethod.TypeArguments[0].SpecialType != SpecialType.System_Object ? invocation.TargetMethod.TypeArguments[0]
            : AdapterContext.Argument(invocation, "messageType") is ITypeOfOperation typeOf ? typeOf.TypeOperand
            : AdapterContext.Argument(invocation, "message") is { Type: { SpecialType: not SpecialType.System_Object } type } ? type
            : null;
        if (message is null || context.IdOf(message) is not { } contractId || context.EnclosingMember(invocation.Syntax, model) is not { } publisher)
        {
            return;
        }
        var evidence = new List<string?> { context.Evidence(invocation.Syntax) };
        JsonObject? destination = null;
        if (operation == "send" && AdapterContext.Receiver(invocation) is { } receiver && SendEndpointAddress(receiver, context) is { } address)
        {
            destination = Address(address, context, evidence);
        }
        Add(new Pending(context, "messaging.publish", new JsonObject
        {
            ["contract"] = Contract(message, contractId, context),
            ["publisher"] = publisher,
            ["resolution"] = "exact",
            ["ext.dotnet.operation"] = operation,
            ["ext.dotnet.source"] = AdapterContext.FullName(invocation.TargetMethod.ContainingType) + "." + invocation.TargetMethod.Name,
        }, evidence, null, contractId, operation == "produce" ? "kafka" : null, operation != "produce", destination));
    }

    /// <summary>The address of "await provider.GetSendEndpoint(uri)" behind a send endpoint value.</summary>
    private static IOperation? SendEndpointAddress(IOperation receiver, AdapterContext context)
    {
        receiver = AdapterContext.Unwrap(receiver);
        if (receiver is ILocalReferenceOperation local && context.LocalValue(local.Local) is { } value)
        {
            receiver = value;
        }
        if (receiver is IAwaitOperation await)
        {
            receiver = AdapterContext.Unwrap(await.Operation);
        }
        return receiver is IInvocationOperation { TargetMethod.Name: "GetSendEndpoint" } get ? AdapterContext.Argument(get, "address") : null;
    }

    /// <summary>"queue:name", "exchange:name", "topic:name" or a transport URI (last path segment is the queue).</summary>
    private static JsonObject Address(IOperation? operation, AdapterContext context, List<string?> evidence)
    {
        var destination = context.Configuration!.Destination("queue", operation, context, evidence);
        if ((string?)destination["name"] is { } address)
        {
            var colon = address.IndexOf(':');
            var scheme = colon > 0 ? address[..colon] : string.Empty;
            if (scheme is "queue" or "exchange" or "topic")
            {
                destination["kind"] = scheme;
                destination["name"] = address[(colon + 1)..];
            }
            else if (address.Contains("://", StringComparison.Ordinal))
            {
                destination["name"] = address.TrimEnd('/').Split('/')[^1].Split('?')[0];
            }
        }
        return destination;
    }

    private void AddConsumerEndpoint(string consumerId, JsonObject destination, string transport, List<string?> evidence)
    {
        lock (_gate)
        {
            if (!_consumerEndpoints.TryGetValue(consumerId, out var list))
            {
                _consumerEndpoints[consumerId] = list = [];
            }
            list.Add((destination, transport, evidence));
        }
    }

    private void Add(Pending pending)
    {
        lock (_gate) _pending.Add(pending);
    }

    // ---- emission ----

    public void Emit()
    {
        foreach (var pending in _pending)
        {
            var fields = (JsonObject)pending.Fields.DeepClone();
            var evidence = new List<string?>(pending.Evidence);
            var busTransports = pending.UsesBus ? BusTransports(pending.Context.Project.ProjectPath, evidence) : [];
            if (pending.Kind == "messaging.subscribe")
            {
                var endpoints = _consumerEndpoints.GetValueOrDefault(pending.ConsumerId!) ?? [];
                if (endpoints.Count > 0)
                {
                    // One observation per endpoint the consumer is configured on (a consumer can listen on a queue and a topic).
                    foreach (var (destination, transport, endpointEvidence) in endpoints)
                    {
                        var copy = (JsonObject)fields.DeepClone();
                        SetTransport(copy, transport == "kafka" ? ["kafka"] : busTransports);
                        copy["destination"] = destination.DeepClone();
                        pending.Context.Observe(pending.Kind, copy, [.. evidence, .. endpointEvidence]);
                    }
                    continue;
                }
                SetTransport(fields, busTransports);
                fields["destination"] = _configureEndpoints.Overlaps(_runners(pending.Context.Project.ProjectPath))
                    ? new JsonObject { ["kind"] = "queue", ["name"] = KebabEndpointName(pending.ConsumerId!), ["nameSource"] = "convention" }
                    : null;
                pending.Context.Observe(pending.Kind, fields, [.. evidence]);
                continue;
            }

            JsonObject? publishDestination = pending.Destination;
            if (pending.Transport == "kafka")
            {
                SetTransport(fields, ["kafka"]);
                if (_producerTopics.TryGetValue(pending.ContractId!, out var topic))
                {
                    publishDestination = topic.Destination;
                    evidence.AddRange(topic.Evidence);
                }
                publishDestination ??= new JsonObject { ["kind"] = "topic", ["name"] = null, ["nameSource"] = "unknown" };
            }
            else
            {
                SetTransport(fields, busTransports);
                if (publishDestination is null && (string?)fields["ext.dotnet.operation"] == "send" && _sendConventions.TryGetValue(pending.ContractId!, out var mapped))
                {
                    publishDestination = mapped.Destination;
                    evidence.AddRange(mapped.Evidence);
                }
                if (publishDestination is null && (string?)fields["ext.dotnet.operation"] == "publish")
                {
                    publishDestination = new JsonObject
                    {
                        ["kind"] = busTransports.Count == 1 && busTransports[0] == "rabbitmq" ? "exchange" : "topic",
                        ["name"] = (string?)fields["contract"]!["ext.dotnet.entityName"],
                        ["nameSource"] = (string?)fields["contract"]!["ext.dotnet.entityNameSource"],
                    };
                }
            }
            fields["destination"] = publishDestination?.DeepClone();
            pending.Context.Observe(pending.Kind, fields, [.. evidence]);
        }
    }

    private List<string> BusTransports(string projectPath, List<string?> evidence)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var runner in _runners(projectPath))
        {
            if (_transportsByProject.TryGetValue(runner, out var transports))
            {
                set.UnionWith(transports);
                evidence.AddRange(_transportEvidence[runner]);
            }
        }
        return [.. set];
    }

    /// <summary>One transport: "transport"; several (chosen per environment by configuration): "transports"; none found: "unknown".</summary>
    private static void SetTransport(JsonObject fields, IReadOnlyList<string> transports)
    {
        fields["transport"] = transports.Count switch { 0 => "unknown", 1 => transports[0], _ => null };
        if (transports.Count > 1)
        {
            fields["transports"] = new JsonArray(transports.Select(t => (JsonNode)t).ToArray());
        }
    }

    private static JsonObject Contract(ITypeSymbol type, string id, AdapterContext context)
    {
        var entityName = type.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == "MassTransit.EntityNameAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
        return new JsonObject
        {
            ["entity"] = id,
            ["name"] = type.Name,
            ["fullName"] = AdapterContext.FullName(type),
            ["identity"] = context.Conventions.Messaging.ContractIdentity == "typeName" ? "name:" + type.Name : id,
            // MassTransit names the publish exchange (RabbitMQ) or topic (Azure Service Bus) after the message type: "Namespace:TypeName".
            ["ext.dotnet.entityName"] = entityName ?? (type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + ":" + type.Name : type.Name),
            ["ext.dotnet.entityNameSource"] = entityName is null ? "convention" : "literal",
        };
    }

    /// <summary>MassTransit's KebabCaseEndpointNameFormatter for a consumer: "SubmitOrderConsumer" gives "submit-order".</summary>
    internal static string KebabEndpointName(string consumerId)
    {
        var name = consumerId[(consumerId.LastIndexOf('.') + 1)..];
        var tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }
        if (name.EndsWith("Consumer", StringComparison.Ordinal) && name.Length > "Consumer".Length)
        {
            name = name[..^"Consumer".Length];
        }
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])))
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    private static INamedTypeSymbol? BaseGeneric(INamedTypeSymbol type, string definition)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && AdapterContext.FullName(current.OriginalDefinition) == definition)
            {
                return current;
            }
        }
        return null;
    }
}
