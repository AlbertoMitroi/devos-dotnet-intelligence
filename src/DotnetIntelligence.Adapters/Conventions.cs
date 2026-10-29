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

    public static Conventions Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new Conventions();
        }
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        return deserializer.Deserialize<Conventions?>(File.ReadAllText(path)) ?? new Conventions();
    }

    public bool IsEmpty => Messaging.Publish.Count == 0 && Messaging.Subscribe.Count == 0 && Messaging.HandlerInterfaces.Count == 0;
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
