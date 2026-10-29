using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>A configuration key read by code, with the default the code supplies when the key is absent (an options property initializer).</summary>
public sealed record ConfigurationReference(string Key, string? CodeDefault);

/// <summary>One value of a key in one appsettings file of a project that runs the code.</summary>
public sealed record ConfigurationValue(string File, string BlobId, string Value, int Line, int Column, int Length, string LineText);

/// <summary>
/// Configuration values used as destinations (decision D21, contract C3 / D20). Values of appsettings*.json are held in
/// memory only; a value leaves the engine only through <see cref="Destination"/>, when an adapter uses it as a
/// destination (topic, queue, exchange, base address, hub route) and neither the key nor any value looks secret.
/// Keys come from code: IConfiguration indexers, GetValue, GetSection chains, GetConnectionString, and options
/// classes bound to a section (Configure&lt;T&gt;, Bind, BindConfiguration, Get&lt;T&gt;).
/// </summary>
public sealed class ConfigurationValues
{
    private readonly Dictionary<string, List<(string File, string BlobId, Dictionary<string, (string? Value, int Offset, int Length)> Keys, byte[] Bytes)>> _byDirectory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _optionsSections = new(StringComparer.Ordinal);
    private readonly ProjectGraph _projects;

    /// <param name="projects">Values come from the appsettings files of the projects that run the code (the project and its referencers).</param>
    public ConfigurationValues(string root, IReadOnlyList<WorkingCopyFile> files, ProjectGraph projects)
    {
        _projects = projects;
        foreach (var file in files.Where(f => Path.GetFileName(f.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                     && f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, file.Path));
            var keys = new Dictionary<string, (string?, int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value, offset, length) in ConfigurationAdapter.Keys(bytes))
            {
                keys[key] = (value, offset, length);
            }
            var directory = Path.GetDirectoryName(file.Path)?.Replace('\\', '/') ?? string.Empty;
            if (!_byDirectory.TryGetValue(directory, out var list))
            {
                _byDirectory[directory] = list = [];
            }
            list.Add((file.Path, file.BlobId, keys, bytes));
        }
    }

    /// <summary>Options type id to configuration section, collected from every project before the adapters run.</summary>
    public void BindOptions(string typeId, string section) => _optionsSections.TryAdd(typeId, section);

    public string? SectionOf(string typeId) => _optionsSections.TryGetValue(typeId, out var section) ? section : null;

    /// <summary>Values of a key in the appsettings files of the projects that run the given project's code, base file first.</summary>
    public IReadOnlyList<ConfigurationValue> Values(string projectPath, string key)
    {
        var results = new List<ConfigurationValue>();
        foreach (var runner in _projects.Runners(projectPath))
        {
            var directory = Path.GetDirectoryName(runner)?.Replace('\\', '/') ?? string.Empty;
            foreach (var (file, blobId, keys, bytes) in _byDirectory.GetValueOrDefault(directory) ?? [])
            {
                if (keys.TryGetValue(key, out var entry) && entry.Value is not null)
                {
                    var (line, column) = ConfigurationAdapter.Position(bytes, entry.Offset);
                    results.Add(new ConfigurationValue(file, blobId, entry.Value, line, column, entry.Length, ConfigurationAdapter.LineAt(bytes, entry.Offset)));
                }
            }
        }
        return results.OrderBy(v => Path.GetFileName(v.File).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => v.File, StringComparer.Ordinal).ToList();
    }

    // ---- pre-pass: options bindings ----

    /// <summary>Records Configure&lt;T&gt;(section), AddOptions&lt;T&gt;().Bind(section) / .BindConfiguration("X") and section.Bind(instance) / Get&lt;T&gt;().</summary>
    public void VisitBinding(IInvocationOperation invocation, AdapterContext context)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var type = AdapterContext.FullName(method.ContainingType);
        ITypeSymbol? options = null;
        string? section = null;
        if (method.Name == "Configure" && type.StartsWith("Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions", StringComparison.Ordinal)
            && invocation.TargetMethod.TypeArguments.Length == 1)
        {
            options = invocation.TargetMethod.TypeArguments[0];
            section = SectionKey(AdapterContext.Argument(invocation, "config"), context);
        }
        else if (method.Name is "Bind" or "BindConfiguration" && type.StartsWith("Microsoft.Extensions.DependencyInjection.Options", StringComparison.Ordinal)
            && AdapterContext.Receiver(invocation)?.Type is INamedTypeSymbol { TypeArguments.Length: 1 } builder && AdapterContext.FullName(builder) == "Microsoft.Extensions.Options.OptionsBuilder")
        {
            options = builder.TypeArguments[0];
            section = method.Name == "BindConfiguration"
                ? context.StringTemplate(AdapterContext.Argument(invocation, "configSectionPath")) is { Template: { } path, Constant: true } ? path : null
                : SectionKey(AdapterContext.Argument(invocation, "config"), context);
        }
        else if (type == "Microsoft.Extensions.Configuration.ConfigurationBinder" && method.Name is "Bind" or "Get")
        {
            section = SectionKey(AdapterContext.Receiver(invocation), context);
            options = method.Name == "Get" ? invocation.TargetMethod.ReturnType : AdapterContext.Argument(invocation, "instance")?.Type;
        }
        if (options is not null && section is not null && context.IdOf(options) is { } id && options.SpecialType == SpecialType.None)
        {
            BindOptions(id, section);
        }
    }

    // ---- resolving a value expression to a configuration key ----

    /// <summary>The configuration key an expression reads, or null when it is not a configuration read.</summary>
    public ConfigurationReference? Reference(IOperation? operation, AdapterContext context, int depth = 0)
    {
        if (operation is null || depth > 8)
        {
            return null;
        }
        operation = AdapterContext.Unwrap(operation);
        switch (operation)
        {
            case ILocalReferenceOperation local when context.LocalValue(local.Local) is { } value:
                return Reference(value, context, depth + 1);
            case IObjectCreationOperation { Constructor.ContainingType: { } uriType } creation when AdapterContext.FullName(uriType) == "System.Uri" && creation.Arguments.Length > 0:
                return Reference(creation.Arguments[0].Value, context, depth + 1);
            case ICoalesceOperation coalesce:
                return Reference(coalesce.Value, context, depth + 1) is { } inner
                    ? inner with { CodeDefault = inner.CodeDefault ?? (context.StringTemplate(coalesce.WhenNull) is { Template: { } d, Constant: true } ? d : null) }
                    : null;
            case IPropertyReferenceOperation { Property.IsIndexer: true } indexer when IsConfiguration(indexer.Property.ContainingType)
                && indexer.Arguments.Length == 1 && context.StringTemplate(indexer.Arguments[0].Value) is { Template: { } key, Constant: true }
                && SectionKey(indexer.Instance, context) is { } indexerSection:
                return new ConfigurationReference(Join(indexerSection, key), null);
            case IPropertyReferenceOperation { Property.Name: "Value" } sectionValue when IsSection(sectionValue.Property.ContainingType):
                return SectionKey(sectionValue.Instance, context) is { } sectionKey ? new ConfigurationReference(sectionKey, null) : null;
            case IInvocationOperation invocation:
            {
                var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                var type = AdapterContext.FullName(method.ContainingType);
                if (type == "Microsoft.Extensions.Configuration.ConfigurationBinder" && method.Name == "GetValue"
                    && context.StringTemplate(AdapterContext.Argument(invocation, "key")) is { Template: { } key, Constant: true }
                    && SectionKey(AdapterContext.Receiver(invocation), context) is { } valueSection)
                {
                    var fallback = AdapterContext.Argument(invocation, "defaultValue") is { } defaultValue && context.StringTemplate(defaultValue) is { Template: { } t, Constant: true } ? t : null;
                    return new ConfigurationReference(Join(valueSection, key), fallback);
                }
                if (type == "Microsoft.Extensions.Configuration.ConfigurationExtensions" && method.Name == "GetConnectionString"
                    && context.StringTemplate(AdapterContext.Argument(invocation, "name")) is { Template: { } name, Constant: true })
                {
                    return new ConfigurationReference("ConnectionStrings:" + name, null);
                }
                return null;
            }
            case IPropertyReferenceOperation property:
                return OptionsProperty(property, context);
            default:
                return null;
        }
    }

    /// <summary>A property chain rooted in a bound options type: options.Value.Topics.Orders gives "{section}:Topics:Orders".</summary>
    private ConfigurationReference? OptionsProperty(IPropertyReferenceOperation property, AdapterContext context)
    {
        var names = new List<string>();
        var leaf = property.Property;
        IOperation? current = property;
        while (current is IPropertyReferenceOperation reference)
        {
            if (reference.Instance?.Type is { } ownerType && context.IdOf(ownerType) is { } ownerId && SectionOf(ownerId) is { } section)
            {
                names.Add(reference.Property.Name);
                names.Reverse();
                return new ConfigurationReference(section + ":" + string.Join(':', names), Initializer(leaf, context));
            }
            names.Add(reference.Property.Name);
            current = reference.Instance is null ? null : AdapterContext.Unwrap(reference.Instance);
            // IOptions<T>.Value, IOptionsSnapshot<T>.Value and IOptionsMonitor<T>.CurrentValue are the options object itself.
            if (current is IPropertyReferenceOperation { Property.Name: "Value" or "CurrentValue" } wrapper
                && AdapterContext.FullName(wrapper.Property.ContainingType).StartsWith("Microsoft.Extensions.Options.IOptions", StringComparison.Ordinal)
                && wrapper.Type is { } optionsType && context.IdOf(optionsType) is { } optionsId && SectionOf(optionsId) is { } wrapped)
            {
                names.Reverse();
                return new ConfigurationReference(wrapped + ":" + string.Join(':', names), Initializer(leaf, context));
            }
        }
        return null;
    }

    private static string? Initializer(IPropertySymbol property, AdapterContext context)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax { Initializer.Value: { } value }
                && context.Compilation.ContainsSyntaxTree(value.SyntaxTree)
                && context.Compilation.GetSemanticModel(value.SyntaxTree).GetOperation(value) is { } op
                && context.StringTemplate(op) is { Template: { } constant, Constant: true })
            {
                return constant;
            }
        }
        return null;
    }

    /// <summary>"A:B" for configuration.GetSection("A").GetSection("B"); "" for the configuration root; null when unknown.</summary>
    private static string? SectionKey(IOperation? operation, AdapterContext context)
    {
        if (operation is null)
        {
            return null;
        }
        operation = AdapterContext.Unwrap(operation);
        if (operation is ILocalReferenceOperation local && context.LocalValue(local.Local) is { } value)
        {
            operation = value;
        }
        if (operation is IInvocationOperation { TargetMethod.Name: "GetSection" or "GetRequiredSection" } invocation
            && context.StringTemplate(AdapterContext.Argument(invocation, "key")) is { Template: { } key, Constant: true })
        {
            return Join(SectionKey(AdapterContext.Receiver(invocation), context), key);
        }
        return operation.Type is { } type && IsConfiguration(type) && !IsSection(type) ? string.Empty : null;
    }

    private static string Join(string? section, string key) => string.IsNullOrEmpty(section) ? key : section + ":" + key;

    private static bool IsConfiguration(ITypeSymbol type) => AdapterContext.Implements(type, "Microsoft.Extensions.Configuration.IConfiguration");

    private static bool IsSection(ITypeSymbol type) => AdapterContext.Implements(type, "Microsoft.Extensions.Configuration.IConfigurationSection");

    // ---- destinations ----

    /// <summary>
    /// A destination object for an observation: a literal name, a configuration value (D21: stored only when neither key
    /// nor value looks secret), the code default of a configuration key, or an unknown name. Evidence of the code and of
    /// every configuration line used is added to <paramref name="evidence"/>.
    /// </summary>
    public JsonObject Destination(string kind, IOperation? operation, AdapterContext context, List<string?> evidence)
    {
        var (template, constant) = context.StringTemplate(operation);
        if (template is not null && constant)
        {
            return new JsonObject { ["kind"] = kind, ["name"] = template, ["nameSource"] = "literal" };
        }
        if (Reference(operation, context) is { } reference)
        {
            return FromConfiguration(kind, reference, context, evidence);
        }
        var unknown = new JsonObject { ["kind"] = kind, ["name"] = null, ["nameSource"] = "unknown" };
        if (template is not null)
        {
            unknown["ext.dotnet.template"] = template;
        }
        return unknown;
    }

    public JsonObject FromConfiguration(string kind, ConfigurationReference reference, AdapterContext context, List<string?> evidence)
    {
        var values = Values(context.Project.ProjectPath, reference.Key);
        var secret = SecretScrubber.LooksSecret(reference.Key, null) || values.Any(v => SecretScrubber.LooksSecret(reference.Key, v.Value));
        var destination = new JsonObject { ["kind"] = kind, ["configurationKey"] = reference.Key };
        foreach (var value in values)
        {
            evidence.Add(context.Graph.AddEvidence(
                new SourceLocation(value.File, value.BlobId, new SourceSpan(value.Line, value.Column, value.Line, value.Column + value.Length), "repository", null),
                "config", context.ContextId, value.LineText));
        }
        if (secret)
        {
            destination["name"] = null;
            destination["nameSource"] = "configuration";
            destination["secret"] = true;
            return destination;
        }
        if (values.Count > 0)
        {
            destination["name"] = values[0].Value;
            destination["nameSource"] = "configuration";
            var others = values.Skip(1).Where(v => v.Value != values[0].Value).ToList();
            if (others.Count > 0)
            {
                destination["alternatives"] = new JsonArray(others.Select(v => (JsonNode)new JsonObject { ["file"] = v.File, ["name"] = v.Value }).ToArray());
            }
            destination["configurationFiles"] = new JsonArray(values.Select(v => v.File).Distinct(StringComparer.Ordinal).Select(f => (JsonNode)f).ToArray());
            return destination;
        }
        if (reference.CodeDefault is not null)
        {
            destination["name"] = reference.CodeDefault;
            destination["nameSource"] = "literal";
            destination["ext.dotnet.overridableBy"] = reference.Key;
            return destination;
        }
        destination["name"] = null;
        destination["nameSource"] = "configuration";
        destination["reason"] = "The key has no value in the appsettings files of the projects that run this code.";
        return destination;
    }
}
