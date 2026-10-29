using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Topology (4.3): Aspire AppHost resources (Add* on the distributed application builder or on a resource builder,
/// with their name, resource type and, for projects, the project path from the generated project metadata) and
/// WithReference relationships between resources.
/// </summary>
public sealed class TopologyAdapter(AdapterContext context)
{
    private const string ResourceBuilder = "Aspire.Hosting.ApplicationModel.IResourceBuilder";

    public void VisitInvocation(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.Name == "WithReference" && AdapterContext.FullName(method.ContainingType) == "Aspire.Hosting.ResourceBuilderExtensions")
        {
            var from = Resource(AdapterContext.Receiver(invocation), 0);
            var source = invocation.Arguments.Skip(1).FirstOrDefault() is { } a ? Resource(AdapterContext.Unwrap(a.Value), 0) : null;
            if (from is not null && source is not null)
            {
                context.Observe("topology.reference", new JsonObject { ["from"] = from, ["to"] = source }, context.Evidence(invocation.Syntax));
            }
            return;
        }
        if (!IsResourceCreation(invocation, out var name, out var resourceType))
        {
            return;
        }
        var parent = AdapterContext.Receiver(invocation) is { } receiver && AdapterContext.FullName(receiver.Type) == ResourceBuilder ? Resource(receiver, 0) : null;
        context.Observe("topology.resource", new JsonObject
        {
            ["name"] = name,
            ["resourceType"] = resourceType,
            ["targetProject"] = ProjectPath(method) ?? ProjectFromPathArgument(invocation),
            ["parent"] = parent,
        }, context.Evidence(invocation.Syntax));
    }

    private static bool IsResourceCreation(IInvocationOperation invocation, out string? name, out string? resourceType)
    {
        name = null;
        resourceType = null;
        var method = invocation.TargetMethod;
        if (!method.Name.StartsWith("Add", StringComparison.Ordinal) || method.ReturnType is not INamedTypeSymbol { IsGenericType: true } returned
            || AdapterContext.FullName(returned) != ResourceBuilder)
        {
            return false;
        }
        var receiverType = AdapterContext.Receiver(invocation)?.Type;
        if (!AdapterContext.Implements(receiverType, "Aspire.Hosting.IDistributedApplicationBuilder") && AdapterContext.FullName(receiverType) != ResourceBuilder)
        {
            return false;
        }
        name = AdapterContext.Argument(invocation, "name")?.ConstantValue is { HasValue: true, Value: string n } ? n : null;
        resourceType = returned.TypeArguments[0].Name;
        return name is not null;
    }

    /// <summary>The resource name an expression stands for: its Add* call, through locals and fluent calls.</summary>
    private string? Resource(IOperation? operation, int depth)
    {
        while (operation is not null && depth++ < 32)
        {
            switch (operation)
            {
                case ILocalReferenceOperation local:
                    operation = context.LocalValue(local.Local);
                    continue;
                case IInvocationOperation call when IsResourceCreation(call, out var name, out _):
                    return name;
                case IInvocationOperation call:
                    operation = AdapterContext.Receiver(call);
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// For resource methods that take a project path or directory as a string (for example AddDotnetProject("name", "../Api")):
    /// a constant argument that names a C# project of this repository, relative to the AppHost project.
    /// </summary>
    private string? ProjectFromPathArgument(IInvocationOperation invocation)
    {
        var projects = context.RepositoryProjects;
        foreach (var argument in invocation.Arguments.Where(a => a.Parameter?.Name != "name"))
        {
            if (AdapterContext.Unwrap(argument.Value).ConstantValue is not { HasValue: true, Value: string value } || value.Length == 0)
            {
                continue;
            }
            string candidate;
            try
            {
                candidate = DotnetIntelligence.Analysis.SolutionDiscovery.ResolveRelative(context.Project.ProjectPath, value);
            }
            catch (FormatException)
            {
                continue;
            }
            if (projects.Contains(candidate))
            {
                return candidate;
            }
            var inDirectory = projects.Where(p => Path.GetDirectoryName(p)!.Replace('\\', '/') == candidate.TrimEnd('/')).ToList();
            if (inDirectory.Count == 1)
            {
                return inDirectory[0];
            }
        }
        return null;
    }

    /// <summary>For AddProject&lt;TProject&gt;: the ProjectPath constant of the generated project metadata type, repository-relative.</summary>
    private string? ProjectPath(IMethodSymbol method)
    {
        if (method.Name != "AddProject" || method.TypeArguments.FirstOrDefault() is not INamedTypeSymbol metadata)
        {
            return null;
        }
        var property = metadata.GetMembers("ProjectPath").OfType<IPropertySymbol>().FirstOrDefault();
        foreach (var reference in property?.DeclaringSyntaxReferences ?? [])
        {
            var literal = reference.GetSyntax().DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault();
            if (literal?.Token.Value is string path)
            {
                return context.Files.Relative(Path.GetFullPath(path.Replace('\\', '/')));
            }
        }
        return null;
    }
}
