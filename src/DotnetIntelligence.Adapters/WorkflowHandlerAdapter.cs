using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Workflow handlers (contract v1.1, workflow.handler) from the caller's conventions: classes implementing a configured
/// plugin interface, with the node (or flow event) name taken from an attribute, a constant property, a type argument
/// or the type name. Names read from code are exact; names derived from the type name are candidates. The linker joins
/// handlers to BPMN nodes with dispatches_to links (via workflow).
/// </summary>
public sealed class WorkflowHandlerAdapter(AdapterContext context)
{
    public void VisitType(INamedTypeSymbol type)
    {
        if (context.Conventions.Workflow.Handlers.Count == 0 || type.TypeKind != TypeKind.Class || type.IsAbstract
            || type.Locations.FirstOrDefault(l => l.IsInSource) is not { } location || context.IdOf(type) is not { } handlerId)
        {
            return;
        }
        foreach (var convention in context.Conventions.Workflow.Handlers)
        {
            var @interface = type.AllInterfaces.FirstOrDefault(i => AdapterContext.FullName(i.OriginalDefinition) == convention.Interface);
            if (@interface is null)
            {
                continue;
            }
            var evidence = new List<string?> { context.Evidence(location, "configured-convention") };
            var (name, exact) = Name(type, @interface, convention.Name, evidence);
            var (process, _) = convention.Process is null ? ("*", true) : Name(type, @interface, convention.Process, evidence);
            var isEvent = convention.Role == "event";
            context.Observe("workflow.handler", new JsonObject
            {
                ["processId"] = process ?? "*",
                [isEvent ? "eventName" : "nodeName"] = name,
                ["handler"] = handlerId,
                ["role"] = convention.Role,
                ["resolution"] = name is null ? "unresolved" : exact ? "exact" : "candidate",
                ["ext.dotnet.interface"] = convention.Interface,
            }, [.. evidence]);
        }
    }

    private (string? Name, bool Exact) Name(INamedTypeSymbol type, INamedTypeSymbol @interface, NameRule rule, List<string?> evidence)
    {
        if (rule.Attribute is { } attributeName
            && type.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == attributeName) is { } attribute
            && rule.AttributeArgument < attribute.ConstructorArguments.Length && attribute.ConstructorArguments[rule.AttributeArgument].Value is string fromAttribute)
        {
            if (attribute.ApplicationSyntaxReference is { } syntax)
            {
                evidence.Add(context.Evidence(syntax.GetSyntax(), "configured-convention"));
            }
            return (fromAttribute, true);
        }
        if (rule.Property is { } propertyName && PropertyValue(type, propertyName, evidence) is { } fromProperty)
        {
            return (fromProperty, true);
        }
        if (rule.TypeArgument is { } index && index < @interface.TypeArguments.Length)
        {
            return (@interface.TypeArguments[index].Name, true);
        }
        if (rule.TypeName)
        {
            var name = type.Name;
            foreach (var suffix in rule.RemoveSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                {
                    name = name[..^suffix.Length];
                    break;
                }
            }
            return (name, false);
        }
        return (null, false);
    }

    /// <summary>A constant string from "Name =&gt; "x"", "get { return "x"; }" or "{ get; } = "x"" in the type or its source base types.</summary>
    private string? PropertyValue(INamedTypeSymbol type, string propertyName, List<string?> evidence)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers(propertyName).OfType<IPropertySymbol>())
            {
                foreach (var syntax in property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>()
                             .Where(s => context.Compilation.ContainsSyntaxTree(s.SyntaxTree)))
                {
                    var model = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                    ExpressionSyntax? expression = syntax.ExpressionBody?.Expression ?? syntax.Initializer?.Value
                        ?? syntax.AccessorList?.Accessors.FirstOrDefault(a => a.Keyword.ValueText == "get") switch
                        {
                            { ExpressionBody.Expression: { } body } => body,
                            { Body.Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
                            _ => null,
                        };
                    if (expression is not null && model.GetOperation(expression) is IOperation operation
                        && context.StringTemplate(operation) is { Template: { } value, Constant: true })
                    {
                        evidence.Add(context.Evidence(expression, "configured-convention"));
                        return value;
                    }
                }
            }
        }
        return null;
    }
}
