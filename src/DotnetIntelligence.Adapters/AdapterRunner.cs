using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Extraction;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>Runs every adapter (4.3) over each semantically loaded project, after extraction.</summary>
public static class AdapterRunner
{
    public static SuggestionCollector Run(
        IReadOnlyList<(LoadedProject Project, string ContextId)> projects,
        GraphBuilder graph,
        FileMap files,
        SymbolIdentity identity,
        IReadOnlyList<WorkingCopyFile> repositoryFiles,
        Conventions conventions,
        CancellationToken cancellationToken)
    {
        var calls = new CallGraph(graph.Relations.Where(r => (string)r["kind"]! == "calls_declared_target")
            .Select(r => ((string)r["from"]!, (string)r["to"]!)));
        var suggestions = new SuggestionCollector();
        var repositoryProjects = repositoryFiles.Select(f => f.Path).Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        foreach (var (project, contextId) in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new AdapterContext(project, contextId, graph, files, identity) { RepositoryProjects = repositoryProjects };
            var di = new DependencyInjectionAdapter(context, calls);
            var web = new AspNetCoreAdapter(context);
            var http = new HttpClientAdapter(context);
            var grpc = new GrpcAdapter(context);
            var messaging = new MessagingAdapter(context, conventions, suggestions);
            var topology = new TopologyAdapter(context);
            var data = new DataAdapter(context);

            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                var model = context.Compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(node, cancellationToken) is not IInvocationOperation invocation)
                    {
                        continue;
                    }
                    di.VisitInvocation(invocation, model);
                    web.VisitInvocation(invocation);
                    http.VisitInvocation(invocation, model);
                    grpc.VisitInvocation(invocation, model);
                    messaging.VisitInvocation(invocation, model);
                    topology.VisitInvocation(invocation);
                }
            }
            foreach (var type in SourceTypes(context.Compilation.Assembly.GlobalNamespace))
            {
                di.VisitType(type);
                web.VisitType(type);
                grpc.VisitType(type);
                messaging.VisitType(type);
                data.VisitType(type);
            }
            web.Complete();
            grpc.VisitProtos(repositoryFiles);
            new ConfigurationAdapter(context).Visit(repositoryFiles);
        }
        return suggestions;
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var t in SourceTypes(child)) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var t in WithNested(type)) yield return t;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        if (type.Locations.Any(l => l.IsInSource)) yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var t in WithNested(nested)) yield return t;
        }
    }
}
