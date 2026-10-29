using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Data (4.3): DbContext types and their DbSet&lt;T&gt; properties as data.maps_to (no read or write claims), and EF Core
/// tables as data.table (contract v1.1): ToTable(name, schema) on an EntityTypeBuilder&lt;T&gt; (OnModelCreating or
/// IEntityTypeConfiguration&lt;T&gt;), else [Table] on the entity, else EF's convention (the DbSet property name).
/// HasDefaultSchema gives the schema of a context's convention tables.
/// </summary>
public sealed class DataAdapter(AdapterContext context)
{
    private readonly Dictionary<string, List<(string? Table, string? Schema, string Resolution, IInvocationOperation Call)>> _fluent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _defaultSchema = new(StringComparer.Ordinal);

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (!AdapterContext.FullName(method.ContainingType).StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
        {
            return;
        }
        if (method.Name == "HasDefaultSchema" && model.GetEnclosingSymbol(invocation.Syntax.SpanStart)?.ContainingType is { } owner && context.IdOf(owner) is { } ownerId)
        {
            var (schema, constant) = context.StringTemplate(AdapterContext.Argument(invocation, "schema"));
            _defaultSchema[ownerId] = constant ? schema : null;
            return;
        }
        if (method.Name != "ToTable" || EntityOf(AdapterContext.Receiver(invocation)?.Type) is not { } entity || context.IdOf(entity) is not { } entityId)
        {
            return;
        }
        var (table, tableConstant) = context.StringTemplate(AdapterContext.Argument(invocation, "name"));
        var (schemaName, schemaConstant) = context.StringTemplate(AdapterContext.Argument(invocation, "schema"));
        var hasSchema = AdapterContext.Argument(invocation, "schema") is not null;
        var resolution = table is null ? "unresolved" : tableConstant && (!hasSchema || schemaConstant) ? "exact" : "candidate";
        if (!_fluent.TryGetValue(entityId, out var list))
        {
            _fluent[entityId] = list = [];
        }
        list.Add((table, schemaName, resolution, invocation));
    }

    public void VisitType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || !AdapterContext.DerivesFrom(type.BaseType, "Microsoft.EntityFrameworkCore.DbContext") || context.IdOf(type) is not { } contextId)
        {
            return;
        }
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is INamedTypeSymbol { IsGenericType: true } set && AdapterContext.FullName(set) == "Microsoft.EntityFrameworkCore.DbSet"
                && context.IdOf(set.TypeArguments[0]) is { } entityId && property.Locations.FirstOrDefault(l => l.IsInSource) is { } location)
            {
                var evidence = context.Evidence(location);
                context.Observe("data.maps_to", new JsonObject
                {
                    ["dbContext"] = contextId,
                    ["entity"] = entityId,
                    ["property"] = context.IdOf(property),
                }, evidence);
                if (_fluent.ContainsKey(entityId))
                {
                    continue;
                }
                var attribute = set.TypeArguments[0].GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == "System.ComponentModel.DataAnnotations.Schema.TableAttribute");
                var attributeTable = attribute?.ConstructorArguments.FirstOrDefault().Value as string;
                var attributeSchema = attribute?.NamedArguments.FirstOrDefault(n => n.Key == "Schema").Value.Value as string;
                context.Observe("data.table", new JsonObject
                {
                    ["entity"] = entityId,
                    ["table"] = attributeTable ?? property.Name,
                    ["schema"] = attributeSchema ?? _defaultSchema.GetValueOrDefault(contextId),
                    ["store"] = "efcore",
                    ["dbContext"] = contextId,
                    ["tableSource"] = attributeTable is null ? "convention" : "attribute",
                    ["resolution"] = "exact",
                }, evidence, attribute?.ApplicationSyntaxReference is { } a ? context.Evidence(a.GetSyntax()) : null);
            }
        }
    }

    /// <summary>Fluent tables, emitted after all invocations of the project were seen.</summary>
    public void Complete(SemanticModelProvider models)
    {
        foreach (var (entityId, calls) in _fluent)
        {
            foreach (var (table, schema, resolution, call) in calls)
            {
                var owner = models(call.Syntax.SyntaxTree).GetEnclosingSymbol(call.Syntax.SpanStart)?.ContainingType;
                var dbContext = owner is not null && AdapterContext.DerivesFrom(owner, "Microsoft.EntityFrameworkCore.DbContext") ? context.IdOf(owner) : null;
                context.Observe("data.table", new JsonObject
                {
                    ["entity"] = entityId,
                    ["table"] = table,
                    ["schema"] = schema ?? (dbContext is null ? null : _defaultSchema.GetValueOrDefault(dbContext)),
                    ["store"] = "efcore",
                    ["dbContext"] = dbContext,
                    ["tableSource"] = "fluent",
                    ["resolution"] = resolution,
                }, context.Evidence(call.Syntax));
            }
        }
    }

    public delegate SemanticModel SemanticModelProvider(SyntaxTree tree);

    private static ITypeSymbol? EntityOf(ITypeSymbol? builder) =>
        builder is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
        && AdapterContext.FullName(named.OriginalDefinition) == "Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder"
            ? named.TypeArguments[0] : null;
}
