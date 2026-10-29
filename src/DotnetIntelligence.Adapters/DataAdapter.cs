using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;

namespace DotnetIntelligence.Adapters;

/// <summary>Data (4.3, minimal): DbContext types and their DbSet&lt;T&gt; properties as data.maps_to. No read or write claims.</summary>
public sealed class DataAdapter(AdapterContext context)
{
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
                context.Observe("data.maps_to", new JsonObject
                {
                    ["dbContext"] = contextId,
                    ["entity"] = entityId,
                    ["property"] = context.IdOf(property),
                }, context.Evidence(location));
            }
        }
    }
}
