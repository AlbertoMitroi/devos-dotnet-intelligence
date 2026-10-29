using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// ABP Framework, by resolved symbol (generic: no application names).
/// - Distributed events: IDistributedEventBus.PublishAsync and IDistributedEventHandler&lt;T&gt; give messaging.publish /
///   messaging.subscribe; the contract identity is the event name ([EventName] or the full type name, as ABP routes
///   them); the transport comes from the event bus module the running projects depend on (typeof(AbpEventBus*Module));
///   destinations from RabbitMQ:EventBus:ExchangeName / ClientName (configuration, D21).
/// - Local events: ILocalEventBus.PublishAsync and ILocalEventHandler&lt;T&gt; give inprocess.send / inprocess.handler.
/// - Auto API controllers: ConventionalControllers.Create(assembly, o =&gt; o.RootPath = ...) turns the application
///   services of that assembly into http.endpoint, with ABP's route conventions (candidates: conventions, not code).
/// - Static client proxies: ClientProxyBase&lt;TService&gt; methods calling RequestAsync(nameof(M)) give http.call with the
///   verb and URL from the generated "*generate-proxy.json" and the base URL from RemoteServices (D21).
/// Repository-wide: hosts configure what libraries declare, so most observations are emitted by <see cref="Emit"/>.
/// </summary>
public sealed class AbpAdapter
{
    private static readonly Dictionary<string, string> BusModules = new(StringComparer.Ordinal)
    {
        ["Volo.Abp.EventBus.RabbitMq.AbpEventBusRabbitMqModule"] = "rabbitmq",
        ["Volo.Abp.EventBus.Kafka.AbpEventBusKafkaModule"] = "kafka",
        ["Volo.Abp.EventBus.Azure.AbpEventBusAzureModule"] = "azure-service-bus",
        ["Volo.Abp.EventBus.Rebus.AbpEventBusRebusModule"] = "rebus",
        ["Volo.Abp.EventBus.Dapr.AbpEventBusDaprModule"] = "dapr",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, List<(string Transport, string? Evidence)>> _busModulesByProject = new(StringComparer.Ordinal);
    private readonly List<(AdapterContext Context, string Kind, JsonObject Fields, List<string?> Evidence)> _messages = [];
    private readonly List<(string Assembly, string RootPath, string? Evidence)> _conventional = [];
    private readonly List<(AdapterContext Context, INamedTypeSymbol Service)> _applicationServices = [];
    private readonly List<(string Entity, string? Evidence)> _autoEventSelectors = [];
    private readonly List<(AdapterContext Context, ITypeSymbol Entity, ITypeSymbol Eto, string? Publisher, string? Evidence)> _etoMappings = [];
    private Func<string, IReadOnlyList<string>> _runners = p => [p];
    private Func<string, IReadOnlyList<string>> _dependencies = p => [p];

    public void UseProjects(ProjectGraph graph)
    {
        _runners = graph.Runners;
        _dependencies = graph.Dependencies;
    }

    // ---- types ----

    public void VisitType(INamedTypeSymbol type, AdapterContext context)
    {
        if (type.TypeKind != TypeKind.Class || type.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
        {
            return;
        }
        // [DependsOn(typeof(AbpEventBusRabbitMqModule))] on a module: the event bus transport of every host that loads it.
        foreach (var attribute in type.GetAttributes().Where(a => AdapterContext.FullName(a.AttributeClass) == "Volo.Abp.Modularity.DependsOnAttribute"))
        {
            foreach (var argument in attribute.ConstructorArguments.SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : [a]))
            {
                if (argument.Value is INamedTypeSymbol module && BusModules.TryGetValue(AdapterContext.FullName(module), out var transport))
                {
                    lock (_gate)
                    {
                        if (!_busModulesByProject.TryGetValue(context.Project.ProjectPath, out var list))
                        {
                            _busModulesByProject[context.Project.ProjectPath] = list = [];
                        }
                        list.Add((transport, attribute.ApplicationSyntaxReference is { } s ? context.Evidence(s.GetSyntax()) : context.Evidence(location)));
                    }
                }
            }
        }
        if (type.IsAbstract || context.IdOf(type) is not { } handlerId)
        {
            return;
        }
        foreach (var @interface in type.AllInterfaces.Where(i => i.IsGenericType && i.TypeArguments.Length == 1))
        {
            var definition = AdapterContext.FullName(@interface.OriginalDefinition);
            var message = @interface.TypeArguments[0];
            if (context.IdOf(message) is not { } messageId)
            {
                continue;
            }
            if (definition == "Volo.Abp.EventBus.Distributed.IDistributedEventHandler")
            {
                lock (_gate) _messages.Add((context, "messaging.subscribe", new JsonObject
                {
                    ["contract"] = Contract(message, messageId),
                    ["handler"] = handlerId,
                    ["resolution"] = "exact",
                    ["ext.dotnet.via"] = "handler-interface",
                    ["ext.dotnet.source"] = definition,
                }, [context.Evidence(location)]));
            }
            else if (definition == "Volo.Abp.EventBus.ILocalEventHandler")
            {
                context.Observe("inprocess.handler", new JsonObject
                {
                    ["request"] = messageId,
                    ["handler"] = handlerId,
                    ["mechanism"] = "abp-local-event",
                    ["pattern"] = "notification",
                    ["resolution"] = "exact",
                }, context.Evidence(location));
            }
        }
        if (AdapterContext.Implements(type, "Volo.Abp.Application.Services.IApplicationService") && type.DeclaredAccessibility == Accessibility.Public && !type.IsGenericType)
        {
            lock (_gate) _applicationServices.Add((context, type));
        }
        if (BaseGeneric(type, "Volo.Abp.Http.Client.ClientProxying.ClientProxyBase") is { } proxyBase)
        {
            StaticProxy(type, proxyBase.TypeArguments[0], context);
        }
    }

    // ---- invocations ----

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model, AdapterContext context)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var containing = AdapterContext.FullName(method.ContainingType);
        if (!containing.StartsWith("Volo.Abp.", StringComparison.Ordinal))
        {
            return;
        }
        if (method.Name is "AddDistributedEvent" or "AddLocalEvent" && containing.StartsWith("Volo.Abp.Domain.Entities.", StringComparison.Ordinal)
            && AdapterContext.Argument(invocation, "eventData") is { Type: { SpecialType: SpecialType.None } aggregateEvent }
            && context.IdOf(aggregateEvent) is { } aggregateEventId && context.EnclosingMember(invocation.Syntax, model) is { } raiser)
        {
            // Aggregate roots collect events; ABP publishes them when the unit of work saves the aggregate.
            if (method.Name == "AddDistributedEvent")
            {
                lock (_gate) _messages.Add((context, "messaging.publish", new JsonObject
                {
                    ["contract"] = Contract(aggregateEvent, aggregateEventId),
                    ["publisher"] = raiser,
                    ["resolution"] = "exact",
                    ["ext.dotnet.source"] = containing + ".AddDistributedEvent",
                }, [context.Evidence(invocation.Syntax)]));
            }
            else
            {
                context.Observe("inprocess.send", new JsonObject
                {
                    ["request"] = aggregateEventId,
                    ["sender"] = raiser,
                    ["mechanism"] = "abp-local-event",
                    ["pattern"] = "notification",
                    ["resolution"] = "exact",
                }, context.Evidence(invocation.Syntax));
            }
            return;
        }
        // AbpDistributedEntityEventOptions: AutoEventSelectors.Add<TEntity>() / AddAll() and EtoMappings.Add<TEntity, TEto>().
        if (method.Name is "Add" or "AddAll" && containing.StartsWith("Volo.Abp.Domain.Entities.Events.Distributed.", StringComparison.Ordinal))
        {
            var typeArguments = invocation.TargetMethod.TypeArguments;
            lock (_gate)
            {
                if (containing.Contains("Selector", StringComparison.Ordinal))
                {
                    _autoEventSelectors.Add((method.Name == "AddAll" || typeArguments.Length == 0 ? "*" : context.IdOf(typeArguments[0]) ?? "?", context.Evidence(invocation.Syntax)));
                }
                else if (typeArguments.Length == 2)
                {
                    _etoMappings.Add((context, typeArguments[0], typeArguments[1], context.EnclosingMember(invocation.Syntax, model), context.Evidence(invocation.Syntax)));
                }
            }
            return;
        }
        if (method.Name == "PublishAsync" && invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0) is { } argument
            && AdapterContext.Unwrap(argument.Value).Type is { SpecialType: SpecialType.None } eventType && eventType.TypeKind != TypeKind.TypeParameter
            && !AdapterContext.FullName(eventType).StartsWith("System.", StringComparison.Ordinal)
            && context.IdOf(eventType) is { } eventId && context.EnclosingMember(invocation.Syntax, model) is { } publisher)
        {
            if (AdapterContext.Implements(method.ContainingType, "Volo.Abp.EventBus.Distributed.IDistributedEventBus"))
            {
                lock (_gate) _messages.Add((context, "messaging.publish", new JsonObject
                {
                    ["contract"] = Contract(eventType, eventId),
                    ["publisher"] = publisher,
                    ["resolution"] = "exact",
                    ["ext.dotnet.source"] = containing + ".PublishAsync",
                }, [context.Evidence(invocation.Syntax)]));
            }
            else if (AdapterContext.Implements(method.ContainingType, "Volo.Abp.EventBus.Local.ILocalEventBus"))
            {
                context.Observe("inprocess.send", new JsonObject
                {
                    ["request"] = eventId,
                    ["sender"] = publisher,
                    ["mechanism"] = "abp-local-event",
                    ["pattern"] = "notification",
                    ["resolution"] = "exact",
                }, context.Evidence(invocation.Syntax));
            }
            return;
        }
        // options.ConventionalControllers.Create(typeof(XModule).Assembly, o => o.RootPath = "x")
        if (method.Name == "Create" && containing.EndsWith("ConventionalControllerSettingList", StringComparison.Ordinal)
            && AdapterContext.Argument(invocation, "assembly") is IPropertyReferenceOperation { Property.Name: "Assembly", Instance: ITypeOfOperation typeOf })
        {
            var (rootPath, constant, _) = context.AssignedInLambda(AdapterContext.Argument(invocation, "optionsAction"), "RootPath");
            lock (_gate) _conventional.Add((typeOf.TypeOperand.ContainingAssembly.Name, rootPath is not null && constant ? rootPath : "app", context.Evidence(invocation.Syntax)));
        }
    }

    // ---- static client proxies ----

    private void StaticProxy(INamedTypeSymbol proxy, ITypeSymbol service, AdapterContext context)
    {
        var actions = ProxyActions(context);
        var serviceName = AdapterContext.FullName(service);
        foreach (var method in proxy.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            foreach (var syntax in method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).Where(s => context.Compilation.ContainsSyntaxTree(s.SyntaxTree)))
            {
                var model = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var call in syntax.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                             .Select(n => model.GetOperation(n)).OfType<IInvocationOperation>().Where(i => i.TargetMethod.Name == "RequestAsync"))
                {
                    var (actionName, _) = context.StringTemplate(AdapterContext.Argument(call, "methodName"));
                    var action = actions.FirstOrDefault(a => a.Service == serviceName && a.Name == actionName && a.ParameterCount == method.Parameters.Length);
                    var evidence = new List<string?> { context.Evidence(call.Syntax) };
                    var fields = new JsonObject
                    {
                        ["method"] = action?.HttpMethod ?? "{}",
                        ["urlTemplate"] = action?.Url,
                        ["canonicalRoute"] = action is null ? null : Routes.Canonical(action.Url),
                        ["client"] = context.IdOf(proxy),
                        ["caller"] = context.IdOf(method),
                        ["resolution"] = action is null ? "unresolved" : "exact",
                        ["remoteService"] = action?.RemoteService,
                        ["ext.dotnet.via"] = "abp-static-proxy",
                    };
                    if (action is null)
                    {
                        fields["reason"] = $"No action {serviceName}.{actionName} with {method.Parameters.Length} parameters in the generated proxy description of '{context.Project.ProjectPath}'.";
                    }
                    else
                    {
                        evidence.Add(action.Evidence);
                        var name = action.RemoteService ?? "Default";
                        var baseUrl = context.Configuration!.Values(context.Project.ProjectPath, $"RemoteServices:{name}:BaseUrl").Count > 0 ? name : "Default";
                        var destination = context.Configuration.FromConfiguration("base-address", new ConfigurationReference($"RemoteServices:{baseUrl}:BaseUrl", null), context, evidence);
                        fields["baseAddress"] = destination["name"]?.DeepClone();
                        fields["baseAddressSource"] = destination.DeepClone();
                    }
                    context.Observe("http.call", fields, [.. evidence]);
                }
            }
        }
    }

    private sealed record ProxyAction(string Service, string Name, int ParameterCount, string HttpMethod, string Url, string? RemoteService, string Evidence);

    private readonly Dictionary<string, List<ProxyAction>> _proxyActions = new(StringComparer.Ordinal);

    /// <summary>Actions of the "*generate-proxy.json" files under ClientProxies/ in the project's directory.</summary>
    private List<ProxyAction> ProxyActions(AdapterContext context)
    {
        lock (_gate)
        {
            if (_proxyActions.TryGetValue(context.Project.ProjectPath, out var cached))
            {
                return cached;
            }
            var actions = new List<ProxyAction>();
            var directory = Path.GetDirectoryName(context.Project.ProjectPath)!.Replace('\\', '/');
            var prefix = directory.Length == 0 ? string.Empty : directory + "/";
            foreach (var file in context.RepositoryFiles.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.EndsWith("generate-proxy.json", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = File.ReadAllBytes(Path.Combine(context.Files.Root, file.Path));
                var text = Encoding.UTF8.GetString(bytes);
                try
                {
                    using var json = JsonDocument.Parse(bytes);
                    foreach (var module in json.RootElement.GetProperty("modules").EnumerateObject())
                    {
                        var remoteService = module.Value.TryGetProperty("remoteServiceName", out var r) ? r.GetString() : null;
                        foreach (var controller in module.Value.GetProperty("controllers").EnumerateObject())
                        {
                            foreach (var action in controller.Value.GetProperty("actions").EnumerateObject())
                            {
                                var a = action.Value;
                                var url = a.GetProperty("url").GetString() ?? string.Empty;
                                var service = a.TryGetProperty("implementFrom", out var from) ? from.GetString() ?? string.Empty : string.Empty;
                                var count = a.TryGetProperty("parametersOnMethod", out var parameters) ? parameters.GetArrayLength() : 0;
                                // Evidence: the line of the action's key in the JSON file.
                                var offset = text.IndexOf("\"" + action.Name + "\"", StringComparison.Ordinal);
                                var byteOffset = offset < 0 ? 0 : Encoding.UTF8.GetByteCount(text.AsSpan(0, offset));
                                var (line, column) = ConfigurationAdapter.Position(bytes, byteOffset);
                                var evidence = context.Graph.AddEvidence(
                                    new Analysis.Graph.SourceLocation(file.Path, file.BlobId, new Analysis.Graph.SourceSpan(line, column, line, column + action.Name.Length + 2), "repository", null),
                                    "config", context.ContextId, ConfigurationAdapter.LineAt(bytes, byteOffset));
                                actions.Add(new ProxyAction(service, a.GetProperty("name").GetString() ?? action.Name, count,
                                    a.GetProperty("httpMethod").GetString() ?? "{}", "/" + url.TrimStart('/'), remoteService, evidence));
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // An unreadable description leaves the proxy calls unresolved, each with its reason.
                }
            }
            _proxyActions[context.Project.ProjectPath] = actions;
            return actions;
        }
    }

    // ---- emission ----

    public void Emit()
    {
        // Auto entity events: ABP publishes EntityCreated/Updated/DeletedEto<TEto> for every selected entity with an ETO mapping.
        foreach (var (context, entity, eto, publisher, evidence) in _etoMappings)
        {
            var entityId = context.IdOf(entity);
            var selectors = _autoEventSelectors.Where(s => s.Entity == "*" || s.Entity == entityId).ToList();
            if (selectors.Count == 0 || publisher is null)
            {
                continue;
            }
            foreach (var name in new[] { "EntityCreatedEto", "EntityUpdatedEto", "EntityDeletedEto" })
            {
                if (context.Compilation.GetTypeByMetadataName($"Volo.Abp.Domain.Entities.Events.Distributed.{name}`1") is { } definition
                    && definition.Construct(eto) is var constructed && context.IdOf(constructed) is { } constructedId)
                {
                    _messages.Add((context, "messaging.publish", new JsonObject
                    {
                        ["contract"] = Contract(constructed, constructedId),
                        ["publisher"] = publisher,
                        ["resolution"] = "exact",
                        ["ext.dotnet.source"] = "Volo.Abp.Domain.Entities.Events.Distributed.AbpDistributedEntityEventOptions",
                        ["ext.dotnet.via"] = "abp-auto-entity-event",
                    }, [evidence, .. selectors.Select(s => s.Evidence)]));
                }
            }
        }

        foreach (var (context, kind, fields, evidence) in _messages)
        {
            var copy = (JsonObject)fields.DeepClone();
            var all = new List<string?>(evidence);
            var transports = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var runner in _runners(context.Project.ProjectPath))
            {
                foreach (var dependency in _dependencies(runner))
                {
                    foreach (var (transport, transportEvidence) in _busModulesByProject.GetValueOrDefault(dependency) ?? [])
                    {
                        transports.Add(transport);
                        all.Add(transportEvidence);
                    }
                }
            }
            copy["transport"] = transports.Count switch { 0 => "unknown", 1 => transports.Min, _ => null };
            if (transports.Count > 1)
            {
                copy["transports"] = new JsonArray(transports.Select(t => (JsonNode)t).ToArray());
            }
            if (transports.Contains("rabbitmq"))
            {
                var exchange = context.Configuration!.FromConfiguration("exchange", new ConfigurationReference("RabbitMQ:EventBus:ExchangeName", null), context, all);
                exchange["ext.dotnet.routingKey"] = copy["contract"]!["identity"]!.GetValue<string>()["name:".Length..];
                if (kind == "messaging.subscribe")
                {
                    var queue = context.Configuration.FromConfiguration("queue", new ConfigurationReference("RabbitMQ:EventBus:ClientName", null), context, all);
                    queue["ext.dotnet.boundTo"] = exchange;
                    copy["destination"] = queue;
                }
                else
                {
                    copy["destination"] = exchange;
                }
            }
            else
            {
                copy["destination"] = null;
            }
            context.Observe(kind, copy, [.. all]);
        }

        foreach (var (context, service) in _applicationServices)
        {
            var registrations = _conventional.Where(c => c.Assembly == service.ContainingAssembly.Name).ToList();
            if (registrations.Count == 0 || IsRemoteServiceDisabled(service))
            {
                continue;
            }
            foreach (var (_, rootPath, registrationEvidence) in registrations)
            {
                foreach (var method in PublicActions(service))
                {
                    var (verb, route) = AutoApiRoute(rootPath, service, method);
                    context.Observe("http.endpoint", new JsonObject
                    {
                        ["method"] = verb,
                        ["routeTemplate"] = route,
                        ["canonicalRoute"] = Routes.Canonical(route),
                        ["handler"] = context.IdOf(method),
                        ["resolution"] = "candidate",
                        ["ext.dotnet.style"] = "abp-auto-api",
                    }, method.Locations.FirstOrDefault(l => l.IsInSource) is { } location ? context.Evidence(location) : null, registrationEvidence);
                }
            }
        }
    }

    private static IEnumerable<IMethodSymbol> PublicActions(INamedTypeSymbol service)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var type = service; type is not null && !AdapterContext.FullName(type).StartsWith("Volo.Abp.", StringComparison.Ordinal); type = type.BaseType)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic))
            {
                if (seen.Add(method.Name + "/" + method.Parameters.Length) && !IsRemoteServiceDisabled(method))
                {
                    yield return method;
                }
            }
        }
    }

    private static bool IsRemoteServiceDisabled(ISymbol symbol) =>
        symbol.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == "Volo.Abp.RemoteServiceAttribute") is { } attribute
        && (attribute.ConstructorArguments.FirstOrDefault().Value is false
            || attribute.NamedArguments.Any(n => n.Key == "IsEnabled" && n.Value.Value is false));

    private static readonly (string Prefix, string Verb)[] VerbPrefixes =
    [
        ("GetList", "GET"), ("GetAll", "GET"), ("Get", "GET"),
        ("Put", "PUT"), ("Update", "PUT"),
        ("Delete", "DELETE"), ("Remove", "DELETE"),
        ("Post", "POST"), ("Create", "POST"), ("Add", "POST"), ("Insert", "POST"),
        ("Patch", "PATCH"),
    ];

    /// <summary>ABP's conventional route: api/{rootPath}/{controller}/{id?}/{action?}, verb from the method name prefix.</summary>
    internal static (string Verb, string Route) AutoApiRoute(string rootPath, INamedTypeSymbol service, IMethodSymbol method)
    {
        var controller = service.Name;
        foreach (var suffix in new[] { "ApplicationService", "AppService", "Service" })
        {
            if (controller.EndsWith(suffix, StringComparison.Ordinal) && controller.Length > suffix.Length)
            {
                controller = controller[..^suffix.Length];
                break;
            }
        }
        var name = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^"Async".Length] : method.Name;
        var verb = "POST";
        foreach (var (prefix, candidate) in VerbPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                verb = candidate;
                name = name[prefix.Length..];
                break;
            }
        }
        var route = new StringBuilder("api/").Append(rootPath).Append('/').Append(Kebab(controller));
        if (method.Parameters.Any(p => p.Name.Equals("id", StringComparison.OrdinalIgnoreCase) && (p.Type.SpecialType != SpecialType.None || p.Type.Name is "Guid")))
        {
            route.Append("/{id}");
        }
        if (name.Length > 0)
        {
            route.Append('/').Append(Kebab(name));
        }
        return (verb, route.ToString());
    }

    internal static string Kebab(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    /// <summary>ABP routes distributed events by event name: [EventName], [GenericEventName] around the argument's name, or the full type name.</summary>
    private static JsonObject Contract(ITypeSymbol type, string id)
    {
        var (eventName, source) = EventName(type);
        return new JsonObject
        {
            ["entity"] = id,
            ["name"] = type.Name,
            ["fullName"] = AdapterContext.FullName(type),
            ["identity"] = "name:" + eventName,
            ["ext.dotnet.eventNameSource"] = source,
        };
    }

    internal static (string Name, string Source) EventName(ITypeSymbol type)
    {
        if (type.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == "Volo.Abp.EventBus.EventNameAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value is string name)
        {
            return (name, "attribute");
        }
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic
            && generic.OriginalDefinition.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == "Volo.Abp.EventBus.GenericEventNameAttribute") is { } genericName)
        {
            string? Named(string key) => genericName.NamedArguments.FirstOrDefault(n => n.Key == key).Value.Value as string;
            return (Named("Prefix") + EventName(generic.TypeArguments[0]).Name + Named("Postfix"), "generic-attribute");
        }
        return (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)), "type");
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
