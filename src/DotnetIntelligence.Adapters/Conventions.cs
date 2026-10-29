using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Caller-supplied conventions (4.3, messaging), read from the configuration file. They name a codebase's own
/// abstractions by symbol; the engine itself knows no repository (rule 1.4.2).
/// </summary>
public sealed class Conventions
{
    public MessagingConventions Messaging { get; set; } = new();

    public WorkflowConventions Workflow { get; set; } = new();

    public RealtimeConventions Realtime { get; set; } = new();

    public static Conventions Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new Conventions();
        }
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        return deserializer.Deserialize<Conventions?>(File.ReadAllText(path)) ?? new Conventions();
    }

    public bool IsEmpty => Messaging.Publish.Count == 0 && Messaging.Subscribe.Count == 0 && Messaging.HandlerInterfaces.Count == 0
        && Workflow.Handlers.Count == 0 && Workflow.Metadata.NodeFields.Count == 0 && Realtime.Send.Count == 0;
}

public sealed class MessagingConventions
{
    /// <summary>"entity" (default: the same type) or "typeName" (the simple type name identifies a message across services).</summary>
    public string ContractIdentity { get; set; } = "entity";

    public List<PublishConvention> Publish { get; set; } = [];

    public List<SubscribeConvention> Subscribe { get; set; } = [];

    public List<HandlerConvention> HandlerInterfaces { get; set; } = [];
}

/// <param name="Method">Containing type full name and method name, for example "My.Bus.IEventBus.PublishAsync".</param>
public sealed class PublishConvention
{
    public string Method { get; set; } = string.Empty;
    public int EventArgument { get; set; }
}

public sealed class SubscribeConvention
{
    public string Method { get; set; } = string.Empty;
    public int EventTypeArgument { get; set; }
    public int HandlerTypeArgument { get; set; } = 1;
}

public sealed class HandlerConvention
{
    /// <summary>Full name of the generic handler interface without arity, for example "My.Bus.IEventHandler".</summary>
    public string Type { get; set; } = string.Empty;
    public int EventTypeArgument { get; set; }
}

/// <summary>
/// Workflow handler conventions: an in-house workflow engine's plugin interfaces (for example IHandleServiceTask)
/// and how a plugin names the BPMN node or flow event it handles. Matched by symbol; the engine knows no library.
/// </summary>
public sealed class WorkflowConventions
{
    public List<WorkflowHandlerConvention> Handlers { get; set; } = [];

    public WorkflowMetadataConvention Metadata { get; set; } = new();
}

public sealed class WorkflowHandlerConvention
{
    /// <summary>Full name of the plugin interface without arity, for example "My.Flow.IHandleServiceTask".</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>Convention role, compared with workflow.node nodeType (serviceTask, userTask, ...) or "event" for flow events.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Where the handled node (or event) name comes from, tried in order: attribute, property, typeArgument, typeName.</summary>
    public NameRule Name { get; set; } = new();

    /// <summary>Optional: where the process id comes from (same rule shape); "*" when absent.</summary>
    public NameRule? Process { get; set; }
}

public sealed class NameRule
{
    /// <summary>Full name of an attribute on the handler type whose constructor argument gives the name.</summary>
    public string? Attribute { get; set; }
    public int AttributeArgument { get; set; }

    /// <summary>A string property of the handler with a constant value (expression body, getter return or initializer).</summary>
    public string? Property { get; set; }

    /// <summary>Index of a type argument of the plugin interface whose simple type name is the name (events, commands).</summary>
    public int? TypeArgument { get; set; }

    /// <summary>Use the handler's simple type name, after removing the first matching suffix.</summary>
    public bool TypeName { get; set; }
    public List<string> RemoveSuffixes { get; set; } = [];
}

public sealed class WorkflowMetadataConvention
{
    /// <summary>String fields copied from *.ProcessMetaData.json objects whose "id" equals a BPMN node id.</summary>
    public List<string> NodeFields { get; set; } = [];
}

/// <summary>A codebase's own real-time helpers (for example a SendToAll wrapper over SignalR), named by symbol.</summary>
public sealed class RealtimeConventions
{
    public List<RealtimeSendConvention> Send { get; set; } = [];
}

public sealed class RealtimeSendConvention
{
    /// <summary>Containing type full name and method name, for example "My.Hubs.IHubUtility.SendToAll".</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Index of the argument that carries the client method name; null when the method name is fixed below.</summary>
    public int? MethodArgument { get; set; }

    /// <summary>Fixed client method name when the helper has no such argument.</summary>
    public string? ClientMethod { get; set; }

    /// <summary>all, users, group or unknown.</summary>
    public string Audience { get; set; } = "unknown";

    /// <summary>Optional hub type full name when the helper always targets one hub.</summary>
    public string? Hub { get; set; }
}
